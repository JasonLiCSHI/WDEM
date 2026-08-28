using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Planning;

public sealed class ExecutionPlanner(IResourceProviderRegistry providers) : IExecutionPlanner
{
  public const int MaxResourceCount = 10_000;
  public const int MaxStepsPerResource = 1_000;
  public const int MaxTotalStepCount = 100_000;

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

    foreach (var layer in graph.TopologicalLayers)
    {
      foreach (var resourceId in layer.ResourceIds)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = graph.Nodes[resourceId];
        var item = await PlanResourceAsync(
            resolved,
            detectedStates,
            cancellationToken).ConfigureAwait(false);

        var blockedBy = resolved.Definition.Dependencies
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
          item = Invalid(item, sizeError);
        }

        errors.AddRange(item.Diagnostics.Where(error => !errors.Contains(error)));
        planned.Add(item);
        plannedById.Add(resolved.Definition.Id, item);
      }
    }

    return CreatePlan(
        profileId,
        profileVersion,
        SnapshotLayers(graph.TopologicalLayers),
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
      var diagnostics = validation.StructuredErrors.Count > 0
          ? validation.StructuredErrors
          : validation.Errors.Select(error => ProviderError(
              definition.Id,
              "Provider validation rejected the resource.",
              error)).ToArray();
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

    var plan = SnapshotPlan(resourcePlan);
    var planError = ValidateProviderPlan(definition, plan);
    if (planError is not null)
    {
      return Invalid(CreatePlannedResource(resolved, definition, plan), planError);
    }

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
      var diagnostics = plan.StructuredErrors.Count > 0
          ? plan.StructuredErrors
          : [ProviderError(
              definition.Id,
              status == PlannedResourceStatus.DetectionFailed
                  ? "Detection did not produce a plannable state."
                  : "The provider cannot produce an executable plan.",
              plan.Error ?? $"Resource '{definition.Id}' has compliance status '{plan.Compliance}'.")];
      item = item with { Diagnostics = ReadOnly(diagnostics) };
    }

    return item;
  }

  private static StructuredError? ValidateProviderPlan(
      ResourceDefinition definition,
      ResourcePlan plan)
  {
    var expectedFingerprint = ResourceDefinitionFingerprint.Create(definition);
    if (!IdComparer.Equals(plan.ResourceId, definition.Id) ||
        !IdComparer.Equals(plan.ResourceType, definition.Type) ||
        !IdComparer.Equals(plan.ProviderName, definition.Provider) ||
        !string.Equals(
            plan.DesiredStateFingerprint,
            expectedFingerprint,
            StringComparison.Ordinal))
    {
      return ProviderError(
          definition.Id,
          "Provider plan identity does not match the requested resource.",
          "The provider returned a different resource id, type, provider, or desired-state fingerprint.");
    }

    if (plan.Steps.Count > MaxStepsPerResource)
    {
      return ProviderError(
          definition.Id,
          "Provider plan contains too many steps.",
          $"Resource '{definition.Id}' has {plan.Steps.Count} steps; the limit is {MaxStepsPerResource}.");
    }

    var stepIds = new HashSet<string>(IdComparer);
    foreach (var step in plan.Steps)
    {
      if (string.IsNullOrWhiteSpace(step.Id) || string.IsNullOrWhiteSpace(step.Description))
      {
        return ProviderError(
            definition.Id,
            "Provider plan contains a malformed step.",
            "Every step must have a non-empty id and description.");
      }

      if (!stepIds.Add(step.Id))
      {
        return ProviderError(
            definition.Id,
            "Provider plan contains duplicate step ids.",
            $"Duplicate step id '{step.Id}' occurs for resource '{definition.Id}'.");
      }
    }

    var remediable = plan.Compliance is ComplianceStatus.Missing or
        ComplianceStatus.VersionMismatch or ComplianceStatus.ConfigurationMismatch;
    var modifyingSteps = plan.Steps.Where(step => step.Action != PlanAction.None).ToArray();
    if (!remediable && modifyingSteps.Length > 0)
    {
      return ProviderError(
          definition.Id,
          "Provider plan modifies a non-remediable resource state.",
          $"Compliance status '{plan.Compliance}' cannot contain modifying steps.");
    }

    if (remediable && plan.IsExecutable && modifyingSteps.Length == 0)
    {
      return ProviderError(
          definition.Id,
          "Provider plan has no remediation steps.",
          $"Executable status '{plan.Compliance}' requires at least one modifying step.");
    }

    if (plan.Compliance == ComplianceStatus.Satisfied && !plan.IsExecutable)
    {
      return ProviderError(
          definition.Id,
          "Provider marked a satisfied resource as non-executable.",
          "Satisfied resources must be executable without applying changes.");
    }

    return null;
  }

  private static PlannedResource CreatePlannedResource(
      ResolvedResource resolved,
      ResourceDefinition definition,
      ResourcePlan plan)
  {
    var requiresElevation = definition.PrivilegeRequirement == PrivilegeRequirement.Administrator ||
        plan.Steps.Any(step => step.PrivilegeRequirement == PrivilegeRequirement.Administrator);
    var isDestructive = plan.Steps.Any(step => step.IsDestructive);
    var restartPolicy = plan.Steps
        .Select(step => step.RestartPolicy)
        .Append(definition.RestartPolicy)
        .Max();
    var reason = plan.Steps
        .Select(step => step.Reason)
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? plan.Error;
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

  private static PlannedResource Invalid(PlannedResource item, StructuredError error) => item with
  {
    Status = PlannedResourceStatus.Invalid,
    Diagnostics = ReadOnly(item.Diagnostics.Append(error))
  };

  private static ResourceDefinition SnapshotDefinition(ResourceDefinition definition) =>
      definition with
      {
        Dependencies = ReadOnly(definition.Dependencies),
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

  private static ResourcePlan SnapshotPlan(ResourcePlan plan) => plan with
  {
    Steps = ReadOnly(plan.Steps.Select(step => step with { })),
    StructuredErrors = ReadOnly(plan.StructuredErrors)
  };

  private static IReadOnlyList<ResourceGraphLayer> SnapshotLayers(
      IEnumerable<ResourceGraphLayer> layers) => ReadOnly(layers.Select(layer =>
          new ResourceGraphLayer(layer.Index, ReadOnly(layer.ResourceIds))));

  private static StructuredError? ValidateGraph(ResourceGraph graph)
  {
    if (graph.Nodes.Count == 0 || graph.TopologicalLayers.Count == 0)
    {
      return new StructuredError(
          WdemErrorCode.DependencyError,
          "The resource graph has no executable layers.",
          "Select at least one acyclic resource before creating an execution plan.");
    }

    var seen = new HashSet<string>(IdComparer);
    var completed = new HashSet<string>(IdComparer);
    for (var index = 0; index < graph.TopologicalLayers.Count; index++)
    {
      var layer = graph.TopologicalLayers[index];
      if (layer.Index != index || layer.ResourceIds.Count == 0)
      {
        return MalformedGraph("Graph layer indices must be contiguous and layers cannot be empty.");
      }

      var current = new HashSet<string>(layer.ResourceIds, IdComparer);
      foreach (var id in layer.ResourceIds)
      {
        if (!graph.Nodes.TryGetValue(id, out var node) || !seen.Add(id))
        {
          return MalformedGraph($"Resource '{id}' is missing from the graph or occurs in multiple layers.");
        }

        if (node.Definition.Dependencies.Any(dependency =>
                !graph.Nodes.ContainsKey(dependency) ||
                (!completed.Contains(dependency) && !current.Contains(dependency))))
        {
          return MalformedGraph($"Resource '{id}' has a missing or incorrectly ordered dependency.");
        }

        if (node.Definition.Dependencies.Any(current.Contains))
        {
          return MalformedGraph($"Resource '{id}' depends on a resource in the same graph layer.");
        }
      }

      completed.UnionWith(current);
    }

    return seen.Count == graph.Nodes.Count
        ? null
        : MalformedGraph("Not every graph resource occurs in a topological layer.");
  }

  private static StructuredError MalformedGraph(string detail) => new(
      WdemErrorCode.DependencyError,
      "The resource graph is not a valid topological plan.",
      detail)
  {
    SuggestedAction = "Rebuild the resource graph and create a new plan."
  };

  private static StructuredError ProviderException(
      string resourceId,
      string summary,
      Exception exception) => new(
          WdemErrorCode.ProviderError,
          summary,
          exception.Message)
      {
        ResourceId = resourceId,
        UnderlyingException = exception,
        SuggestedAction = "Review provider diagnostics and create a new plan."
      };

  private static StructuredError ProviderError(
      string resourceId,
      string summary,
      string detail) => new(WdemErrorCode.ProviderError, summary, detail)
      {
        ResourceId = resourceId,
        SuggestedAction = "Review provider diagnostics and create a new plan."
      };

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
