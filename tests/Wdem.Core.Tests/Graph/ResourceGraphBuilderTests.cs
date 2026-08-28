using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Core.Resources;
using Xunit;

namespace Wdem.Core.Tests.Graph;

public sealed class ResourceGraphBuilderTests
{
  [Fact]
  public void Build_SelectingOptionalResource_AddsTransitiveDependenciesInStableLayers()
  {
    var graph = CreateBuilder().Build(Profile(), new ProfileSelection(
        new HashSet<string>(["resharper-settings"], StringComparer.OrdinalIgnoreCase)));

    Assert.Equal(ResourceOrigin.Required, graph.Nodes["visual-studio"].Origin);
    Assert.Equal(ResourceOrigin.AutoDependency, graph.Nodes["resharper"].Origin);
    Assert.Equal(ResourceOrigin.SelectedOptional, graph.Nodes["resharper-settings"].Origin);
    Assert.Equal(
        [["git"], ["visual-studio"], ["resharper"], ["resharper-settings"]],
        graph.TopologicalLayers.Select(layer => layer.ResourceIds).ToArray());
  }

  [Fact]
  public void Build_DefaultSelectedOptionalResource_IsIncluded()
  {
    var graph = CreateBuilder().Build(Profile(), new ProfileSelection());

    Assert.Equal(ResourceOrigin.SelectedOptional, graph.Nodes["terminal"].Origin);
    Assert.DoesNotContain("resharper", graph.Nodes.Keys);
  }

  [Fact]
  public void Build_ExplicitEmptySelection_CancelsDefaultSelectedOptionalResources()
  {
    var graph = CreateBuilder().Build(Profile(), EmptySelection());

    Assert.DoesNotContain("terminal", graph.Nodes.Keys);
    Assert.Equal(ResourceOrigin.Required, graph.Nodes["visual-studio"].Origin);
  }

  [Fact]
  public void Build_SelectionAndGraphIds_AreCaseInsensitiveAndCanonical()
  {
    var graph = CreateBuilder().Build(Profile(), new ProfileSelection(
        new HashSet<string>(StringComparer.Ordinal) { "RESHARPER-SETTINGS" }));

    Assert.Contains("resharper-settings", graph.Nodes.Keys);
    Assert.Equal(
        "resharper-settings",
        graph.TopologicalLayers.SelectMany(layer => layer.ResourceIds).Single(
            id => id.Equals("resharper-settings", StringComparison.OrdinalIgnoreCase)));
  }

  [Fact]
  public void Build_SharedDependency_IsDeduplicatedAndTracksEveryDependent()
  {
    var graph = CreateBuilder().Build(Profile(), new ProfileSelection(
        new HashSet<string>(["resharper", "resharper-settings"], StringComparer.OrdinalIgnoreCase)));

    Assert.Equal(4, graph.Nodes.Count);
    Assert.Equal(ResourceOrigin.SelectedOptional, graph.Nodes["resharper"].Origin);
    Assert.Equal(
        ["resharper", "resharper-settings"],
        graph.Nodes["visual-studio"].RequiredBy.Order(StringComparer.OrdinalIgnoreCase).ToArray());
  }

  [Fact]
  public void Build_SelectedDependency_RetainsTheStrongerSelectedOrigin()
  {
    var graph = CreateBuilder().Build(Profile(), new ProfileSelection(
        new HashSet<string>(["resharper", "resharper-settings"], StringComparer.OrdinalIgnoreCase)));

    Assert.Equal(ResourceOrigin.SelectedOptional, graph.Nodes["resharper"].Origin);
  }

  [Fact]
  public void Build_AutoDependency_AppliesItsOptionalReferenceVersionOverrides()
  {
    var source = Profile();
    var profile = source with
    {
      OptionalResources = source.OptionalResources.Select(reference =>
          reference.Id.Equals("resharper", StringComparison.OrdinalIgnoreCase)
              ? reference with
              {
                VersionConstraint = "2026.1.x",
                PreferredVersion = "2026.1.4"
              }
              : reference).ToArray()
    };

    var graph = CreateBuilder().Build(profile, new ProfileSelection(
        new HashSet<string>(["resharper-settings"], StringComparer.OrdinalIgnoreCase)));

    Assert.Equal(ResourceOrigin.AutoDependency, graph.Nodes["resharper"].Origin);
    Assert.Equal("2026.1.x", graph.Nodes["resharper"].Definition.VersionConstraint);
    Assert.Equal("2026.1.4", graph.Nodes["resharper"].Definition.PreferredVersion);
  }

