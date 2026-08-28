using System.Collections.Frozen;
using Wdem.Core.Execution;
using Wdem.Core.Profiles;
using Wdem.Core.Resources;

namespace Wdem.Core.Graph;

public sealed class ResourceGraphBuilder
{
  private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;
  private readonly Func<string, string?>? _environmentVariableReader;

  public ResourceGraphBuilder(Func<string, string?>? environmentVariableReader = null)
  {
    _environmentVariableReader = environmentVariableReader;
  }

  public ResourceGraph Build(DeveloperProfile profile, ProfileSelection selection)
  {
    var result = TryBuild(profile, selection);
    if (result.Errors.Count > 0)
    {
      throw new InvalidOperationException(result.Errors[0].Detail);
    }

    return result.Graph!;
  }

  public ResourceGraphBuildResult TryBuild(DeveloperProfile profile, ProfileSelection selection)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(selection);

    var errors = new List<StructuredError>();
    var nodes = new Dictionary<string, NodeState>(IdComparer);
    var requiredReferences = ToReferenceMap(profile.RequiredResources);
    var optionalReferences = ToReferenceMap(profile.OptionalResources);

    ValidateSelection(selection, requiredReferences, optionalReferences, errors);
    var selectedOptionalIds = selection.SelectedOptionalResourceIds is null
        ? new HashSet<string>(
            profile.OptionalResources
                .Where(reference => reference.DefaultSelected)
                .Select(reference => reference.Id),
            IdComparer)
        : new HashSet<string>(
            selection.SelectedOptionalResourceIds.Where(id => id is not null),
            IdComparer);

    foreach (var reference in profile.RequiredResources)
    {
      AddSeed(profile, nodes, reference, ResourceOrigin.Required, errors);
    }

    foreach (var reference in profile.OptionalResources)
    {
      if (selectedOptionalIds.Contains(reference.Id))
      {
        AddSeed(profile, nodes, reference, ResourceOrigin.SelectedOptional, errors);
      }
    }

    ResolveDependencyClosure(
        profile,
        nodes,
        requiredReferences,
        optionalReferences,
        errors);

    if (errors.Count == 0)
    {
      ExpandSelectedValues(profile, nodes, errors);
    }

    if (errors.Count == 0)
    {
      var cycle = FindCycle(nodes);
      if (cycle is not null)
      {
        errors.Add(DependencyError(
            "The selected resources contain a dependency cycle.",
            $"Dependency cycle detected: {string.Join(" -> ", cycle)}."));
      }
    }

