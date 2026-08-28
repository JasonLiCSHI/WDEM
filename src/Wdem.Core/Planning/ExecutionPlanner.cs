using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Planning;

public sealed partial class ExecutionPlanner(IResourceProviderRegistry providers) : IExecutionPlanner
{
  public const int MaxResourceCount = 10_000;
  public const int MaxStepsPerResource = 1_000;
  public const int MaxTotalStepCount = 100_000;
  public const int MaxTextFieldByteCount = 4_096;
  public const int MaxDiagnosticsPerResource = 100;
  public const int MaxTotalProviderTextByteCount = 4 * 1024 * 1024;

  private const int MaxStepIdLength = 128;

  private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;
  private readonly IResourceProviderRegistry _providers =
      providers ?? throw new ArgumentNullException(nameof(providers));

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

    var graphError = ValidateGraph(graph);
    if (graphError is not null || graph.Nodes.Count > MaxResourceCount)
    {
      var error = graphError ?? new StructuredError(
          WdemErrorCode.DependencyError,
          "The execution plan is too large.",
          $"The graph contains {graph.Nodes.Count} resources; the limit is {MaxResourceCount}.")
      {
        SuggestedAction = "Reduce the selected resource set and create a new plan."
      };
      return CreatePlan(profileId, profileVersion, [], [], [error]);
    }

    var errors = new List<StructuredError>();
    var planned = new List<PlannedResource>(graph.Nodes.Count);
    var plannedById = new Dictionary<string, PlannedResource>(IdComparer);
    var totalSteps = 0;
    var totalProviderTextBytes = 0;
    var canonicalLayers = CanonicalizeLayers(graph.TopologicalLayers);

    foreach (var layer in canonicalLayers)
    {
      foreach (var resourceId in layer.ResourceIds)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = graph.Nodes[resourceId];
        var item = await PlanResourceAsync(
            resolved,
            detectedStates,
            cancellationToken).ConfigureAwait(false);

        var blockedBy = item.Dependencies
            .Where(dependencyId => plannedById.TryGetValue(dependencyId, out var dependency) &&
                dependency.Status is not PlannedResourceStatus.Ready and
                    not PlannedResourceStatus.AlreadySatisfied)
            .Order(IdComparer)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (blockedBy.Length > 0)
        {
          var blockedError = new StructuredError(
              WdemErrorCode.DependencyError,
              "The resource is blocked by a dependency.",
              $"Resource '{resolved.Definition.Id}' is blocked by: {string.Join(", ", blockedBy)}.")
          {
            ResourceId = resolved.Definition.Id,
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

  private async Task<PlannedResource> PlanResourceAsync(
      ResolvedResource resolved,
      IReadOnlyDictionary<string, DetectedState> detectedStates,
      CancellationToken cancellationToken)
  {
    var definition = SnapshotDefinition(resolved.Definition);
    if (!_providers.TryGet(definition.Type, definition.Provider, out var provider) ||
        provider is null)
    {
      return Failure(
          resolved,
          definition,
          ProviderError(
              definition.Id,
              "No provider is available for the resource.",
              $"Provider '{definition.Provider}' is not registered for type '{definition.Type}'."));
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
              $"No detected state was supplied for resource '{definition.Id}'.")
          {
            ResourceId = definition.Id,
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
              $"State for resource '{SanitizeVisible(detectedState.ResourceId)}' cannot plan resource '{definition.Id}'.")
          {
            ResourceId = definition.Id,
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
          $"Provider '{definition.Provider}' returned a null validation result."));
    }

    if (!validation.IsValid)
    {
      IReadOnlyList<StructuredError> diagnostics;
      if (validation.StructuredErrors.Count > 0)
      {
        if (!TryNormalizeDiagnostics(
                definition,
                validation.StructuredErrors,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                out diagnostics,
                out var diagnosticError))
        {
          return Failure(resolved, definition, diagnosticError!);
        }
      }
      else if (validation.Errors.Count > MaxDiagnosticsPerResource)
      {
        return Failure(resolved, definition, ProviderError(
            definition.Id,
            "Provider validation returned too many errors.",
            $"The diagnostic limit is {MaxDiagnosticsPerResource} per resource."));
      }
      else
      {
        diagnostics = ReadOnly(validation.Errors.Select(error => ProviderError(
            definition.Id,
            "Provider validation rejected the resource.",
            SanitizeVisible(error))));
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
          $"Provider '{definition.Provider}' returned a null resource plan."));
    }

    var planError = ValidateProviderPlan(definition, detectedState, resourcePlan);
    if (planError is not null)
    {
      var failureStatus = detectedState.Outcome switch
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

    if (resourcePlan.Error is not null || normalizedDiagnostics.Count > 0)
    {
      var diagnostics = normalizedDiagnostics.ToList();
      if (resourcePlan.Error is not null)
      {
        diagnostics.Add(ProviderError(
            definition.Id,
            "Provider planning reported an error.",
            SanitizeVisible(resourcePlan.Error)));
      }

      if (resourcePlan.IsExecutable)
      {
        diagnostics.Add(ProviderError(
            definition.Id,
            "Provider returned errors with an executable plan.",
            "A resource plan with errors or diagnostics cannot be executable."));
      }

      return Failure(
          resolved,
          definition,
          diagnostics,
          PlannedResourceStatus.Invalid);
    }

    var plan = SnapshotPlan(resourcePlan, normalizedDiagnostics);

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
      IReadOnlyList<StructuredError> diagnostics = detectedDiagnostics.Count > 0
          ? ReadOnly(detectedDiagnostics)
          : status is PlannedResourceStatus.Unsupported or PlannedResourceStatus.DetectionFailed
              ? [DetectionStateError(
                  definition.Id,
                  detectedState.Outcome,
                  $"Resource '{definition.Id}' has detection outcome '{detectedState.Outcome}'.")]
              : [ProviderError(
                  definition.Id,
                  "The provider cannot produce an executable plan.",
                  $"Resource '{definition.Id}' has compliance status '{plan.Compliance}'.")];
      item = item with { Diagnostics = ReadOnly(diagnostics) };
    }

    return item;
  }

