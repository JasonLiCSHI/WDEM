using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Core.Resources;
using Wdem.Desktop.ViewModels;
using Xunit;

namespace Wdem.Desktop.Tests.ViewModels;

public sealed class ProfileSelectionViewModelTests
{
  [Fact]
  public async Task EmptyCatalogShowsTheSameSpecificErrorInChildAndMain()
  {
    var main = new MainWindowViewModel(
        new FixedResultsProfileCatalog([]),
        new ResourceGraphBuilder(_ => null));

    await main.InitializeAsync();

    Assert.Equal(main.ProfileSelection.ErrorMessage, main.ErrorMessage);
    Assert.Contains("未找到", main.ErrorMessage, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InvalidResultShowsSameSanitizedActionableErrorInChildAndMain()
  {
    var error = new StructuredError(
        WdemErrorCode.ProfileError,
        "The profile is invalid.",
        @"Profile C:\Users\Alice\profile.yaml contains token=raw-secret.")
    {
      SuggestedAction = "Retry with Authorization: Bearer action-secret."
    };
    var catalog = new FixedResultsProfileCatalog([
      new ProfileLoadResult
      {
        SourcePath = "csharp-developer.yaml",
        Errors = [error]
      }
    ]);
    var main = new MainWindowViewModel(catalog, new ResourceGraphBuilder(_ => null));

    await main.InitializeAsync();

    Assert.Equal(main.ProfileSelection.ErrorMessage, main.ErrorMessage);
    Assert.Contains("The profile is invalid.", main.ErrorMessage, StringComparison.Ordinal);
    Assert.Contains("Profile", main.ErrorMessage, StringComparison.Ordinal);
    Assert.Contains("Retry with", main.ErrorMessage, StringComparison.Ordinal);
    Assert.DoesNotContain("Alice", main.ErrorMessage, StringComparison.Ordinal);
    Assert.DoesNotContain("raw-secret", main.ErrorMessage, StringComparison.Ordinal);
    Assert.DoesNotContain("action-secret", main.ErrorMessage, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FailedLoadCanBeRetriedAndSuccessClearsChildAndMainErrors()
  {
    var catalog = new RetryProfileCatalog(Profile());
    var main = new MainWindowViewModel(catalog, new ResourceGraphBuilder(_ => null));

    await main.InitializeAsync();

    Assert.NotNull(main.ProfileSelection.ErrorMessage);
    Assert.Equal(main.ProfileSelection.ErrorMessage, main.ErrorMessage);

    await main.ProfileSelection.LoadCommand.ExecuteAsync(null);

    Assert.Single(main.ProfileSelection.Profiles);
    Assert.Null(main.ProfileSelection.ErrorMessage);
    Assert.Null(main.ErrorMessage);
  }

  [Fact]
  public async Task GraphFailureWhileSelectingProfileUsesTheSameSafeErrorInChildAndMain()
  {
    DeveloperProfile profile = ProfileWithRequiredSource();
    var main = new MainWindowViewModel(
        new FixedResultsProfileCatalog([
          new ProfileLoadResult
          {
            Profile = profile,
            SourcePath = "csharp-developer.yaml"
          }
        ]),
        new ResourceGraphBuilder(_ => null));
    await main.InitializeAsync();

    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);

    Assert.Null(main.ResourceSelection);
    Assert.Equal(main.ProfileSelection.ErrorMessage, main.ErrorMessage);
    Assert.Contains("WDEM_COMPANY_VSIX_PATH", main.ErrorMessage, StringComparison.Ordinal);
    Assert.Contains("required", main.ErrorMessage, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task NavigatingToResourcesClearsAStaleProfileAndMainError()
  {
    bool sourceIsAvailable = true;
    DeveloperProfile profile = ProfileWithRequiredSource();
    var main = new MainWindowViewModel(
        new FixedResultsProfileCatalog([
          new ProfileLoadResult
          {
            Profile = profile,
            SourcePath = "csharp-developer.yaml"
          }
        ]),
        new ResourceGraphBuilder(_ => sourceIsAvailable ? @"C:\safe\company.vsix" : null));
    await main.InitializeAsync();
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);
    Assert.NotNull(main.ResourceSelection);
    sourceIsAvailable = false;
    await main.ProfileSelection.SelectProfileCommand.ExecuteAsync(null);
    Assert.NotNull(main.ProfileSelection.ErrorMessage);

    await main.NavigateToResourcesCommand.ExecuteAsync(null);

    Assert.Null(main.ProfileSelection.ErrorMessage);
    Assert.Null(main.ResourceSelection!.ErrorMessage);
    Assert.Null(main.ErrorMessage);
  }

  private static DeveloperProfile Profile() => new()
  {
    Id = "csharp-developer",
    Version = "1.0.0",
    DisplayName = "C# Developer",
    Description = "C# developer workstation"
  };

  private static DeveloperProfile ProfileWithRequiredSource() => new()
  {
    Id = "csharp-developer",
    Version = "1.0.0",
    DisplayName = "C# Developer",
    Description = "C# developer workstation",
    RequiredResources = [new ProfileResourceReference { Id = "company-vs-extension" }],
    Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
    {
      ["company-vs-extension"] = new ResourceDefinition
      {
        Id = "company-vs-extension",
        Type = "vsix",
        Provider = "test",
        DisplayName = "Company Visual Studio extension",
        Parameters = new Dictionary<string, string?>
        {
          ["sourcePath"] = "${WDEM_COMPANY_VSIX_PATH}"
        }
      }
    }
  };

  private sealed class RetryProfileCatalog(DeveloperProfile profile) : IProfileCatalog
  {
    private int _loadAllCount;
    private readonly ProfileLoadResult _success = new()
    {
      Profile = profile,
      SourcePath = "csharp-developer.yaml"
    };

    public Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default) => Task.FromResult(_success);

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default) => Task.FromResult(_success);

    public Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
      _loadAllCount++;
      return _loadAllCount == 1
          ? Task.FromException<IReadOnlyList<ProfileLoadResult>>(
              new IOException("The profile directory is temporarily unavailable."))
          : Task.FromResult<IReadOnlyList<ProfileLoadResult>>([_success]);
    }
  }

  private sealed class FixedResultsProfileCatalog(IReadOnlyList<ProfileLoadResult> results)
      : IProfileCatalog
  {
    public Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(results.FirstOrDefault() ?? new ProfileLoadResult { SourcePath = string.Empty });

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(results.FirstOrDefault() ?? new ProfileLoadResult { SourcePath = string.Empty });

    public Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) => Task.FromResult(results);
  }
}