  [Fact]
  public void Build_RequiredResource_RetainsTheStrongestOrigin()
  {
    var profile = Profile() with
    {
      RequiredResources =
      [
        new ProfileResourceReference { Id = "visual-studio" },
        new ProfileResourceReference { Id = "git" }
      ]
    };

    var graph = CreateBuilder().Build(profile, EmptySelection());

    Assert.Equal(ResourceOrigin.Required, graph.Nodes["git"].Origin);
  }

  [Fact]
  public void Build_AppliesReferenceVersionOverridesWithoutMutatingTheProfile()
  {
    var profile = Profile() with
    {
      RequiredResources =
      [
        new ProfileResourceReference
        {
          Id = "visual-studio",
          VersionConstraint = ">=18.0.0",
          PreferredVersion = "18.1.0"
        }
      ]
    };

    var graph = CreateBuilder().Build(profile, EmptySelection());

    Assert.Equal(">=18.0.0", graph.Nodes["visual-studio"].Definition.VersionConstraint);
    Assert.Equal("18.1.0", graph.Nodes["visual-studio"].Definition.PreferredVersion);
    Assert.Equal(">=17.0.0", profile.Resources["visual-studio"].VersionConstraint);
    Assert.Null(profile.Resources["visual-studio"].PreferredVersion);
  }

