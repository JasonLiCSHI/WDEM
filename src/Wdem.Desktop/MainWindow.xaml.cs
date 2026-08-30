using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Wdem.Desktop.ViewModels;
using Wdem.Desktop.Views;

namespace Wdem.Desktop;

public partial class MainWindow : Window
{
  private MainWindowViewModel? _viewModel;
  private bool _isSynchronizingNavigation;
  private bool _isClosing;
  private bool _allowClose;

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
    AppWindow.Closing += AppWindow_Closing;
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
      PlanViewModel viewModel => new PlanView { DataContext = viewModel },
      ExecutionMonitorViewModel viewModel => new ExecutionMonitorView { DataContext = viewModel },
      CompletionViewModel viewModel => new CompletionView(this) { DataContext = viewModel },
      _ => throw new InvalidOperationException("The current page type is not supported.")
    };

    _isSynchronizingNavigation = true;
    RootNavigation.SelectedItem = currentPage is ProfileSelectionViewModel
        ? ProfilesNavigationItem
        : ResourcesNavigationItem;
    _isSynchronizingNavigation = false;
  }

  private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
  {
    if (_allowClose)
    {
      return;
    }

    args.Cancel = true;
    if (_isClosing)
    {
      return;
    }

    _isClosing = true;
    try
    {
      await DataContext.DisposeAsync();
      _allowClose = true;
      Close();
    }
    finally
    {
      _isClosing = false;
    }
  }

}
