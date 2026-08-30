using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wdem.Desktop.ViewModels;
using Wdem.Desktop.Views;

namespace Wdem.Desktop;

public partial class MainWindow : Window
{
  private MainWindowViewModel? _viewModel;
  private bool _isSynchronizingNavigation;

  public MainWindow(Func<MainWindowViewModel> viewModelFactory)
  {
    ArgumentNullException.ThrowIfNull(viewModelFactory);
    InitializeComponent();
    _viewModel = viewModelFactory();
    RootNavigation.DataContext = _viewModel;
    _viewModel.PropertyChanged += (_, args) =>
    {
      if (args.PropertyName == nameof(MainWindowViewModel.CurrentPage))
      {
        ShowCurrentPage();
      }
    };
    ShowCurrentPage();
  }

  public MainWindowViewModel DataContext =>
      _viewModel ?? throw new InvalidOperationException("The window view model is not initialized.");

  private void NavigationView_SelectionChanged(
      NavigationView sender,
      NavigationViewSelectionChangedEventArgs args)
  {
    if (_isSynchronizingNavigation ||
        _viewModel is null ||
        args.SelectedItemContainer?.Tag is not string tag)
    {
      return;
    }

    if (tag == "profiles")
    {
      _viewModel.NavigateToProfilesCommand.Execute(null);
    }
    else if (tag == "resources")
    {
      _viewModel.NavigateToResourcesCommand.Execute(null);
    }
  }

  private void ShowCurrentPage()
  {
    object currentPage = DataContext.CurrentPage;
    PageHost.Content = currentPage switch
    {
      ProfileSelectionViewModel viewModel => new ProfileSelectionView { DataContext = viewModel },
      ResourceSelectionViewModel viewModel => new ResourceSelectionView { DataContext = viewModel },
      PlanPagePlaceholderViewModel viewModel => CreatePlanPlaceholder(viewModel),
      _ => throw new InvalidOperationException("The current page type is not supported.")
    };

    _isSynchronizingNavigation = true;
    RootNavigation.SelectedItem = currentPage is ProfileSelectionViewModel
        ? ProfilesNavigationItem
        : ResourcesNavigationItem;
    _isSynchronizingNavigation = false;
  }

  private static FrameworkElement CreatePlanPlaceholder(PlanPagePlaceholderViewModel viewModel) =>
      new StackPanel
      {
        Padding = new Thickness(32),
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = viewModel.Title, FontSize = 28 },
            new TextBlock { Text = viewModel.Message, TextWrapping = TextWrapping.Wrap }
        }
      };
}
