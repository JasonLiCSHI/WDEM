using Wdem.Core.Graph;

namespace Wdem.Desktop.ViewModels;

public sealed class ResourceSelectionItemViewModel : ObservableObject
{
  private readonly Action<ResourceSelectionItemViewModel, bool> _selectionChanged;
  private ResourceOrigin _origin;
  private bool _isSelected;
  private bool _canChangeSelection;

  internal ResourceSelectionItemViewModel(
      string id,
      string displayName,
      string description,
      Action<ResourceSelectionItemViewModel, bool> selectionChanged)
  {
    Id = id;
    DisplayName = displayName;
    Description = description;
    _selectionChanged = selectionChanged;
  }

  public string Id { get; }

  public string DisplayName { get; }

  public string Description { get; }

  public ResourceOrigin Origin
  {
    get => _origin;
    private set
    {
      if (SetProperty(ref _origin, value))
      {
        OnPropertyChanged(nameof(OriginDisplayName));
      }
    }
  }

  public string OriginDisplayName => Origin switch
  {
    ResourceOrigin.Required => "Required",
    ResourceOrigin.SelectedOptional => "Optional",
    ResourceOrigin.AutoDependency => "Auto dependency",
    _ => throw new ArgumentOutOfRangeException(nameof(Origin), Origin, "Unknown resource origin.")
  };

  public bool IsSelected
  {
    get => _isSelected;
    set
    {
      if (CanChangeSelection && value != _isSelected)
      {
        _selectionChanged(this, value);
      }
    }
  }

  public bool CanChangeSelection
  {
    get => _canChangeSelection;
    private set => SetProperty(ref _canChangeSelection, value);
  }

  internal void ApplyState(ResourceOrigin origin, bool isSelected, bool canChangeSelection)
  {
    Origin = origin;
    SetProperty(ref _isSelected, isSelected, nameof(IsSelected));
    CanChangeSelection = canChangeSelection;
  }
}
