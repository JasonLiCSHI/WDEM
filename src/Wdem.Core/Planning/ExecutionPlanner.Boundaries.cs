using System.Collections.Frozen;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Planning;

public sealed partial class ExecutionPlanner
{
  private static bool TrySnapshotGraph(
      ResourceGraph graph,
      ref int totalTextBytes,
      out ResourceGraph snapshot,
      out StructuredError? contractError)
  {
    snapshot = new ResourceGraph(
        FrozenDictionary<string, ResolvedResource>.Empty,
        Array.Empty<ResourceGraphLayer>());
    contractError = null;
    if (graph.Nodes is null || graph.TopologicalLayers is null)
    {
      contractError = GraphBoundaryError(
          "The resource graph is malformed.",
          "Graph node and layer collections cannot be null.");
      return false;
    }

    if (graph.Nodes.Count > MaxResourceCount ||
        graph.TopologicalLayers.Count > MaxExternalCollectionCount)
    {
      contractError = GraphBoundaryError(
          "The execution plan is too large.",
          $"The graph exceeds the limit of {MaxResourceCount} resources or " +
          $"{MaxExternalCollectionCount} layers.");
      return false;
    }

    var nodes = new Dictionary<string, ResolvedResource>(IdComparer);
    foreach (var pair in graph.Nodes)
    {
      if (!IsValidResourceId(pair.Key) || pair.Value?.Definition is null)
      {
        contractError = GraphBoundaryError(
            "The resource graph contains a malformed node.",
            "Graph keys must be restricted resource ids and definitions cannot be null.");
        return false;
      }

      if (!TryAddExternalText(pair.Key, ref totalTextBytes) ||
          !TrySnapshotDefinition(
              pair.Value.Definition,
              ref totalTextBytes,
              out var definition,
              out contractError))
      {
        contractError ??= ResourceBoundaryError(
            pair.Key,
            "The resource definition exceeds planner limits.",
            "A resource field or the total external input exceeded its byte limit.");
        return false;
      }

      if (!IdComparer.Equals(pair.Key, definition.Id) || pair.Value.RequiredBy is null)
      {
        contractError = GraphBoundaryError(
            "The resource graph contains an identity mismatch.",
            "A graph key does not match its definition or has a null required-by collection.");
        return false;
      }

      if (pair.Value.RequiredBy.Count > MaxExternalCollectionCount)
      {
        contractError = ResourceBoundaryError(
            definition.Id,
            "The resource graph contains too many reverse dependencies.",
            $"The collection limit is {MaxExternalCollectionCount} entries.");
        return false;
      }

      var requiredBy = new HashSet<string>(IdComparer);
      foreach (var requiredById in pair.Value.RequiredBy)
      {
        if (!IsValidResourceId(requiredById) ||
            !TryAddExternalText(requiredById, ref totalTextBytes) ||
            !requiredBy.Add(requiredById))
        {
          contractError = ResourceBoundaryError(
              definition.Id,
              "The resource graph contains malformed reverse dependencies.",
              "Reverse-dependency ids must be restricted, unique, and within planner limits.");
          return false;
        }
      }

      if (!nodes.TryAdd(pair.Key, new ResolvedResource(
              definition,
              pair.Value.Origin,
              requiredBy.ToFrozenSet(IdComparer))))
      {
        contractError = GraphBoundaryError(
            "The resource graph contains a duplicate resource.",
            "Resource ids must be unique when compared case-insensitively.");
        return false;
      }
    }

    var layers = new List<ResourceGraphLayer>(graph.TopologicalLayers.Count);
    foreach (var layer in graph.TopologicalLayers)
    {
      if (layer is null || layer.ResourceIds is null ||
          layer.ResourceIds.Count > MaxExternalCollectionCount)
      {
        contractError = GraphBoundaryError(
            "The resource graph contains a malformed layer.",
            "Layer resource collections cannot be null or exceed planner limits.");
        return false;
      }

      var resourceIds = new List<string>(layer.ResourceIds.Count);
      foreach (var resourceId in layer.ResourceIds)
      {
        if (!IsValidResourceId(resourceId) ||
            !TryAddExternalText(resourceId, ref totalTextBytes))
        {
          contractError = GraphBoundaryError(
              "The resource graph contains a malformed layer identity.",
              "Layer resource ids must be restricted and within planner limits.");
          return false;
        }

        resourceIds.Add(resourceId);
      }

      layers.Add(new ResourceGraphLayer(layer.Index, ReadOnly(resourceIds)));
    }

    snapshot = new ResourceGraph(
        nodes.ToFrozenDictionary(IdComparer),
        ReadOnly(layers));
    return true;
  }