  [Fact]
  public void TryBuild_UnknownSelection_ReturnsProfileErrorAndNoExecutableLayers()
  {
    var result = CreateBuilder().TryBuild(Profile(), new ProfileSelection(
        new HashSet<string>(["unknown"], StringComparer.OrdinalIgnoreCase)));

    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.ProfileError, error.Code);
    Assert.Contains("unknown", error.Detail, StringComparison.Ordinal);
    Assert.NotNull(result.Graph);
    Assert.Empty(result.Graph.TopologicalLayers);
  }

  [Fact]
  public void TryBuild_RequiredIdPassedAsOptionalSelection_IsRejected()
  {
    var result = CreateBuilder().TryBuild(Profile(), new ProfileSelection(
        new HashSet<string>(["VISUAL-STUDIO"], StringComparer.OrdinalIgnoreCase)));

    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.ProfileError, error.Code);
    Assert.Contains("required", error.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(result.Graph!.TopologicalLayers);
  }

  [Fact]
  public void TryBuild_MissingDependency_ReturnsDependencyErrorAndNoExecutableLayers()
  {
    var profile = Profile();
    profile = profile with
    {
      Resources = profile.Resources.ToDictionary(
          pair => pair.Key,
          pair => pair.Key.Equals("visual-studio", StringComparison.OrdinalIgnoreCase)
              ? pair.Value with { Dependencies = ["missing"] }
              : pair.Value,
          StringComparer.OrdinalIgnoreCase)
    };

    var result = CreateBuilder().TryBuild(profile, EmptySelection());

    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.DependencyError, error.Code);
    Assert.Contains("missing", error.Detail, StringComparison.Ordinal);
    Assert.Empty(result.Graph!.TopologicalLayers);
  }

  [Fact]
  public void TryBuild_Cycle_ReturnsTheExactClosedDependencyPathAndNoExecutableLayers()
  {
    var result = CreateBuilder().TryBuild(CyclicProfile(), EmptySelection());

    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.DependencyError, error.Code);
    Assert.Contains("a -> b -> c -> a", error.Detail, StringComparison.Ordinal);
    Assert.Empty(result.Graph!.TopologicalLayers);
  }

  [Fact]
  public void TryBuild_DeepCycle_DoesNotOverflowTheStack()
  {
    const int resourceCount = 8_000;
    var resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < resourceCount; index++)
    {
      var id = $"resource-{index}";
      resources[id] = Resource(id, dependencies: [$"resource-{(index + 1) % resourceCount}"]);
    }

    var profile = Profile(resources, requiredId: "resource-0");

    var result = CreateBuilder().TryBuild(profile, EmptySelection());

    Assert.Equal(WdemErrorCode.DependencyError, Assert.Single(result.Errors).Code);
    Assert.Empty(result.Graph!.TopologicalLayers);
  }

  [Fact]
  public void TryBuild_UnresolvedSelectedEnvironmentValue_ReturnsProfileError()
  {
    var profile = Profile();
    profile = profile with
    {
      Resources = profile.Resources.ToDictionary(
          pair => pair.Key,
          pair => pair.Key.Equals("terminal", StringComparison.OrdinalIgnoreCase)
              ? pair.Value with
              {
                Parameters = new Dictionary<string, string?>
                {
                  ["path"] = "${WDEM_MISSING_TEST_VALUE}"
                }
              }
              : pair.Value,
          StringComparer.OrdinalIgnoreCase)
    };

    var result = new ResourceGraphBuilder(_ => null).TryBuild(profile, new ProfileSelection());

    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.ProfileError, error.Code);
    Assert.Contains("/resources/terminal/parameters/path", error.Detail, StringComparison.Ordinal);
    Assert.Empty(result.Graph!.TopologicalLayers);
  }

  [Fact]
  public void Build_ExpandsParametersAcrossTheSelectedDependencyClosure()
  {
    var profile = Profile();
    profile = profile with
    {
      Resources = profile.Resources.ToDictionary(
          pair => pair.Key,
          pair => pair.Key.Equals("visual-studio", StringComparison.OrdinalIgnoreCase)
              ? pair.Value with
              {
                Parameters = new Dictionary<string, string?> { ["channel"] = "${WDEM_VS_CHANNEL}" }
              }
              : pair.Value,
          StringComparer.OrdinalIgnoreCase)
    };

    var graph = new ResourceGraphBuilder(name => name == "WDEM_VS_CHANNEL" ? "preview" : null)
        .Build(profile, EmptySelection());

    Assert.Equal("preview", graph.Nodes["visual-studio"].Definition.Parameters["channel"]);
  }

  [Fact]
  public void Build_EmptyProfileAndSelection_ReturnsAnEmptyGraph()
  {
    var graph = CreateBuilder().Build(Profile(
        new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase),
        requiredId: null), EmptySelection());

    Assert.Empty(graph.Nodes);
    Assert.Empty(graph.TopologicalLayers);
  }

  [Fact]
  public void Build_DifferentOptionalSelection_RecomputesAutomaticDependencies()
  {
    var builder = CreateBuilder();
    var selected = builder.Build(Profile(), new ProfileSelection(
        new HashSet<string>(["resharper-settings"], StringComparer.OrdinalIgnoreCase)));
    var deselected = builder.Build(Profile(), EmptySelection());

    Assert.Contains("resharper", selected.Nodes.Keys);
    Assert.DoesNotContain("resharper", deselected.Nodes.Keys);
    Assert.DoesNotContain("resharper-settings", deselected.Nodes.Keys);
  }

  [Fact]
  public void Build_DeepSnapshotsMutableResourceInputs()
  {
    var dependencies = new List<string> { "git" };
    var parameters = new Dictionary<string, string?> { ["channel"] = "stable" };
    var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tool" };
    var profile = Profile(
        new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
        {
          ["git"] = Resource("git"),
          ["tool"] = Resource("tool") with
          {
            Dependencies = dependencies,
            Parameters = parameters
          }
        },
        requiredId: "git",
        optionalReferences: [new ProfileResourceReference { Id = "tool" }]);

    var graph = CreateBuilder().Build(profile, new ProfileSelection(selectedIds));
    dependencies.Clear();
    dependencies.Add("other");
    parameters["channel"] = "mutated";
    parameters["new"] = "value";
    selectedIds.Clear();

    Assert.Equal(["git"], graph.Nodes["tool"].Definition.Dependencies);
    Assert.Equal("stable", graph.Nodes["tool"].Definition.Parameters["channel"]);
    Assert.False(graph.Nodes["tool"].Definition.Parameters.ContainsKey("new"));
    Assert.Equal([["git"], ["tool"]],
        graph.TopologicalLayers.Select(layer => layer.ResourceIds).ToArray());
  }

  [Fact]
  public void Build_ResultCollectionsCannotBeMutatedThroughConcreteCollectionInterfaces()
  {
    var graph = CreateBuilder().Build(Profile(), new ProfileSelection(
        new HashSet<string>(["resharper-settings"], StringComparer.OrdinalIgnoreCase)));

    AssertCannotMutateDictionary(graph.Nodes, "injected", graph.Nodes["git"]);
    AssertCannotMutateList(graph.TopologicalLayers, new ResourceGraphLayer(99, ["injected"]));
    AssertCannotMutateList(graph.TopologicalLayers[0].ResourceIds, "injected");
    AssertCannotMutateSet(graph.Nodes["visual-studio"].RequiredBy, "injected");
    AssertCannotMutateList(graph.Nodes["resharper"].Definition.Dependencies, "injected");
    AssertCannotMutateDictionary(
        graph.Nodes["resharper"].Definition.Parameters,
        "injected",
        "value");

    Assert.False(graph.Nodes.ContainsKey("injected"));
    Assert.Equal([["git"], ["visual-studio"], ["resharper"], ["resharper-settings"]],
        graph.TopologicalLayers.Select(layer => layer.ResourceIds).ToArray());
  }

  [Fact]
  public void TryBuild_ErrorCollectionCannotBeMutated()
  {
    var result = CreateBuilder().TryBuild(Profile(), new ProfileSelection(
        new HashSet<string>(["unknown"], StringComparer.OrdinalIgnoreCase)));

    AssertCannotMutateList(
        result.Errors,
        new StructuredError(WdemErrorCode.ProfileError, "injected", "injected"));
    Assert.Single(result.Errors);
  }

  [Fact]
  public void TryBuild_MultipleInvalidSelections_ReturnsErrorsInStableIdOrder()
  {
    var first = CreateBuilder().TryBuild(Profile(), new ProfileSelection(
        new HashSet<string>(StringComparer.Ordinal) { "Zulu", "alpha", "BRAVO" }));
    var second = CreateBuilder().TryBuild(Profile(), new ProfileSelection(
        new HashSet<string>(StringComparer.Ordinal) { "BRAVO", "Zulu", "alpha" }));

    Assert.Equal(first.Errors.Select(error => error.Detail), second.Errors.Select(error => error.Detail));
    Assert.Collection(
        first.Errors,
        error => Assert.Contains("alpha", error.Detail, StringComparison.Ordinal),
        error => Assert.Contains("BRAVO", error.Detail, StringComparison.Ordinal),
        error => Assert.Contains("Zulu", error.Detail, StringComparison.Ordinal));
  }

  [Fact]
  public void Build_GraphErrors_ThrowsConvenienceException()
  {
    var exception = Assert.Throws<InvalidOperationException>(() =>
        CreateBuilder().Build(CyclicProfile(), EmptySelection()));

    Assert.Contains("a -> b -> c -> a", exception.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void TryBuild_NullArguments_ThrowBeforeGraphConstruction()
  {
    var builder = CreateBuilder();

    Assert.Throws<ArgumentNullException>(() => builder.TryBuild(null!, EmptySelection()));
    Assert.Throws<ArgumentNullException>(() => builder.TryBuild(Profile(), null!));
  }

  private static ResourceGraphBuilder CreateBuilder() => new(_ => null);

  private static ProfileSelection EmptySelection() => new(
      new HashSet<string>(StringComparer.OrdinalIgnoreCase));

  private static DeveloperProfile Profile() => Profile(
      new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = Resource("git"),
        ["visual-studio"] = Resource(
            "visual-studio",
            dependencies: ["git"],
            versionConstraint: ">=17.0.0"),
        ["resharper"] = Resource("resharper", dependencies: ["visual-studio"]),
        ["resharper-settings"] = Resource(
            "resharper-settings",
            dependencies: ["visual-studio", "resharper"]),
        ["terminal"] = Resource("terminal")
      },
      requiredId: "visual-studio",
      optionalReferences:
      [
        new ProfileResourceReference { Id = "resharper" },
        new ProfileResourceReference { Id = "resharper-settings" },
        new ProfileResourceReference { Id = "terminal", DefaultSelected = true }
      ]);

  private static DeveloperProfile CyclicProfile() => Profile(
      new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["a"] = Resource("a", dependencies: ["b"]),
        ["b"] = Resource("b", dependencies: ["c"]),
        ["c"] = Resource("c", dependencies: ["a"])
      },
      requiredId: "a");

  private static DeveloperProfile Profile(
      IReadOnlyDictionary<string, ResourceDefinition> resources,
      string? requiredId,
      IReadOnlyList<ProfileResourceReference>? optionalReferences = null) => new()
      {
        Id = "csharp-developer",
        Version = "1.0.0",
        DisplayName = "C# Developer",
        Description = "C# developer workstation",
        RequiredResources = requiredId is null
        ? []
        : [new ProfileResourceReference { Id = requiredId }],
        OptionalResources = optionalReferences ?? [],
        Resources = resources
      };

  private static ResourceDefinition Resource(
      string id,
      IReadOnlyList<string>? dependencies = null,
      string? versionConstraint = null) => new()
      {
        Id = id,
        Type = "package",
        Provider = "test",
        Dependencies = dependencies ?? [],
        VersionConstraint = versionConstraint
      };

  private static void AssertCannotMutateDictionary<TKey, TValue>(
      IReadOnlyDictionary<TKey, TValue> dictionary,
      TKey key,
      TValue value) where TKey : notnull
  {
    if (dictionary is IDictionary<TKey, TValue> mutable)
    {
      Assert.Throws<NotSupportedException>(() => mutable.Add(key, value));
    }
  }

  private static void AssertCannotMutateList<T>(IReadOnlyList<T> list, T value)
  {
    if (list is IList<T> mutable)
    {
      if (mutable.Count == 0)
      {
        Assert.Throws<NotSupportedException>(() => mutable.Add(value));
      }
      else
      {
        Assert.Throws<NotSupportedException>(() => mutable[0] = value);
      }
    }
  }

  private static void AssertCannotMutateSet<T>(IReadOnlySet<T> set, T value)
  {
    if (set is ISet<T> mutable)
    {
      Assert.Throws<NotSupportedException>(() => mutable.Add(value));
    }
  }
}
