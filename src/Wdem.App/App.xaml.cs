using System.Windows;
using Wdem.Bootstrapper;
using Wdem.Windows.Security;

namespace Wdem.App;

public partial class App : System.Windows.Application
{
  private WdemSession? _session;

  protected override void OnStartup(StartupEventArgs e)
  {
    I18n.Initialize(Resources);
    base.OnStartup(e);

    if (!AdministratorRequirement.IsSatisfied())
    {
      MessageBox.Show(
          I18n.Get("AdministratorRequiredMessage"),
          I18n.Get("AdministratorRequiredTitle"),
          MessageBoxButton.OK,
          MessageBoxImage.Warning);
      Shutdown(AdministratorRequirement.AccessDeniedExitCode);
      return;
    }

    _session = WdemBootstrapper.StartSession("gui");
    MainWindow = new MainWindow(_session);
    MainWindow.Show();
  }

  protected override void OnExit(ExitEventArgs e)
  {
    _session?.Dispose();
    base.OnExit(e);
  }
}
