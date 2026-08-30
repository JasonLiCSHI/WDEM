using System.Collections.ObjectModel;
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
  private string? _errorMessage;
  private ProfileSelectionItemViewModel? _selectedProfile;

  public ProfileSelectionViewModel(
      IProfileCatalog catalog,
      Action<DeveloperProfile> selectProfile,
      Action<Exception>? onError = null)
  {
    ArgumentNullException.ThrowIfNull(catalog);
    ArgumentNullException.ThrowIfNull(selectProfile);
    _catalog = catalog;
    _selectProfile = selectProfile;
    Profiles = new ObservableCollection<ProfileSelectionItemViewModel>();
    LoadCommand = new AsyncRelayCommand(_ => LoadAsync(), onError: ReportError(onError));
    SelectProfileCommand = new AsyncRelayCommand(
        _ => SelectAsync(),
        _ => SelectedProfile is not null,
        ReportError(onError));
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
    ErrorMessage = Profiles.Count > 0
        ? null
        : "未找到可用的 C# Developer 配置文件。";
  }

  private Task SelectAsync()
  {
    if (SelectedProfile is not null)
    {
      _selectProfile(SelectedProfile.Profile);
    }

    return Task.CompletedTask;
  }

  private Action<Exception> ReportError(Action<Exception>? onError) => exception =>
  {
    ErrorMessage = "无法加载配置文件。";
    onError?.Invoke(exception);
  };
}
