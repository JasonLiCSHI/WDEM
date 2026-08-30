using System.Collections.ObjectModel;
using Wdem.Core.Execution;
using Wdem.Core.Profiles;

namespace Wdem.Desktop.ViewModels;

public sealed class ProfileSelectionItemViewModel
{
  internal ProfileSelectionItemViewModel(DeveloperProfile profile)
  {
    Profile = profile;
  }

  internal DeveloperProfile Profile { get; }

  public string Id => Profile.Id;

  public string DisplayName => Profile.DisplayName;

  public string Description => Profile.Description;

  public string Version => Profile.Version;

  public string VersionDisplay => $"版本 {Version}";

  public bool IsEnabled => true;
}

public sealed class ProfileSelectionViewModel : ObservableObject
{
  private const string DeliveredProfileId = "csharp-developer";
  private readonly IProfileCatalog _catalog;
  private readonly Action<DeveloperProfile> _selectProfile;
  private readonly Func<Exception, string> _reportError;
  private readonly Action _clearError;
  private string? _errorMessage;
  private ProfileSelectionItemViewModel? _selectedProfile;

  public ProfileSelectionViewModel(
      IProfileCatalog catalog,
      Action<DeveloperProfile> selectProfile,
      Func<Exception, string>? reportError = null,
      Action? clearError = null)
  {
    ArgumentNullException.ThrowIfNull(catalog);
    ArgumentNullException.ThrowIfNull(selectProfile);
    _catalog = catalog;
    _selectProfile = selectProfile;
    _reportError = reportError ?? UserErrorMessageFormatter.Format;
    _clearError = clearError ?? (() => { });
    Profiles = new ObservableCollection<ProfileSelectionItemViewModel>();
    LoadCommand = new AsyncRelayCommand(_ => LoadAsync(), onError: ReportError);
    SelectProfileCommand = new AsyncRelayCommand(
        _ => SelectAsync(),
        _ => SelectedProfile is not null,
        ReportError);
  }

  public ObservableCollection<ProfileSelectionItemViewModel> Profiles { get; }

  public ProfileSelectionItemViewModel? SelectedProfile
  {
    get => _selectedProfile;
    set
    {
      if (SetProperty(ref _selectedProfile, value))
      {
        SelectProfileCommand.RaiseCanExecuteChanged();
      }
    }
  }

  public string? ErrorMessage
  {
    get => _errorMessage;
    private set => SetProperty(ref _errorMessage, value);
  }

  public AsyncRelayCommand LoadCommand { get; }

  public AsyncRelayCommand SelectProfileCommand { get; }

  public async Task LoadAsync()
  {
    ClearErrors();
    IReadOnlyList<ProfileLoadResult> results = await _catalog.LoadAllAsync();
    Profiles.Clear();
    foreach (ProfileLoadResult result in results.Where(result =>
                 result.IsValid &&
                 string.Equals(
                     result.Profile!.Id,
                     DeliveredProfileId,
                     StringComparison.OrdinalIgnoreCase)))
    {
      Profiles.Add(new ProfileSelectionItemViewModel(result.Profile!));
    }

    SelectedProfile = Profiles.FirstOrDefault();
    if (Profiles.Count == 0)
    {
      StructuredError? error = results
          .Where(IsDeliveredProfileResult)
          .SelectMany(result => result.Errors)
          .FirstOrDefault();
      if (error is not null)
      {
        ReportError(new StructuredErrorException(error));
      }
      else
      {
        ReportError(new UserMessageException("未找到可用的 C# Developer 配置文件。"));
      }
    }
  }

  private Task SelectAsync()
  {
    ClearErrors();
    if (SelectedProfile is not null)
    {
      _selectProfile(SelectedProfile.Profile);
    }

    return Task.CompletedTask;
  }

  private void ReportError(Exception exception)
  {
    ErrorMessage = _reportError(exception);
  }

  private void ClearErrors()
  {
    ClearError();
    _clearError();
  }

  internal void ClearError() => ErrorMessage = null;

  private static bool IsDeliveredProfileResult(ProfileLoadResult result)
  {
    if (result.Profile is not null)
    {
      return string.Equals(
          result.Profile.Id,
          DeliveredProfileId,
          StringComparison.OrdinalIgnoreCase);
    }

    return string.Equals(
        Path.GetFileNameWithoutExtension(result.SourcePath),
        DeliveredProfileId,
        StringComparison.OrdinalIgnoreCase);
  }
}
