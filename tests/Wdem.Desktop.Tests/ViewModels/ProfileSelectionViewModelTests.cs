using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Desktop.ViewModels;
using Xunit;

namespace Wdem.Desktop.Tests.ViewModels;

public sealed class ProfileSelectionViewModelTests
{
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
    Assert.NotNull(main.ErrorMessage);

    await main.ProfileSelection.LoadCommand.ExecuteAsync(null);

    Assert.Single(main.ProfileSelection.Profiles);
    Assert.Null(main.ProfileSelection.ErrorMessage);
    Assert.Null(main.ErrorMessage);
  }

  private static DeveloperProfile Profile() => new()
  {
    Id = "csharp-developer",
    Version = "1.0.0",
    DisplayName = "C# Developer",
    Description = "C# developer workstation"
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
        CancellationToken cancellationToken = default) => Task.FromResult(results[0]);

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default) => Task.FromResult(results[0]);

    public Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) => Task.FromResult(results);
  }
}