  private static bool TrySnapshotDefinition(
      ResourceDefinition definition,
      ref int totalTextBytes,
      out ResourceDefinition snapshot,
      out StructuredError? contractError)
  {
    snapshot = definition;
    contractError = null;
    if (!IsValidResourceId(definition.Id) ||
        !IsValidResourceId(definition.Type) ||
        !IsValidResourceId(definition.Provider))
    {
      contractError = ResourceBoundaryError(
          definition.Id,
          "The resource definition has an invalid identity.",
          "Resource ids, types, and provider names must use the restricted identifier format.");
      return false;
    }

    if (definition.Dependencies is null || definition.Parameters is null)
    {
      contractError = ResourceBoundaryError(
          definition.Id,
          "The resource definition is malformed.",
          "Dependency and parameter collections cannot be null.");
      return false;
    }

    if (definition.Dependencies.Count > MaxExternalCollectionCount ||
        definition.Parameters.Count > MaxExternalCollectionCount)
    {
      contractError = ResourceBoundaryError(
          definition.Id,
          "The resource definition contains too many values.",
          $"Dependency and parameter collections are limited to {MaxExternalCollectionCount} entries.");
      return false;
    }

    foreach (var value in new[]
             {
               definition.Id,
               definition.Type,
               definition.Provider,
               definition.DisplayName,
               definition.VersionConstraint,
               definition.PreferredVersion
             })
    {
      if (!TryAddExternalText(value, ref totalTextBytes))
      {
        contractError = ResourceBoundaryError(
            definition.Id,
            "The resource definition contains oversized text.",
            "A resource field or the total external input exceeded its byte limit.");
        return false;
      }
    }

    var dependencies = new HashSet<string>(IdComparer);
    foreach (var dependency in definition.Dependencies)
    {
      if (!IsValidResourceId(dependency) ||
          !TryAddExternalText(dependency, ref totalTextBytes) ||
          !dependencies.Add(dependency))
      {
        contractError = ResourceBoundaryError(
            definition.Id,
            "The resource definition contains malformed dependencies.",
            "Dependency ids must be restricted, unique, and within planner limits.");
        return false;
      }
    }

    var parameters = new Dictionary<string, string?>(IdComparer);
    foreach (var parameter in definition.Parameters)
    {
      if (string.IsNullOrWhiteSpace(parameter.Key) ||
          parameter.Key.Any(char.IsControl) ||
          parameter.Value is null ||
          !TryAddExternalText(parameter.Key, ref totalTextBytes) ||
          !TryAddExternalText(parameter.Value, ref totalTextBytes) ||
          !parameters.TryAdd(parameter.Key, parameter.Value))
      {
        contractError = ResourceBoundaryError(
            definition.Id,
            "The resource definition contains malformed parameters.",
            "Parameter keys must be unique case-insensitively and values cannot be null or exceed limits.");
        return false;
      }
    }

    snapshot = definition with
    {
      Dependencies = ReadOnly(dependencies
          .Order(IdComparer)
          .ThenBy(id => id, StringComparer.Ordinal)),
      Parameters = parameters.ToFrozenDictionary(IdComparer)
    };
    return true;
  }

  private static bool TrySnapshotDetectedStates(
      IReadOnlyDictionary<string, DetectedState> source,
      ref int totalTextBytes,
      out IReadOnlyDictionary<string, DetectedState> snapshot,
      out StructuredError? contractError)
  {
    var states = new Dictionary<string, DetectedState>(IdComparer);
    snapshot = FrozenDictionary<string, DetectedState>.Empty;
    contractError = null;
    if (source.Count > MaxExternalCollectionCount)
    {
      contractError = DetectionBoundaryError(
          "The detected-state collection is too large.",
          $"Detected states are limited to {MaxExternalCollectionCount} entries.");
      return false;
    }

    foreach (var pair in source)
    {
      if (!IsValidResourceId(pair.Key) || pair.Value is null ||
          !IsValidResourceId(pair.Value.ResourceId))
      {
        contractError = DetectionBoundaryError(
            "The detected-state collection is malformed.",
            "Detected-state keys and resource ids must use the restricted identifier format.");
        return false;
      }

      if (!IdComparer.Equals(pair.Key, pair.Value.ResourceId))
      {
        contractError = DetectionBoundaryError(
            "The detected-state collection has an identity mismatch.",
            "A detected-state key does not match its resource identity.");
        return false;
      }

      var state = pair.Value;
      if (state.InstalledVersions is null || state.Evidence is null ||
          state.InstalledVersions.Count > MaxExternalCollectionCount ||
          state.Evidence.Count > MaxExternalCollectionCount)
      {
        contractError = DetectionBoundaryError(
            "A detected state contains a malformed collection.",
            $"Detected-state collections cannot be null or exceed {MaxExternalCollectionCount} entries.");
        return false;
      }

      foreach (var value in new[]
               {
                 pair.Key,
                 state.ResourceId,
                 state.Version,
                 state.ConfigurationHash,
                 state.Error
               })
      {
        if (!TryAddExternalText(value, ref totalTextBytes))
        {
          contractError = DetectionBoundaryError(
              "A detected state contains oversized text.",
              "A detected-state field or the total external input exceeded its byte limit.");
          return false;
        }
      }

      var evidence = new Dictionary<string, string>(IdComparer);
      foreach (var item in state.Evidence)
      {
        if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Any(char.IsControl) ||
            item.Value is null ||
            !TryAddExternalText(item.Key, ref totalTextBytes) ||
            !TryAddExternalText(item.Value, ref totalTextBytes) ||
            !evidence.TryAdd(item.Key, item.Value))
        {
          contractError = DetectionBoundaryError(
              "A detected state contains malformed evidence.",
              "Evidence keys and values must be non-null, unique, and within planner limits.");
          return false;
        }
      }

      StructuredError? diagnostic = null;
      if (state.StructuredError is not null)
      {
        if (!TrySnapshotExternalDiagnostic(
                state.StructuredError,
                ref totalTextBytes,
                out diagnostic))
        {
          contractError = DetectionBoundaryError(
              "A detected state contains an oversized diagnostic.",
              "Diagnostic fields must use valid identities and remain within planner limits.");
          return false;
        }
      }

      var stateSnapshot = state with
      {
        InstalledVersions = ReadOnly(state.InstalledVersions),
        Evidence = evidence.ToFrozenDictionary(IdComparer),
        StructuredError = diagnostic
      };
      if (!states.TryAdd(pair.Key, stateSnapshot))
      {
        contractError = DetectionBoundaryError(
            "The detected-state collection contains a duplicate resource.",
            "Duplicate detected-state keys are not allowed when compared case-insensitively.");
        return false;
      }
    }

