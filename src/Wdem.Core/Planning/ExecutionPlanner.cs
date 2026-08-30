using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Planning;

public sealed partial class ExecutionPlanner(
    IResourceProviderRegistry providers,
    IComplianceEvaluator complianceEvaluator) : IExecutionPlanner
{
  public const int MaxResourceCount = 10_000;
  public const int MaxStepsPerResource = 1_000;
  public const int MaxTotalStepCount = 100_000;
  public const int MaxTextFieldByteCount = 4_096;
  public const int MaxDiagnosticsPerResource = 100;
  public const int MaxTotalProviderTextByteCount = 4 * 1024 * 1024;
  public const int MaxExternalCollectionCount = 10_000;
  public const int MaxTotalExternalTextByteCount = 4 * 1024 * 1024;

  private const int MaxStepIdLength = 128;
  private const int MaxResourceIdLength = 128;

  private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;
  private readonly IResourceProviderRegistry _providers =
      providers ?? throw new ArgumentNullException(nameof(providers));
  private readonly IComplianceEvaluator _complianceEvaluator =
      complianceEvaluator ?? throw new ArgumentNullException(nameof(complianceEvaluator));

  public async Task<ExecutionPlan> CreateAsync(
      ResourceGraph graph,
      IReadOnlyDictionary<string, DetectedState> detectedStates,
      string profileId,
      string profileVersion,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(graph);
    ArgumentNullException.ThrowIfNull(detectedStates);
    ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
    ArgumentException.ThrowIfNullOrWhiteSpace(profileVersion);
    cancellationToken.ThrowIfCancellationRequested();

    if (Utf8ByteCount(profileId) > MaxTextFieldByteCount ||
        Utf8ByteCount(profileVersion) > MaxTextFieldByteCount)
    {
      throw new ArgumentException("Profile identity fields exceed the execution-plan size limit.");
    }

    var totalExternalTextBytes = 0;
    ResourceGraph graphSnapshot;
    StructuredError? graphSnapshotError;
    try
    {
      if (!TrySnapshotGraph(
              graph,
              ref totalExternalTextBytes,
              out graphSnapshot,
              out graphSnapshotError))
      {
        return CreatePlan(profileId, profileVersion, [], [], [graphSnapshotError!]);
      }
    }
    catch (Exception exception)
    {
      return CreatePlan(
          profileId,
          profileVersion,
          [],
          [],
          [BoundaryException(
              WdemErrorCode.ProfileError,
              "The resource graph could not be safely snapshotted.",
              exception)]);
    }

    IReadOnlyDictionary<string, DetectedState> detectedStateSnapshot;
    StructuredError? detectedStateCollectionError;
    try
    {
      if (!TrySnapshotDetectedStates(
              detectedStates,
              ref totalExternalTextBytes,
              out detectedStateSnapshot,
              out detectedStateCollectionError))
      {
        return CreatePlan(
            profileId,
            profileVersion,
            [],
            [],
            [detectedStateCollectionError!]);
      }
    }
    catch (Exception exception)
    {
      return CreatePlan(
          profileId,
          profileVersion,
          [],
          [],
          [BoundaryException(
              WdemErrorCode.DetectionError,
              "The detected-state collection could not be safely snapshotted.",
              exception)]);
    }

    var graphError = ValidateGraph(graphSnapshot);
    if (graphError is not null)
    {
      return CreatePlan(profileId, profileVersion, [], [], [graphError]);
    }

    var errors = new List<StructuredError>();
    var planned = new List<PlannedResource>(graphSnapshot.Nodes.Count);
    var plannedById = new Dictionary<string, PlannedResource>(IdComparer);
    var totalSteps = 0;
    var totalProviderTextBytes = 0;
    var canonicalLayers = CanonicalizeLayers(graphSnapshot.TopologicalLayers);

    foreach (var layer in canonicalLayers)
    {
      foreach (var resourceId in layer.ResourceIds)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = graphSnapshot.Nodes[resourceId];
        var item = await PlanResourceAsync(
            resolved,
            detectedStateSnapshot,
            cancellationToken).ConfigureAwait(false);

        if (CanDeferUntilDependenciesComplete(item, plannedById))
        {
          var authorization = CreateDeferredAuthorization(item);
          var expectedAction = authorization.AllowedActions.Single();
          item = item with
          {
            Status = PlannedResourceStatus.Deferred,
            Risk = authorization.MaximumRisk,
            RequiresElevation = authorization.MaximumPrivilege ==
                PrivilegeRequirement.Administrator,
            IsDestructive = authorization.AllowDestructive,
            RestartPolicy = authorization.MaximumRestartPolicy,
            Reason = authorization.DynamicPlanNotice,
            DeferredAuthorization = authorization,
            Diagnostics = ReadOnly(Array.Empty<StructuredError>()),
            ResourcePlan = item.ResourcePlan with
            {
              Error = null,
              StructuredErrors = ReadOnly(Array.Empty<StructuredError>()),
              Steps =
              [
                new PlanStep
                {
                  Id = "deferred-refinement",
                  Description = $"Authorize deferred {expectedAction.ToString().ToLowerInvariant()} after dependency re-detection.",
                  Action = expectedAction,
                  PrivilegeRequirement = authorization.MaximumPrivilege,
                  RestartPolicy = authorization.MaximumRestartPolicy,
                  IsDestructive = authorization.AllowDestructive,
                  Reason = authorization.DynamicPlanNotice
                }
              ]
            }
          };
        }

        var blockedBy = item.Dependencies
            .Where(dependencyId => plannedById.TryGetValue(dependencyId, out var dependency) &&
                dependency.Status is not PlannedResourceStatus.Ready and
                    not PlannedResourceStatus.Deferred and
                    not PlannedResourceStatus.AlreadySatisfied)
            .Order(IdComparer)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (blockedBy.Length > 0)
        {
          var blockedError = new StructuredError(
              WdemErrorCode.DependencyError,
              "The resource is blocked by a dependency.",
              SanitizeVisible(
                  $"Resource '{SanitizeVisible(resolved.Definition.Id)}' is blocked by: " +
                  $"{string.Join(", ", blockedBy.Select(SanitizeVisible))}."))
          {
            ResourceId = NormalizeResourceId(resolved.Definition.Id),
            SuggestedAction = "Resolve the failed dependencies and create a new plan."
          };
          item = item with
          {
            Status = PlannedResourceStatus.Blocked,
            BlockedBy = ReadOnly(blockedBy),
            Diagnostics = ReadOnly(item.Diagnostics.Append(blockedError))
          };
          errors.Add(blockedError);
        }

        totalSteps += item.ResourcePlan.Steps.Count;
        if (totalSteps > MaxTotalStepCount)
        {
          var sizeError = ProviderError(
              resolved.Definition.Id,
              "The execution plan contains too many steps.",
              $"The plan exceeds the total limit of {MaxTotalStepCount} steps.");
          return CreatePlan(profileId, profileVersion, [], [], [sizeError]);
        }

        totalProviderTextBytes += MeasureProviderText(item);
        if (totalProviderTextBytes > MaxTotalProviderTextByteCount)
        {
          var sizeError = ProviderError(
              resolved.Definition.Id,
              "The execution plan contains too much provider text.",
              $"Provider text exceeds the {MaxTotalProviderTextByteCount}-byte plan limit.");
          return CreatePlan(profileId, profileVersion, [], [], [sizeError]);
        }

        errors.AddRange(item.Diagnostics.Where(error => !errors.Contains(error)));
        planned.Add(item);
        plannedById.Add(resolved.Definition.Id, item);
      }
    }

    return CreatePlan(
        profileId,
        profileVersion,
        canonicalLayers,
        ReadOnly(planned),
        ReadOnly(errors));
  }

  public async Task<PlannedResource> ReplanResourceAsync(
      ResolvedResource resource,
      DetectedState detectedState,
      string approvedDefinitionFingerprint,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(detectedState);
    ArgumentException.ThrowIfNullOrWhiteSpace(approvedDefinitionFingerprint);
    cancellationToken.ThrowIfCancellationRequested();

    var externalTextBytes = 0;
    var sourceGraph = new ResourceGraph(
        new Dictionary<string, ResolvedResource>(IdComparer)
        {
          [resource.Definition.Id] = resource
        },
        [new ResourceGraphLayer(0, [resource.Definition.Id])]);
    if (!TrySnapshotGraph(
            sourceGraph,
            ref externalTextBytes,
            out var graphSnapshot,
            out var graphError))
    {
      throw new ArgumentException(graphError!.Detail, nameof(resource));
    }

    if (!TrySnapshotDetectedStates(
            new Dictionary<string, DetectedState>(IdComparer)
            {
              [detectedState.ResourceId] = detectedState
            },
            ref externalTextBytes,
            out var statesSnapshot,
            out var stateError))
    {
      throw new ArgumentException(stateError!.Detail, nameof(detectedState));
    }

    var snapshot = graphSnapshot.Nodes[resource.Definition.Id];
    var actualFingerprint = ResourceDefinitionFingerprint.Create(snapshot.Definition);
    if (!string.Equals(
            actualFingerprint,
            approvedDefinitionFingerprint,
            StringComparison.Ordinal))
    {
      return Failure(
          snapshot,
          snapshot.Definition,
          new StructuredError(
              WdemErrorCode.ConfigurationError,
              "The deferred resource definition changed after plan approval.",
              "The resource must be reviewed again before it can be replanned.")
          {
            ResourceId = NormalizeResourceId(snapshot.Definition.Id),
            SuggestedAction = "Create and approve a new execution plan."
          });
    }

    return await PlanResourceAsync(snapshot, statesSnapshot, cancellationToken)
        .ConfigureAwait(false);
  }

  private async Task<PlannedResource> PlanResourceAsync(
      ResolvedResource resolved,
      IReadOnlyDictionary<string, DetectedState> detectedStates,
      CancellationToken cancellationToken)
  {
    var definition = resolved.Definition;
    if (!_providers.TryGet(definition.Type, definition.Provider, out var provider) ||
        provider is null)
    {
      return Failure(
          resolved,
          definition,
          ProviderError(
              definition.Id,
              "No provider is available for the resource.",
              $"Provider '{SanitizeVisible(definition.Provider)}' is not registered for type " +
              $"'{SanitizeVisible(definition.Type)}'."));
    }

    if (!detectedStates.TryGetValue(definition.Id, out var detectedState) ||
        detectedState is null)
    {
      return Failure(
          resolved,
          definition,
          new StructuredError(
              WdemErrorCode.DetectionError,
              "Detected state is missing.",
              SanitizeVisible(
                  $"No detected state was supplied for resource " +
                  $"'{SanitizeVisible(definition.Id)}'."))
          {
            ResourceId = NormalizeResourceId(definition.Id),
            SuggestedAction = "Detect the resource again before creating a plan."
          },
          PlannedResourceStatus.DetectionFailed);
    }

    if (!IdComparer.Equals(detectedState.ResourceId, definition.Id))
    {
      return Failure(
          resolved,
          definition,
          new StructuredError(
              WdemErrorCode.DetectionError,
              "Detected state does not match the resource.",
              SanitizeVisible(
                  $"State for resource '{SanitizeVisible(detectedState.ResourceId)}' cannot plan " +
                  $"resource '{SanitizeVisible(definition.Id)}'."))
          {
            ResourceId = NormalizeResourceId(definition.Id),
            SuggestedAction = "Detect the resource again before creating a plan."
          },
          PlannedResourceStatus.DetectionFailed);
    }

    if (!Enum.IsDefined(detectedState.Outcome))
    {
      return Failure(resolved, definition, ProviderError(
          definition.Id,
          "Detected state contains an unknown outcome.",
          "The provider returned an undefined detection outcome."));
    }

    if (detectedState.Outcome == DetectionOutcome.Succeeded &&
        (detectedState.Error is not null || detectedState.StructuredError is not null))
    {
      return Failure(resolved, definition, ProviderError(
          definition.Id,
          "Successful detection contains an error.",
          "A successful detected state cannot also contain error diagnostics."));
    }

    var detectedDiagnostics = new List<StructuredError>();
    if (detectedState.StructuredError is not null)
    {
      if (!TryNormalizeDiagnostics(
              definition,
              [detectedState.StructuredError],
              new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
              out var normalizedDetectedDiagnostics,
              out var detectedDiagnosticError))
      {
        return Failure(resolved, definition, detectedDiagnosticError!);
      }

      detectedDiagnostics.AddRange(normalizedDetectedDiagnostics);
    }

    if (detectedState.Error is not null)
    {
      detectedDiagnostics.Add(DetectionStateError(
          definition.Id,
          detectedState.Outcome,
          detectedState.Error));
    }

    ComplianceResult compliance;
    try
    {
      compliance = _complianceEvaluator.Evaluate(definition, detectedState);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return Failure(resolved, definition, ProviderException(
          definition.Id,
          "Compliance evaluation failed.",
          exception));
    }

    if (compliance is null || !Enum.IsDefined(compliance.Status))
    {
      return Failure(resolved, definition, ProviderError(
          definition.Id,
          "Compliance evaluation returned an invalid result.",
          "The compliance evaluator must return a defined compliance status."));
    }

    IReadOnlyList<StructuredError> complianceDiagnostics = [];
    if (compliance.Error is not null)
    {
      if (!TryNormalizeDiagnostics(
              definition,
              [compliance.Error],
              new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
              out complianceDiagnostics,
              out var complianceDiagnosticError))
      {
        return Failure(resolved, definition, complianceDiagnosticError!);
      }
    }

    ProviderValidationResult validation;
    try
    {
      validation = await provider.ValidateAsync(definition, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return Failure(resolved, definition, ProviderException(
          definition.Id,
          "Provider validation failed.",
          exception));
    }

    if (validation is null)
    {
      return Failure(resolved, definition, ProviderError(
          definition.Id,
          "Provider validation returned no result.",
          $"Provider '{SanitizeVisible(definition.Provider)}' returned a null validation result."));
    }

    if (!validation.IsValid)
    {
      if (!TryNormalizeValidationDiagnostics(
              definition,
              validation,
              out var diagnostics,
              out var diagnosticError))
      {
        return Failure(resolved, definition, diagnosticError!);
      }

      return Failure(resolved, definition, diagnostics, PlannedResourceStatus.Invalid);
    }

    ResourcePlan resourcePlan;
    try
    {
      resourcePlan = await provider.PlanAsync(
          definition,
          detectedState,
          cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return Failure(resolved, definition, ProviderException(
          definition.Id,
          "Provider planning failed.",
          exception));
    }

    if (resourcePlan is null)
    {
      return Failure(resolved, definition, ProviderError(
          definition.Id,
          "Provider planning returned no result.",
          $"Provider '{SanitizeVisible(definition.Provider)}' returned a null resource plan."));
    }

    var planError = ValidateProviderPlan(
        definition,
        compliance.Status,
        resourcePlan);
    if (planError is not null)
    {
      var hasReportedErrors = resourcePlan.Error is not null ||
          resourcePlan.StructuredErrors.Count > 0;
      var failureStatus = resourcePlan.IsExecutable && hasReportedErrors
          ? PlannedResourceStatus.Invalid
          : detectedState.Outcome switch
          {
            DetectionOutcome.Failed or DetectionOutcome.Cancelled =>
                PlannedResourceStatus.DetectionFailed,
            DetectionOutcome.Unsupported => PlannedResourceStatus.Unsupported,
            _ => PlannedResourceStatus.Invalid
          };
      return Failure(resolved, definition, planError, failureStatus);
    }

    var stepIds = resourcePlan.Steps.ToDictionary(
        step => step.Id,
        step => step.Id,
        IdComparer);
    if (!TryNormalizeDiagnostics(
            definition,
            resourcePlan.StructuredErrors,
            stepIds,
            out var normalizedDiagnostics,
            out var malformedDiagnostic))
    {
      return Failure(resolved, definition, malformedDiagnostic!);
    }

    var reportedDiagnostics = normalizedDiagnostics.ToList();
    if (resourcePlan.Error is not null)
    {
      var sanitizedError = SanitizeVisible(resourcePlan.Error);
      if (!reportedDiagnostics.Any(
              diagnostic => string.Equals(
                  diagnostic.Detail,
                  sanitizedError,
                  StringComparison.Ordinal)))
      {
        if (reportedDiagnostics.Count >= MaxDiagnosticsPerResource)
        {
          return Failure(resolved, definition, ProviderError(
              definition.Id,
              "Provider planning returned too many diagnostics.",
              $"The diagnostic limit is {MaxDiagnosticsPerResource} per resource."));
        }

        reportedDiagnostics.Add(ProviderError(
            definition.Id,
            "Provider planning reported an error.",
            sanitizedError));
      }
    }

    if (reportedDiagnostics.Count > 0 &&
        (resourcePlan.IsExecutable ||
         resourcePlan.Compliance is not ComplianceStatus.DetectionFailed and
             not ComplianceStatus.Unsupported))
    {
      var dependencyDeferredCandidate = !resourcePlan.IsExecutable &&
          resourcePlan.Compliance is ComplianceStatus.Missing or
              ComplianceStatus.VersionMismatch or ComplianceStatus.ConfigurationMismatch &&
          reportedDiagnostics.All(error => error.Code == WdemErrorCode.DependencyError);
      if (resourcePlan.IsExecutable)
      {
        var malformedError = ProviderError(
            definition.Id,
            "Provider returned errors with an executable plan.",
            "A resource plan with errors or diagnostics cannot be executable.");
        if (reportedDiagnostics.Count < MaxDiagnosticsPerResource)
        {
          reportedDiagnostics.Add(malformedError);
        }
        else
        {
          reportedDiagnostics[^1] = malformedError;
        }
      }

      if (!dependencyDeferredCandidate)
      {
        return Failure(
            resolved,
            definition,
            reportedDiagnostics,
            PlannedResourceStatus.Invalid);
      }
    }

    var planDiagnostics = ReadOnly(reportedDiagnostics);
    var plan = SnapshotPlan(resourcePlan, planDiagnostics);

    var status = plan.Compliance switch
    {
      ComplianceStatus.Satisfied => PlannedResourceStatus.AlreadySatisfied,
      ComplianceStatus.Unsupported => PlannedResourceStatus.Unsupported,
      ComplianceStatus.DetectionFailed => PlannedResourceStatus.DetectionFailed,
      _ => plan.IsExecutable ? PlannedResourceStatus.Ready : PlannedResourceStatus.Invalid
    };
    var item = CreatePlannedResource(resolved, definition, plan) with { Status = status };
    if (status is PlannedResourceStatus.Unsupported or PlannedResourceStatus.DetectionFailed ||
        !plan.IsExecutable)
    {
      IReadOnlyList<StructuredError> diagnostics = planDiagnostics.Count > 0
          ? planDiagnostics
          : detectedDiagnostics.Count > 0
          ? ReadOnly(detectedDiagnostics)
          : complianceDiagnostics.Count > 0
          ? complianceDiagnostics
          : status is PlannedResourceStatus.Unsupported or PlannedResourceStatus.DetectionFailed
              ? [DetectionStateError(
                  definition.Id,
                  detectedState.Outcome,
                  $"Resource '{SanitizeVisible(definition.Id)}' has detection outcome " +
                  $"'{detectedState.Outcome}'.")]
              : [ProviderError(
                  definition.Id,
                  "The provider cannot produce an executable plan.",
                  $"Resource '{SanitizeVisible(definition.Id)}' has compliance status " +
                  $"'{plan.Compliance}'.")];
      item = item with { Diagnostics = ReadOnly(diagnostics) };
    }

    return item;
  }

  private static bool CanDeferUntilDependenciesComplete(
      PlannedResource item,
      IReadOnlyDictionary<string, PlannedResource> plannedById)
  {
    if (item.Status != PlannedResourceStatus.Invalid ||
        item.ResourcePlan.IsExecutable ||
        item.ResourcePlan.Compliance is not (ComplianceStatus.Missing or
            ComplianceStatus.VersionMismatch or ComplianceStatus.ConfigurationMismatch) ||
        item.Diagnostics.Count == 0 ||
        item.Diagnostics.Any(error => error.Code != WdemErrorCode.DependencyError) ||
        item.Dependencies.Count == 0)
    {
      return false;
    }

    var dependencies = item.Dependencies
        .Select(id => plannedById.GetValueOrDefault(id))
        .ToArray();
    return dependencies.All(dependency => dependency?.Status is PlannedResourceStatus.Ready or
            PlannedResourceStatus.Deferred or PlannedResourceStatus.AlreadySatisfied) &&
        dependencies.Any(dependency => dependency?.Status is PlannedResourceStatus.Ready or
            PlannedResourceStatus.Deferred);
  }

  private static DeferredPlanAuthorization CreateDeferredAuthorization(PlannedResource item)
  {
    var action = item.ResourcePlan.Compliance switch
    {
      ComplianceStatus.Missing when item.Definition.Type.EndsWith(
          "settings",
          StringComparison.OrdinalIgnoreCase) => PlanAction.Configure,
      ComplianceStatus.Missing => PlanAction.Install,
      ComplianceStatus.VersionMismatch => PlanAction.Upgrade,
      _ => PlanAction.Configure
    };
    var maximumPrivilege = item.Definition.PrivilegeRequirement;
    return new DeferredPlanAuthorization
    {
      AllowedActions = [action],
      MaximumPrivilege = maximumPrivilege,
      MaximumRestartPolicy = item.Definition.RestartPolicy,
      MaximumRisk = maximumPrivilege == PrivilegeRequirement.Administrator
          ? PlanRisk.Elevated
          : PlanRisk.Standard,
      AllowDestructive = false,
      DynamicPlanNotice =
          "This resource will be re-detected after its declared dependencies succeed; " +
          "the resulting plan must remain within this displayed authorization."
    };
  }

  private static PlannedResource CreatePlannedResource(
      ResolvedResource resolved,
      ResourceDefinition definition,
      ResourcePlan plan)
  {
    var executionSummary = PlanStepAuthorizationPolicy.Summarize(plan.Steps);

    return new PlannedResource
    {
      Definition = RedactDefinition(definition),
      Origin = resolved.Origin,
      Dependencies = ReadOnly(definition.Dependencies),
      ResourcePlan = plan,
      Status = PlannedResourceStatus.Ready,
      Risk = executionSummary.Risk,
      RequiresElevation = executionSummary.RequiresElevation,
      IsDestructive = executionSummary.IsDestructive,
      RestartPolicy = executionSummary.RestartPolicy,
      Reason = executionSummary.Reason,
      BlockedBy = ReadOnly(Array.Empty<string>()),
      Diagnostics = ReadOnly(plan.StructuredErrors)
    };
  }

  private static PlannedResource Failure(
      ResolvedResource resolved,
      ResourceDefinition definition,
      StructuredError error,
      PlannedResourceStatus status = PlannedResourceStatus.Invalid) =>
      Failure(resolved, definition, [error], status);

  private static PlannedResource Failure(
      ResolvedResource resolved,
      ResourceDefinition definition,
      IEnumerable<StructuredError> errors,
      PlannedResourceStatus status)
  {
    var diagnostics = ReadOnly(errors);
    return CreatePlannedResource(
        resolved,
        definition,
        new ResourcePlan
        {
          ResourceId = definition.Id,
          ResourceType = definition.Type,
          ProviderName = definition.Provider,
          DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(definition),
          Compliance = status == PlannedResourceStatus.DetectionFailed
              ? ComplianceStatus.DetectionFailed
              : ComplianceStatus.Unsupported,
          IsExecutable = false,
          Steps = [],
          StructuredErrors = diagnostics
        }) with
    {
      Status = status,
      Diagnostics = diagnostics
    };
  }

  private static ResourceDefinition RedactDefinition(ResourceDefinition definition) =>
      definition with
      {
        Parameters = definition.Parameters.ToFrozenDictionary(
            pair => pair.Key,
            pair => IsSensitiveParameter(pair.Key) ? "[REDACTED]" : pair.Value,
            StringComparer.OrdinalIgnoreCase)
      };

  private static bool IsSensitiveParameter(string key)
  {
    var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .ToLowerInvariant();
    return normalized is "password" or "passwd" or "pwd" or "token" or
        "accesstoken" or "refreshtoken" or "clientsecret" or "apikey" or
        "secret" or "authorization" ||
        normalized.EndsWith("token", StringComparison.Ordinal) ||
        normalized.EndsWith("password", StringComparison.Ordinal) ||
        normalized.EndsWith("secret", StringComparison.Ordinal);
  }

  private static ResourcePlan SnapshotPlan(
      ResourcePlan plan,
      IReadOnlyList<StructuredError> diagnostics) => plan with
      {
        Error = null,
        Steps = ReadOnly(plan.Steps.Select(step => step with
        {
          Description = SanitizeVisible(step.Description),
          Reason = step.Reason is null ? null : SanitizeVisible(step.Reason)
        })),
        StructuredErrors = diagnostics
      };

  private static IReadOnlyList<ResourceGraphLayer> CanonicalizeLayers(
      IEnumerable<ResourceGraphLayer> layers) => ReadOnly(layers.Select(layer =>
          new ResourceGraphLayer(
              layer.Index,
              ReadOnly(layer.ResourceIds
                  .Order(IdComparer)
                  .ThenBy(id => id, StringComparer.Ordinal)))));

  internal static ExecutionPlan CreatePlan(
      string profileId,
      string profileVersion,
      IReadOnlyList<ResourceGraphLayer> layers,
      IReadOnlyList<PlannedResource> resources,
      IReadOnlyList<StructuredError> errors)
  {
    var executable = layers.Count > 0 && resources.Count > 0 && errors.Count == 0 &&
        resources.All(resource => resource.Status is PlannedResourceStatus.Ready or
            PlannedResourceStatus.Deferred or
            PlannedResourceStatus.AlreadySatisfied);
    var fingerprint = CreateFingerprint(profileId, profileVersion, layers, resources, errors);
    return new ExecutionPlan
    {
      PlanId = CreatePlanId(fingerprint),
      Fingerprint = fingerprint,
      ProfileId = profileId,
      ProfileVersion = profileVersion,
      Layers = layers,
      Resources = resources,
      IsExecutable = executable,
      Errors = errors
    };
  }

  private static string CreateFingerprint(
      string profileId,
      string profileVersion,
      IReadOnlyList<ResourceGraphLayer> layers,
      IReadOnlyList<PlannedResource> resources,
      IReadOnlyList<StructuredError> errors)
  {
    var canonical = new StringBuilder();
    Append(canonical, profileId);
    Append(canonical, profileVersion);
    foreach (var layer in layers)
    {
      Append(canonical, layer.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
      foreach (var id in layer.ResourceIds)
      {
        Append(canonical, id);
      }
    }

    foreach (var resource in resources)
    {
      Append(canonical, ResourceDefinitionFingerprint.Create(resource.Definition));
      Append(canonical, resource.Origin.ToString());
      Append(canonical, resource.Status.ToString());
      Append(canonical, resource.Risk.ToString());
      Append(canonical, resource.RequiresElevation.ToString());
      Append(canonical, resource.IsDestructive.ToString());
      Append(canonical, resource.RestartPolicy.ToString());
      Append(canonical, resource.Reason);
      Append(canonical, resource.DeferredAuthorization is null ? null : "deferred");
      if (resource.DeferredAuthorization is { } authorization)
      {
        foreach (var action in authorization.AllowedActions)
        {
          Append(canonical, action.ToString());
        }
        Append(canonical, authorization.MaximumPrivilege.ToString());
        Append(canonical, authorization.MaximumRestartPolicy.ToString());
        Append(canonical, authorization.MaximumRisk.ToString());
        Append(canonical, authorization.AllowDestructive.ToString());
        Append(canonical, authorization.DynamicPlanNotice);
      }
      foreach (var dependency in resource.Dependencies)
      {
        Append(canonical, dependency);
      }
      foreach (var blockedBy in resource.BlockedBy)
      {
        Append(canonical, blockedBy);
      }
      foreach (var diagnostic in resource.Diagnostics)
      {
        AppendDiagnostic(canonical, diagnostic);
      }
      Append(canonical, resource.ResourcePlan.ResourceId);
      Append(canonical, resource.ResourcePlan.ResourceType);
      Append(canonical, resource.ResourcePlan.ProviderName);
      Append(canonical, resource.ResourcePlan.DesiredStateFingerprint);
      Append(canonical, resource.ResourcePlan.ExecutionPreconditionFingerprint);
      Append(canonical, resource.ResourcePlan.Compliance.ToString());
      Append(canonical, resource.ResourcePlan.IsExecutable.ToString());
      Append(canonical, resource.ResourcePlan.Error);
      foreach (var step in resource.ResourcePlan.Steps)
      {
        Append(canonical, step.Id);
        Append(canonical, step.Description);
        Append(canonical, step.Action.ToString());
        Append(canonical, step.PrivilegeRequirement.ToString());
        Append(canonical, step.RestartPolicy.ToString());
        Append(canonical, step.IsDestructive.ToString());
        Append(canonical, step.Reason);
      }
    }

    foreach (var error in errors)
    {
      AppendDiagnostic(canonical, error);
    }

    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
  }

  private static Guid CreatePlanId(string fingerprint)
  {
    Span<byte> bytes = stackalloc byte[16];
    Convert.FromHexString(fingerprint).AsSpan(0, bytes.Length).CopyTo(bytes);
    return new Guid(bytes);
  }

  private static void AppendDiagnostic(StringBuilder canonical, StructuredError error)
  {
    Append(canonical, error.Code.ToString());
    Append(canonical, error.ResourceId);
    Append(canonical, error.StepId);
    Append(canonical, error.Summary);
    Append(canonical, error.Detail);
    Append(canonical, error.ProcessExitCode?.ToString(
        System.Globalization.CultureInfo.InvariantCulture));
    Append(canonical, error.LogLocation);
    Append(canonical, error.SuggestedAction);
    Append(canonical, error.IsRetryable.ToString());
    Append(canonical, error.UnderlyingExceptionType);
    Append(canonical, error.UnderlyingExceptionMessage);
  }

  private static void Append(StringBuilder builder, string? value)
  {
    if (value is null)
    {
      builder.Append("-1:");
      return;
    }

    builder.Append(value.Length).Append(':').Append(value);
  }

  private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
      Array.AsReadOnly(values.ToArray());
}
