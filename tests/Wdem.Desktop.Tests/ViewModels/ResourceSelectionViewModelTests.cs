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

  [Fact]
  public async Task GraphFailureShowsActionableDetailAndNextSuccessClearsErrors()
  {
    var main = new MainWindowViewModel(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(_ => null));
    await main.InitializeAsync();
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);
    ResourceSelectionViewModel resources = main.ResourceSelection!;

    resources.Resources.Single(item => item.Id == "company-vs-extension").IsSelected = true;

    Assert.Contains("WDEM_COMPANY_VSIX_PATH", resources.ErrorMessage);
    Assert.Contains("required", resources.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(resources.ErrorMessage, main.ErrorMessage);

    resources.Resources.Single(item => item.Id == "resharper").IsSelected = true;

    Assert.Null(resources.ErrorMessage);
    Assert.Null(main.ErrorMessage);
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
      new ProfileResourceReference { Id = "resharper-settings" },
      new ProfileResourceReference { Id = "company-vs-extension" }
    ],
    Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
    {
      ["visual-studio"] = Resource("visual-studio", "Visual Studio"),
      ["resharper"] = Resource("resharper", "ReSharper", ["visual-studio"]),
      ["resharper-settings"] = Resource(
          "resharper-settings",
          "ReSharper settings",
          ["resharper"]),
      ["company-vs-extension"] = Resource(
          "company-vs-extension",
          "Company Visual Studio extension",
          ["visual-studio"],
          new Dictionary<string, string?>
          {
            ["sourcePath"] = "${WDEM_COMPANY_VSIX_PATH}"
          })
    }
  };

  private static ResourceDefinition Resource(
      string id,
      string displayName,
      IReadOnlyList<string>? dependencies = null,
      IReadOnlyDictionary<string, string?>? parameters = null) => new()
      {
        Id = id,
        Type = id,
        Provider = "test",
        DisplayName = displayName,
        Dependencies = dependencies ?? [],
        Parameters = parameters ?? new Dictionary<string, string?>()
      };

  private sealed class FixedProfileCatalog(DeveloperProfile profile) : IProfileCatalog
  {
    private readonly ProfileLoadResult _result = new()
    {
      Profile = profile,
      SourcePath = "csharp-developer.yaml"
    };

    public Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default) => Task.FromResult(_result);

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default) => Task.FromResult(_result);

    public Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProfileLoadResult>>([_result]);
  }
}