    snapshot = states.ToFrozenDictionary(IdComparer);
    return true;
  }

  private static bool TrySnapshotExternalDiagnostic(
      StructuredError diagnostic,
      ref int totalTextBytes,
      out StructuredError snapshot)
  {
    snapshot = diagnostic;
    if (diagnostic.ResourceId is not null && !IsValidResourceId(diagnostic.ResourceId) ||
        diagnostic.StepId is not null && !IsValidStepId(diagnostic.StepId))
    {
      return false;
    }

    foreach (var value in new[]
             {
               diagnostic.Summary,
               diagnostic.Detail,
               diagnostic.ResourceId,
               diagnostic.StepId,
               diagnostic.LogLocation,
               diagnostic.SuggestedAction,
               diagnostic.UnderlyingExceptionType,
               diagnostic.UnderlyingExceptionMessage
             })
    {
      if (!TryAddExternalText(value, ref totalTextBytes))
      {
        return false;
      }
    }

    snapshot = StructuredError.CreateSnapshot(
        diagnostic.Code,
        SanitizeVisible(diagnostic.Summary),
        SanitizeVisible(diagnostic.Detail),
        NormalizeResourceId(diagnostic.ResourceId),
        diagnostic.StepId,
        diagnostic.ProcessExitCode,
        SanitizeOptional(diagnostic.LogLocation),
        SanitizeOptional(diagnostic.SuggestedAction),
        diagnostic.IsRetryable,
        SanitizeOptional(diagnostic.UnderlyingExceptionType),
        SanitizeOptional(diagnostic.UnderlyingExceptionMessage));
    return true;
  }

  private static bool TryAddExternalText(string? value, ref int totalTextBytes)
  {
    var bytes = Utf8ByteCount(value);
    if (bytes > MaxTextFieldByteCount ||
        totalTextBytes > MaxTotalExternalTextByteCount - bytes)
    {
      return false;
    }

    totalTextBytes += bytes;
    return true;
  }

  private static StructuredError GraphBoundaryError(string summary, string detail) => new(
      WdemErrorCode.DependencyError,
      SanitizeVisible(summary),
      SanitizeVisible(detail))
  {
    SuggestedAction = "Rebuild the resource graph and create a new plan."
  };

  private static StructuredError ResourceBoundaryError(
      string? resourceId,
      string summary,
      string detail) => new(
          WdemErrorCode.ProfileError,
          SanitizeVisible(summary),
          SanitizeVisible(detail))
      {
        ResourceId = NormalizeResourceId(resourceId),
        SuggestedAction = "Correct the resource definition and create a new plan."
      };

  private static StructuredError DetectionBoundaryError(string summary, string detail) => new(
      WdemErrorCode.DetectionError,
      SanitizeVisible(summary),
      SanitizeVisible(detail))
  {
    SuggestedAction = "Detect the selected resources again before creating a plan."
  };

  private static StructuredError BoundaryException(
      WdemErrorCode code,
      string summary,
      Exception exception) => StructuredError.CreateSnapshot(
          code,
          SanitizeVisible(summary),
          "An external input collection failed while the planner was reading it.",
          null,
          null,
          null,
          null,
          "Rebuild the external input and create a new plan.",
          false,
          SanitizeVisible(exception.GetType().FullName ?? exception.GetType().Name),
          SanitizeVisible(exception.Message));
}
