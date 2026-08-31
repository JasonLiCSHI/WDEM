using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Wdem.Desktop;
using Wdem.Tests;
using Wdem.Windows.Composition;
using Wdem.Windows.Persistence;
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
    string? useWinUi = project.Descendants("UseWinUI").SingleOrDefault()?.Value;

    Assert.True(
        string.Equals("true", useWinUi, StringComparison.OrdinalIgnoreCase),
        $"Expected UseWinUI to be true, but found '{useWinUi ?? "<missing>"}'.");
    Assert.Empty(project.Descendants("UseWPF"));
  }

  [Fact]
  public void ProfileSelectionViewBindsRetryToLoadCommand()
  {
    string desktopDirectory = Path.GetDirectoryName(GetDesktopProjectPath())!;
    XDocument view = XDocument.Load(Path.Combine(
        desktopDirectory,
        "Views",
        "ProfileSelectionView.xaml"));

    XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    Assert.Contains(
        view.Descendants(presentation + "Button"),
        button => string.Equals(
            button.Attribute("Command")?.Value,
            "{Binding LoadCommand}",
            StringComparison.Ordinal));
  }

  [Fact]
  public void ResourceAndPlanViewsBindResourceDescriptions()
  {
    string desktopDirectory = Path.GetDirectoryName(GetDesktopProjectPath())!;
    string resourceSelection = File.ReadAllText(Path.Combine(
        desktopDirectory,
        "Views",
        "ResourceSelectionView.xaml"));
    string plan = File.ReadAllText(Path.Combine(desktopDirectory, "Views", "PlanView.xaml"));

    Assert.Contains("{Binding Description}", resourceSelection, StringComparison.Ordinal);
    Assert.Contains("{Binding Description}", plan, StringComparison.Ordinal);
  }

  [Fact]
  public void ResourceAndPlanViewModelsUseSharedPresentationRedactor()
  {
    string desktopDirectory = Path.GetDirectoryName(GetDesktopProjectPath())!;
    string plan = File.ReadAllText(Path.Combine(
        desktopDirectory,
        "ViewModels",
        "PlanViewModel.cs"));
    string selection = File.ReadAllText(Path.Combine(
        desktopDirectory,
        "ViewModels",
        "ResourceSelectionViewModel.cs"));

    Assert.Contains("ResourceDefinitionPresentationRedactor.Redact", plan);
    Assert.Contains("ResourceDefinitionPresentationRedactor.Redact", selection);
  }

  [Fact]
  public void RecoveryCandidatesViewBindsSafeDetailsAndActions()
  {
    string desktopDirectory = Path.GetDirectoryName(GetDesktopProjectPath())!;
    string viewPath = Path.Combine(
        desktopDirectory,
        "Views",
        "RecoveryCandidatesView.xaml");

    Assert.True(File.Exists(viewPath), $"Missing recovery view at '{viewPath}'.");
    XDocument view = XDocument.Load(viewPath);
    XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    string document = view.ToString();

    Assert.Contains("{Binding Candidates}", document, StringComparison.Ordinal);
    Assert.Contains("{Binding SelectedCandidate, Mode=TwoWay}", document, StringComparison.Ordinal);
    Assert.Contains("{Binding RecoverCommand}", document, StringComparison.Ordinal);
    Assert.Contains("{Binding AbandonCommand}", document, StringComparison.Ordinal);
    Assert.Contains("{Binding Profile}", document, StringComparison.Ordinal);
    Assert.Contains("{Binding PendingResources}", document, StringComparison.Ordinal);
    Assert.DoesNotContain("ProfileSourcePath", document, StringComparison.Ordinal);
    Assert.Equal(2, view.Descendants(presentation + "Button").Count());
  }

  [Fact]
  public void ExecutionMonitorShowsResourceAndStepStateAndOutcome()
  {
    string desktopDirectory = Path.GetDirectoryName(GetDesktopProjectPath())!;
    XDocument view = XDocument.Load(Path.Combine(
        desktopDirectory,
        "Views",
        "ExecutionMonitorView.xaml"));
    XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    Assert.Equal(
        2,
        view.Descendants(presentation + "TextBlock")
            .Count(element => element.Attribute("Text")?.Value == "{Binding State}"));
    Assert.Equal(
        2,
        view.Descendants(presentation + "TextBlock")
            .Count(element => element.Attribute("Text")?.Value == "{Binding Outcome}"));
  }

  [Fact]
  public void ErrorMessageUsesSingleWrappedRowOutsidePageHost()
  {
    string desktopDirectory = Path.GetDirectoryName(GetDesktopProjectPath())!;
    XDocument window = XDocument.Load(Path.Combine(desktopDirectory, "MainWindow.xaml"));
    XDocument profileView = XDocument.Load(Path.Combine(
        desktopDirectory,
        "Views",
        "ProfileSelectionView.xaml"));
    XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

    XElement pageHost = Assert.Single(
        window.Descendants(presentation + "ContentControl"),
        element => element.Attribute(x + "Name")?.Value == "PageHost");
    XElement errorPresenter = Assert.Single(
        window.Descendants(presentation + "TextBlock"),
        element => element.Attribute("Text")?.Value == "{Binding ErrorMessage}");

    Assert.Equal("0", pageHost.Attribute("Grid.Row")?.Value);
    Assert.Equal("1", errorPresenter.Attribute("Grid.Row")?.Value);
    Assert.Equal("Wrap", errorPresenter.Attribute("TextWrapping")?.Value);
    Assert.Equal(
        ["*", "Auto"],
        window.Descendants(presentation + "RowDefinition")
            .Select(row => row.Attribute("Height")?.Value));
    Assert.DoesNotContain(
        profileView.Descendants(presentation + "TextBlock"),
        element => element.Attribute("Text")?.Value == "{Binding ErrorMessage}");
  }

  [Fact]
  public void ElevatedHostIsDeployedBesideDesktopOutput()
  {
    string hostPath = Path.Combine(AppContext.BaseDirectory, "Wdem.ElevatedHost.exe");

    Assert.True(File.Exists(hostPath), $"Missing elevated host at '{hostPath}'.");
  }

  [Fact]
  public void DesktopDoesNotReferenceElevatedHostAssembly()
  {
    Assert.DoesNotContain(
        typeof(App).Assembly.GetReferencedAssemblies(),
        reference => string.Equals(
            reference.Name,
            "Wdem.ElevatedHost",
            StringComparison.Ordinal));
  }

  [Fact]
  public async Task DesktopPublishIncludesRunnableSelfContainedElevatedHost()
  {
    PublishedElevatedHostResult result =
        await PublishedElevatedHostSmoke.PublishAndRunAsync(
            useBundledCliPublishOptions: false,
            "src",
            "Wdem.Desktop",
            "Wdem.Desktop.csproj");

    Assert.True(result.PublishExitCode == 0, result.PublishOutput);
    Assert.Equal(["Wdem.ElevatedHost.exe"], result.HostFiles);
    Assert.Equal(2, result.HostExitCode);
    Assert.Contains(PublishedElevatedHostSmoke.UsageError, result.HostStandardError);
    Assert.DoesNotContain("hostpolicy", result.HostStandardError, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("runtime", result.HostStandardError, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task DesktopPublishIncludesLoadableShippedProfileTree()
  {
    string projectPath = GetDesktopProjectPath();
    string repositoryRoot = Directory.GetParent(Directory.GetParent(Path.GetDirectoryName(projectPath)!)!.FullName)!.FullName;
    string testRoot = Path.Combine(
        Path.GetTempPath(),
        "wdem-desktop-profile-layout",
        Guid.NewGuid().ToString("N"));
    string publishDirectory = Path.Combine(testRoot, "publish");
    Directory.CreateDirectory(publishDirectory);

    try
    {
      TestProcessResult publish = await TestProcessRunner.RunAsync(
          Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
          repositoryRoot,
          [
            "publish", projectPath,
            "-c", "Release",
            "-o", publishDirectory,
            "--no-restore",
            "--nologo",
            "--verbosity", "minimal",
            "-m:1"
          ]);

      Assert.True(publish.ExitCode == 0, publish.Output);
      string[] expectedProfileFiles =
      [
        "csharp-developer.yaml",
        Path.Combine("assets", "csharp-developer.DotSettings"),
        Path.Combine("assets", "csharp-developer.vsconfig"),
        Path.Combine("assets", "csharp-developer.vssettings"),
        Path.Combine("schemas", "developer-profile.schema.json")
      ];
      string profilesDirectory = Path.Combine(publishDirectory, "profiles");
      Assert.All(
          expectedProfileFiles,
          relativePath => Assert.True(
              File.Exists(Path.Combine(profilesDirectory, relativePath)),
              $"Published profile file '{relativePath}' was not found."));

      WdemWindowsComposition composition = await WdemWindowsFactory.CreateAsync(
          profilesDirectory,
          new WdemDataPaths(Path.Combine(testRoot, "data")));
      var loaded = await composition.Profiles.LoadAsync("csharp-developer");

      Assert.True(loaded.IsValid, string.Join(Environment.NewLine, loaded.Errors.Select(error => error.Detail)));
      Assert.Equal("C# Developer", loaded.Profile!.DisplayName);
    }
    finally
    {
      try
      {
        Directory.Delete(testRoot, recursive: true);
      }
      catch (IOException)
      {
      }
      catch (UnauthorizedAccessException)
      {
      }
    }
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
