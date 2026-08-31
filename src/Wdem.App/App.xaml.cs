using System.Windows;

namespace Wdem.App;

public partial class App : Application
{
  protected override void OnStartup(StartupEventArgs e)
  {
    I18n.Initialize(Resources);
    base.OnStartup(e);
    MainWindow = new MainWindow();
    MainWindow.Show();
  }
}
