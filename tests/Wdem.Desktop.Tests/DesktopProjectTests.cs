using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Wdem.Desktop;
using Xunit;

namespace Wdem.Desktop.Tests;

public sealed class DesktopProjectTests
{
    [Fact]
    public void AppUsesWinUiApplication()
    {
        Assert.True(typeof(App).IsSubclassOf(typeof(Application)));
    }

    [Fact]
    public void MainWindowUsesWinUiWindow()
    {
        Assert.True(typeof(MainWindow).IsSubclassOf(typeof(Window)));
    }

    [Fact]
    public void DesktopProjectEnablesWinUiWithoutWpf()
    {
        XDocument project = XDocument.Load(GetDesktopProjectPath());

        Assert.Equal("true", project.Descendants("UseWinUI").SingleOrDefault()?.Value);
        Assert.Empty(project.Descendants("UseWPF"));
    }

    private static string GetDesktopProjectPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "Wdem.Desktop", "Wdem.Desktop.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate the Wdem.Desktop project file.");
    }
}
