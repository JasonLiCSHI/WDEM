using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Core.Resources;
using Wdem.Desktop.ViewModels;
using Xunit;

namespace Wdem.Desktop.Tests.ViewModels;

public sealed class ResourceSelectionViewModelTests
{
  [Fact]
  public void RequiredResourceCannotBeDeselected()
  {
    var viewModel = new ResourceSelectionViewModel(Profile(), new ResourceGraphBuilder(_ => null));
    ResourceSelectionItemViewModel required = viewModel.Resources.Single(item => item.Id == "visual-studio");

    required.IsSelected = false;

    Assert.True(required.IsSelected);
    Assert.False(required.CanChangeSelection);
    Assert.Equal(ResourceOrigin.Required, required.Origin);
  }

  [Fact]
  public void SelectingResourceRecalculatesAutomaticDependenciesWithCoreGraphBuilder()
  {
    var viewModel = new ResourceSelectionViewModel(Profile(), new ResourceGraphBuilder(_ => null));

    viewModel.Resources.Single(item => item.Id == "resharper-settings").IsSelected = true;

    ResourceSelectionItemViewModel reSharper =
        viewModel.Resources.Single(item => item.Id == "resharper");
    ResourceSelectionItemViewModel visualStudio =
        viewModel.Resources.Single(item => item.Id == "visual-studio");
    Assert.True(reSharper.IsSelected);
    Assert.Equal(ResourceOrigin.AutoDependency, reSharper.Origin);
    Assert.False(reSharper.CanChangeSelection);
    Assert.True(visualStudio.IsSelected);
  }

  [Fact]
  public async Task PlanNavigationCarriesTheResolvedSelectionSnapshot()
  {
    ResourceSelectionNavigationRequest? request = null;
    var viewModel = new ResourceSelectionViewModel(
        Profile(),
        new ResourceGraphBuilder(_ => null),
        navigationRequest => request = navigationRequest);
    viewModel.Resources.Single(item => item.Id == "resharper-settings").IsSelected = true;

    await ((AsyncRelayCommand)viewModel.CheckEnvironmentCommand).ExecuteAsync(null);

    Assert.NotNull(request);
    Assert.Equal(ResourceSelectionAction.CheckEnvironment, request.Action);
    Assert.Contains("resharper-settings", request.Selection.SelectedOptionalResourceIds!);
    Assert.DoesNotContain("resharper", request.Selection.SelectedOptionalResourceIds!);
    Assert.Equal(ResourceOrigin.AutoDependency, request.Graph.Nodes["resharper"].Origin);
  }

  private static DeveloperProfile Profile() => new()
  {
    Id = "csharp-developer",
    Version = "1.0.0",
    DisplayName = "C# Developer",
    Description = "C# developer workstation",
    RequiredResources = [new ProfileResourceReference { Id = "visual-studio" }],
    OptionalResources =
    [
      new ProfileResourceReference { Id = "resharper" },
      new ProfileResourceReference { Id = "resharper-settings" }
    ],
    Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
    {
      ["visual-studio"] = Resource("visual-studio", "Visual Studio"),
      ["resharper"] = Resource("resharper", "ReSharper", ["visual-studio"]),
      ["resharper-settings"] = Resource(
          "resharper-settings",
          "ReSharper settings",
          ["resharper"])
    }
  };

  private static ResourceDefinition Resource(
      string id,
      string displayName,
      IReadOnlyList<string>? dependencies = null) => new()
      {
        Id = id,
        Type = id,
        Provider = "test",
        DisplayName = displayName,
        Dependencies = dependencies ?? []
      };
}