    var layers = errors.Count == 0
        ? BuildTopologicalLayers(nodes)
        : Array.Empty<ResourceGraphLayer>();
    return new ResourceGraphBuildResult(
        CreateGraph(nodes, layers),
        Array.AsReadOnly(errors.ToArray()));
  }

  private static Dictionary<string, ProfileResourceReference> ToReferenceMap(
      IEnumerable<ProfileResourceReference> references)
  {
    var result = new Dictionary<string, ProfileResourceReference>(IdComparer);
    foreach (var reference in references)
    {
      result.TryAdd(reference.Id, reference);
    }

    return result;
  }

  private static void ValidateSelection(
      ProfileSelection selection,
      IReadOnlyDictionary<string, ProfileResourceReference> requiredReferences,
      IReadOnlyDictionary<string, ProfileResourceReference> optionalReferences,
      List<StructuredError> errors)
  {
    if (selection.SelectedOptionalResourceIds is null)
    {
      return;
    }

    foreach (var id in selection.SelectedOptionalResourceIds
                 .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(id => id, StringComparer.Ordinal))
    {
      if (id is null)
      {
        errors.Add(ProfileError(
            "The optional resource selection is invalid.",
            "A selected optional resource id cannot be null."));
      }
      else if (optionalReferences.ContainsKey(id))
      {
        continue;
      }
      else if (requiredReferences.ContainsKey(id))
      {
        errors.Add(ProfileError(
            "A required resource cannot be changed through optional selection.",
            $"Resource '{id}' is required and cannot be deselected or selected as optional."));
      }
      else
      {
        errors.Add(ProfileError(
            "The optional resource selection is unknown.",
            $"Resource '{id}' is not declared as an optional resource in this profile."));
      }
    }
  }

  private static void AddSeed(
      DeveloperProfile profile,
      Dictionary<string, NodeState> nodes,
      ProfileResourceReference reference,
      ResourceOrigin origin,
      List<StructuredError> errors)
  {
    if (!profile.Resources.TryGetValue(reference.Id, out var resource))
    {
      errors.Add(ProfileError(
          "A selected profile resource is not defined.",
          $"Resource '{reference.Id}' is referenced by the profile but has no definition."));
      return;
    }

    var overridden = ApplyReferenceOverrides(resource, reference);
    if (!nodes.TryGetValue(reference.Id, out var existing))
    {
      nodes.Add(resource.Id, new NodeState(overridden, origin));
      return;
    }

    if (GetOriginPriority(origin) > GetOriginPriority(existing.Origin))
    {
      existing.Origin = origin;
      existing.Definition = overridden;
    }
  }

  private static ResourceDefinition ApplyReferenceOverrides(
      ResourceDefinition resource,
      ProfileResourceReference reference) => resource with
      {
        VersionConstraint = reference.VersionConstraint ?? resource.VersionConstraint,
        PreferredVersion = reference.PreferredVersion ?? resource.PreferredVersion
      };

  private static void ResolveDependencyClosure(
      DeveloperProfile profile,
      Dictionary<string, NodeState> nodes,
      IReadOnlyDictionary<string, ProfileResourceReference> requiredReferences,
      IReadOnlyDictionary<string, ProfileResourceReference> optionalReferences,
      List<StructuredError> errors)
  {
    var pending = new Stack<string>(nodes.Keys.Reverse());
    var visited = new HashSet<string>(IdComparer);
    while (pending.Count > 0)
    {
      var resourceId = pending.Pop();
      if (!visited.Add(resourceId) || !nodes.TryGetValue(resourceId, out var node))
      {
        continue;
      }

      for (var index = node.Definition.Dependencies.Count - 1; index >= 0; index--)
      {
        var dependencyId = node.Definition.Dependencies[index];
        if (!profile.Resources.TryGetValue(dependencyId, out var dependency))
        {
          errors.Add(DependencyError(
              "A selected resource dependency is missing.",
              $"Resource '{node.Definition.Id}' depends on undefined resource '{dependencyId}'.",
              node.Definition.Id));
          continue;
        }

        if (!nodes.TryGetValue(dependencyId, out var dependencyNode))
        {
          var resolvedDependency = requiredReferences.TryGetValue(dependencyId, out var requiredReference)
              ? ApplyReferenceOverrides(dependency, requiredReference)
              : optionalReferences.TryGetValue(dependencyId, out var optionalReference)
                  ? ApplyReferenceOverrides(dependency, optionalReference)
                  : dependency;
          dependencyNode = new NodeState(resolvedDependency, ResourceOrigin.AutoDependency);
          nodes.Add(dependency.Id, dependencyNode);
        }

        dependencyNode.RequiredBy.Add(node.Definition.Id);
        pending.Push(dependencyId);
      }
    }
  }

  private void ExpandSelectedValues(
      DeveloperProfile profile,
      Dictionary<string, NodeState> nodes,
      List<StructuredError> errors)
  {
    var expansion = ProfileValueExpander.ExpandSelected(
        profile,
        nodes.Keys,
        _environmentVariableReader);
    if (expansion.Errors.Count > 0)
    {
      errors.AddRange(expansion.Errors);
      return;
    }

    foreach (var pair in nodes)
    {
      var expanded = expansion.Profile!.Resources[pair.Key];
      pair.Value.Definition = expanded with
      {
        VersionConstraint = pair.Value.Definition.VersionConstraint,
        PreferredVersion = pair.Value.Definition.PreferredVersion
      };
    }
  }

  private static IReadOnlyList<string>? FindCycle(IReadOnlyDictionary<string, NodeState> nodes)
  {
    var colors = new Dictionary<string, VisitColor>(IdComparer);
    var activePath = new List<string>();
    var activePositions = new Dictionary<string, int>(IdComparer);

    foreach (var rootId in nodes.Keys.Order(IdComparer))
    {
      if (colors.GetValueOrDefault(rootId) != VisitColor.Unvisited)
      {
        continue;
      }

      var stack = new Stack<TraversalFrame>();
      Enter(rootId, stack, activePath, activePositions, colors);
      while (stack.Count > 0)
      {
        var frame = stack.Pop();
        var dependencies = nodes[frame.ResourceId].Definition.Dependencies;
        if (frame.NextDependencyIndex >= dependencies.Count)
        {
          colors[frame.ResourceId] = VisitColor.Visited;
          activePositions.Remove(frame.ResourceId);
          activePath.RemoveAt(activePath.Count - 1);
          continue;
        }

        stack.Push(frame with { NextDependencyIndex = frame.NextDependencyIndex + 1 });
        var dependencyId = nodes[dependencies[frame.NextDependencyIndex]].Definition.Id;
        var color = colors.GetValueOrDefault(dependencyId);
        if (color == VisitColor.Visiting)
        {
          var start = activePositions[dependencyId];
          return activePath.Skip(start).Append(dependencyId).ToArray();
        }

        if (color == VisitColor.Unvisited)
        {
          Enter(dependencyId, stack, activePath, activePositions, colors);
        }
      }
    }

    return null;
  }

  private static void Enter(
      string resourceId,
      Stack<TraversalFrame> stack,
      List<string> activePath,
      Dictionary<string, int> activePositions,
      Dictionary<string, VisitColor> colors)
  {
    colors[resourceId] = VisitColor.Visiting;
    activePositions[resourceId] = activePath.Count;
    activePath.Add(resourceId);
    stack.Push(new TraversalFrame(resourceId, 0));
  }

  private static IReadOnlyList<ResourceGraphLayer> BuildTopologicalLayers(
      IReadOnlyDictionary<string, NodeState> nodes)
  {
    var indegrees = nodes.Keys.ToDictionary(id => id, _ => 0, IdComparer);
    var dependents = nodes.Keys.ToDictionary(
        id => id,
        _ => new HashSet<string>(IdComparer),
        IdComparer);

    foreach (var pair in nodes)
    {
      var distinctDependencies = new HashSet<string>(pair.Value.Definition.Dependencies, IdComparer);
      indegrees[pair.Key] = distinctDependencies.Count;
      foreach (var dependencyId in distinctDependencies)
      {
        dependents[dependencyId].Add(pair.Key);
      }
    }

    var ready = indegrees
        .Where(pair => pair.Value == 0)
        .Select(pair => pair.Key)
        .Order(IdComparer)
        .ToArray();
    var layers = new List<ResourceGraphLayer>();
    while (ready.Length > 0)
    {
      layers.Add(new ResourceGraphLayer(layers.Count, ready));
      var next = new HashSet<string>(IdComparer);
      foreach (var resourceId in ready)
      {
        foreach (var dependentId in dependents[resourceId])
        {
          indegrees[dependentId]--;
          if (indegrees[dependentId] == 0)
          {
            next.Add(dependentId);
          }
        }
      }

      ready = next.Order(IdComparer).ToArray();
    }

    return layers;
  }

  private static ResourceGraph CreateGraph(
      IReadOnlyDictionary<string, NodeState> nodes,
      IReadOnlyList<ResourceGraphLayer> layers)
  {
    var resolved = nodes.ToFrozenDictionary(
        pair => pair.Key,
        pair => new ResolvedResource(
            SnapshotDefinition(pair.Value.Definition),
            pair.Value.Origin,
            pair.Value.RequiredBy.ToFrozenSet(IdComparer)),
        IdComparer);
    var readOnlyLayers = Array.AsReadOnly(layers
        .Select(layer => new ResourceGraphLayer(
            layer.Index,
            Array.AsReadOnly(layer.ResourceIds.ToArray())))
        .ToArray());
    return new ResourceGraph(resolved, readOnlyLayers);
  }

  private static ResourceDefinition SnapshotDefinition(ResourceDefinition definition) =>
      definition with
      {
        Dependencies = Array.AsReadOnly(definition.Dependencies.ToArray()),
        Parameters = definition.Parameters.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase)
      };

  private static int GetOriginPriority(ResourceOrigin origin) => origin switch
  {
    ResourceOrigin.Required => 3,
    ResourceOrigin.SelectedOptional => 2,
    ResourceOrigin.AutoDependency => 1,
    _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown resource origin.")
  };

  private static StructuredError ProfileError(string summary, string detail) => new(
      WdemErrorCode.ProfileError,
      summary,
      detail)
  {
    SuggestedAction = "Select only optional resources declared by the profile."
  };

  private static StructuredError DependencyError(
      string summary,
      string detail,
      string? resourceId = null) => new(
      WdemErrorCode.DependencyError,
      summary,
      detail)
      {
        ResourceId = resourceId,
        SuggestedAction = "Correct the resource dependency declarations and try again."
      };

  private sealed class NodeState(ResourceDefinition definition, ResourceOrigin origin)
  {
    public ResourceDefinition Definition { get; set; } = definition;
    public ResourceOrigin Origin { get; set; } = origin;
    public HashSet<string> RequiredBy { get; } = new(IdComparer);
  }

  private sealed record TraversalFrame(string ResourceId, int NextDependencyIndex);

  private enum VisitColor
  {
    Unvisited,
    Visiting,
    Visited
  }
}