  private static PlannedResource CreatePlannedResource(
      ResolvedResource resolved,
      ResourceDefinition definition,
      ResourcePlan plan)
  {
    var modifyingSteps = plan.Steps
        .Where(step => step.Action != PlanAction.None)
        .ToArray();
    var requiresElevation = modifyingSteps.Any(
        step => step.PrivilegeRequirement == PrivilegeRequirement.Administrator);
    var isDestructive = modifyingSteps.Any(step => step.IsDestructive);
    var restartPolicy = modifyingSteps
        .Select(step => step.RestartPolicy)
        .Append(RestartPolicy.NoRestart)
        .Max();
    var reason = modifyingSteps
        .Select(step => step.Reason)
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    var risk = isDestructive
        ? PlanRisk.Destructive
        : requiresElevation
            ? PlanRisk.Elevated
            : plan.RequiresApply ? PlanRisk.Standard : PlanRisk.None;

    return new PlannedResource
    {
      Definition = RedactDefinition(definition),
      Origin = resolved.Origin,
      Dependencies = ReadOnly(definition.Dependencies),
      ResourcePlan = plan,
      Status = PlannedResourceStatus.Ready,
      Risk = risk,
      RequiresElevation = requiresElevation,
      IsDestructive = isDestructive,
      RestartPolicy = restartPolicy,
      Reason = reason,
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

  private static ResourceDefinition SnapshotDefinition(ResourceDefinition definition) =>
      definition with
      {
        Dependencies = ReadOnly(definition.Dependencies
            .Order(IdComparer)
            .ThenBy(id => id, StringComparer.Ordinal)),
        Parameters = definition.Parameters.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase)
      };

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

  private static ExecutionPlan CreatePlan(
      string profileId,
      string profileVersion,
      IReadOnlyList<ResourceGraphLayer> layers,
      IReadOnlyList<PlannedResource> resources,
      IReadOnlyList<StructuredError> errors)
  {
    var executable = layers.Count > 0 && resources.Count > 0 && errors.Count == 0 &&
        resources.All(resource => resource.Status is PlannedResourceStatus.Ready or
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
      foreach (var dependency in resource.Dependencies)
      {
        Append(canonical, dependency);
      }
      foreach (var blockedBy in resource.BlockedBy)
      {
        Append(canonical, blockedBy);
      }
      Append(canonical, resource.ResourcePlan.ResourceId);
      Append(canonical, resource.ResourcePlan.ResourceType);
      Append(canonical, resource.ResourcePlan.ProviderName);
      Append(canonical, resource.ResourcePlan.DesiredStateFingerprint);
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
      Append(canonical, error.Code.ToString());
      Append(canonical, error.ResourceId);
      Append(canonical, error.StepId);
      Append(canonical, error.Summary);
      Append(canonical, error.Detail);
    }

    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
  }

  private static Guid CreatePlanId(string fingerprint)
  {
    Span<byte> bytes = stackalloc byte[16];
    Convert.FromHexString(fingerprint).AsSpan(0, bytes.Length).CopyTo(bytes);
    return new Guid(bytes);
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
