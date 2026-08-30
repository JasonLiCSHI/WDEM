using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Wdem.Core.Graph;
using Wdem.Desktop.ViewModels;
using Wdem.Windows.Composition;

namespace Wdem.Desktop;

public partial class App : Application
{
  private Window? _window;
  private WdemWindowsComposition? _composition;

  public App()
  {
    InitializeComponent();
  }

  protected override async void OnLaunched(LaunchActivatedEventArgs args)
  {
    try
    {
      _composition = await WdemWindowsFactory.CreateAsync(FindProfilesDirectory());
      var window = new MainWindow(() => new MainWindowViewModel(
          _composition.Profiles,
          new ResourceGraphBuilder(),
          _composition.EnvironmentRuns,
          _composition.RunEvents,
          _composition.Redactor,
          new DispatcherQueueUiDispatcher(
              DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException(
                  "The WinUI dispatcher is unavailable."))));
      _window = window;
      _window.Activate();
      await window.DataContext.InitializeAsync();
    }
    catch (Exception)
    {
      _window = new Window
      {
        Title = "WDEM",
        Content = new TextBlock
        {
          Margin = new Thickness(32),
          Text = "WDEM 无法初始化。请确认配置文件目录可用后重试。",
          TextWrapping = TextWrapping.Wrap
        }
      };
      _window.Activate();
    }
  }

  private static string FindProfilesDirectory()
  {
    foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
    {
      DirectoryInfo? directory = new(start);
      while (directory is not null)
      {
        string candidate = Path.Combine(directory.FullName, "profiles");
        if (File.Exists(Path.Combine(candidate, "csharp-developer.yaml")))
        {
          return candidate;
        }

        directory = directory.Parent;
      }
    }

    return Path.Combine(AppContext.BaseDirectory, "profiles");
  }
}
