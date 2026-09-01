using System.Windows;
using Wdem.Windows.Security;

namespace Wdem.App;

public partial class App : Application
{
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

    MainWindow = new MainWindow();
    MainWindow.Show();
  }
}
