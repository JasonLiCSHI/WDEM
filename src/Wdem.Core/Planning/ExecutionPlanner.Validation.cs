using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Planning;

public sealed partial class ExecutionPlanner
{
  private static StructuredError? ValidateProviderPlan(
      ResourceDefinition definition,
      ComplianceStatus evaluatedCompliance,
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

    if (!Enum.IsDefined(plan.Compliance))
    {
      return ProviderError(
          definition.Id,
          "Provider plan contains an unknown compliance status.",
          "The provider returned an undefined compliance status.");
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
      var sanitizedDescription = SanitizeVisible(step.Description);
      var sanitizedReason = SanitizeOptional(step.Reason);
      if (!IsValidStepId(step.Id) || string.IsNullOrWhiteSpace(sanitizedDescription))
      {
        return ProviderError(
            definition.Id,
            "Provider plan contains a malformed step.",
            "Every step must have a restricted id and a non-empty description.");
      }

      if (!Enum.IsDefined(step.Action) ||
          !Enum.IsDefined(step.PrivilegeRequirement) ||
          !Enum.IsDefined(step.RestartPolicy))
      {
        return ProviderError(
            definition.Id,
            "Provider plan contains an unknown step value.",
            $"Step '{SanitizeVisible(step.Id)}' uses an undefined action, privilege, or restart value.");
      }

      if (Utf8ByteCount(step.Description) > MaxTextFieldByteCount ||
          Utf8ByteCount(sanitizedDescription) > MaxTextFieldByteCount ||
          step.Reason is not null && Utf8ByteCount(step.Reason) > MaxTextFieldByteCount ||
          sanitizedReason is not null && Utf8ByteCount(sanitizedReason) > MaxTextFieldByteCount)
      {
        return ProviderError(
            definition.Id,
            "Provider plan contains oversized step text.",
            $"Step '{SanitizeVisible(step.Id)}' exceeds the {MaxTextFieldByteCount}-byte field limit.");
      }

      if (!stepIds.Add(step.Id))
      {
        return ProviderError(
            definition.Id,
            "Provider plan contains duplicate step ids.",
            $"Duplicate step id '{step.Id}' occurs for resource '{definition.Id}'.");
      }
    }

    if (plan.Compliance != evaluatedCompliance)
    {
      return new StructuredError(
          evaluatedCompliance is ComplianceStatus.DetectionFailed or ComplianceStatus.Unsupported
              ? WdemErrorCode.DetectionError
              : WdemErrorCode.ProviderError,
          "Provider plan contradicts the compliance evaluation.",
          $"The compliance evaluator returned '{evaluatedCompliance}' but the provider returned '{plan.Compliance}'.")
      {
        ResourceId = definition.Id,
        SuggestedAction = "Detect the resource again and review the provider implementation."
      };
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

    if ((plan.Compliance is ComplianceStatus.Unsupported or ComplianceStatus.DetectionFailed) &&
        plan.IsExecutable)
    {
      return ProviderError(
          definition.Id,
          "Provider marked a non-remediable state as executable.",
          $"Compliance status '{plan.Compliance}' must not be executable.");
    }

    return null;
  }

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
        if (string.IsNullOrWhiteSpace(id) ||
            !graph.Nodes.TryGetValue(id, out var node) ||
            node is null ||
            !seen.Add(id))
        {
          return MalformedGraph($"Resource '{id}' is missing from the graph or occurs in multiple layers.");
        }

        if (!IdComparer.Equals(node.Definition.Id, id))
        {
          return MalformedGraph($"Resource '{id}' does not match its definition identity.");
        }

        if (string.IsNullOrWhiteSpace(node.Definition.Type) ||
            string.IsNullOrWhiteSpace(node.Definition.Provider))
        {
          return ProviderError(
              node.Definition.Id,
              "Resource graph contains an invalid provider identity.",
              "Resource type and provider name must both be non-empty.");
        }

        if (!Enum.IsDefined(node.Origin) ||
            !Enum.IsDefined(node.Definition.PrivilegeRequirement) ||
            !Enum.IsDefined(node.Definition.RestartPolicy))
        {
          return ProviderError(
              node.Definition.Id,
              "Resource graph contains an unknown enum value.",
              "The resource origin, privilege, or restart policy is undefined.");
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

  private static bool IsValidStepId(string? value)
  {
    if (string.IsNullOrEmpty(value) || value.Length > MaxStepIdLength)
    {
      return false;
    }

    return value.All(character =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or
            '.' or '_' or ':' or '-');
  }

  private static bool IsValidResourceId(string? value)
    => !string.IsNullOrWhiteSpace(value);
}
