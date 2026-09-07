using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Wdem.Testing;
using Xunit;

namespace Wdem.Architecture.Tests;

public sealed class ArchitectureDependencyTests
{
  [Fact]
  public void Repository_WhenProjectFrameworksAreInspected_ThenEveryProjectTargetsDotNet10()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();
    var projectFiles = Directory
        .EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
        .Where(path => !IsBuildOutput(path))
        .ToArray();

    var invalidProjects = projectFiles
        .Select(path => new
        {
          Path = path,
          Frameworks = TargetFrameworks(XDocument.Load(path))
        })
        .Where(project =>
            project.Frameworks.Length == 0 ||
            project.Frameworks.Any(framework =>
                !framework.StartsWith("net10.0", StringComparison.Ordinal)))
        .Select(project => Path.GetRelativePath(repositoryRoot, project.Path))
        .ToArray();

    Assert.NotEmpty(projectFiles);
    Assert.Empty(invalidProjects);

    using var globalJson = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(repositoryRoot, "global.json")));
    var sdkVersion = globalJson.RootElement.GetProperty("sdk").GetProperty("version").GetString();
    Assert.StartsWith("10.0.", sdkVersion, StringComparison.Ordinal);
  }

  [Fact]
  public void Repository_WhenPackageReferencesAreInspected_ThenVersionsAreCentralized()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();
    var projectFiles = Directory
        .EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
        .Where(path => !IsBuildOutput(path));
    var inlineVersions = projectFiles
        .SelectMany(path => XDocument.Load(path).Descendants("PackageReference")
            .Where(reference => reference.Attribute("Version") is not null)
            .Select(_ => Path.GetRelativePath(repositoryRoot, path)))
        .ToArray();

    Assert.Empty(inlineVersions);
    var centralPackages = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Packages.props"));
    Assert.NotEmpty(centralPackages.Descendants("PackageVersion"));
  }

  [Fact]
  public void Repository_WhenTestFixturesAreInspected_ThenConcreteFixturesAreSealedAndBasesAreAbstract()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();
    var testFiles = Directory
        .EnumerateFiles(Path.Combine(repositoryRoot, "tests"), "*.cs", SearchOption.AllDirectories)
        .Where(path => !IsBuildOutput(path));
    var fixturePattern = new Regex(
        @"public\s+(?<modifiers>(?:(?:abstract|sealed)\s+)*)class\s+(?<name>\w+(?:Tests|TestBase))\b",
        RegexOptions.CultureInvariant);
    var invalidFixtures = testFiles
        .SelectMany(path => fixturePattern.Matches(File.ReadAllText(path))
            .Where(match =>
            {
              var name = match.Groups["name"].Value;
              var modifiers = match.Groups["modifiers"].Value;
              return name.EndsWith("TestBase", StringComparison.Ordinal)
                  ? !modifiers.Contains("abstract", StringComparison.Ordinal)
                  : !modifiers.Contains("sealed", StringComparison.Ordinal) ||
                    modifiers.Contains("abstract", StringComparison.Ordinal);
            })
            .Select(match =>
                $"{Path.GetRelativePath(repositoryRoot, path)}:{match.Groups["name"].Value}"))
        .ToArray();

    Assert.Empty(invalidFixtures);
  }

  [Fact]
  public void Repository_WhenTestMethodsAreInspected_ThenUsesScenarioBasedNames()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();
    var testFiles = Directory
        .EnumerateFiles(Path.Combine(repositoryRoot, "tests"), "*Tests.cs", SearchOption.AllDirectories)
        .Where(path => !IsBuildOutput(path));
    var testMethodPattern = new Regex(
        @"\[(?:Fact|Theory)\][\s\S]*?public\s+(?:async\s+)?(?:Task|void)\s+(?<name>\w+)\s*\(",
        RegexOptions.CultureInvariant);
    var invalidMethods = testFiles
        .SelectMany(path => testMethodPattern.Matches(File.ReadAllText(path))
            .Where(match => !match.Groups["name"].Value.Contains('_', StringComparison.Ordinal))
            .Select(match =>
                $"{Path.GetRelativePath(repositoryRoot, path)}:{match.Groups["name"].Value}"))
        .ToArray();

    Assert.Empty(invalidMethods);
  }

  [Fact]
  public void TestSupportProject_WhenInspected_ThenDoesNotDependOnProductOrTestFrameworks()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();
    var testSupport = LoadProject(
        repositoryRoot,
        "tests",
        "Wdem.Testing",
        "Wdem.Testing.csproj");

    Assert.Empty(ProjectReferences(testSupport));
    Assert.Empty(PackageReferences(testSupport));
  }

  [Fact]
  public void InnerLayers_WhenDependenciesAreInspected_ThenFollowDeclaredDirection()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();
    var domain = LoadProject(repositoryRoot, "src", "Wdem.Domain", "Wdem.Domain.csproj");
    var application = LoadProject(repositoryRoot, "src", "Wdem.Application", "Wdem.Application.csproj");

    Assert.Empty(ProjectReferences(domain));
    Assert.Empty(PackageReferences(domain));
    Assert.Equal(
        [Path.Combine("..", "Wdem.Domain", "Wdem.Domain.csproj")],
        ProjectReferences(application));
    Assert.DoesNotContain(
        PackageReferences(application),
        package => package.StartsWith("Autofac", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public void ProfileIo_WhenArchitectureIsInspected_ThenUsesInfrastructureBehindApplicationPort()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();
    var infrastructure = LoadProject(
        repositoryRoot,
        "src",
        "Wdem.Infrastructure",
        "Wdem.Infrastructure.csproj");

    Assert.Equal(
        [Path.Combine("..", "Wdem.Application", "Wdem.Application.csproj")],
        ProjectReferences(infrastructure));
    Assert.True(File.Exists(Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Application",
        "Profiles",
        "IProfileRepository.cs")));
    Assert.True(File.Exists(Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Infrastructure",
        "Profiles",
        "ProfileCatalog.cs")));
    Assert.True(File.Exists(Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Infrastructure",
        "Profiles",
        "ProfileParser.cs")));
  }

  [Fact]
  public void Autofac_WhenReferencesAreInspected_ThenIsOwnedByBootstrapperCompositionRoot()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();
    var bootstrapper = LoadProject(
        repositoryRoot,
        "src",
        "Wdem.Bootstrapper",
        "Wdem.Bootstrapper.csproj");
    var app = LoadProject(repositoryRoot, "src", "Wdem.App", "Wdem.App.csproj");
    var cli = LoadProject(repositoryRoot, "src", "Wdem.Cli", "Wdem.Cli.csproj");

    Assert.Contains(
        PackageReferences(bootstrapper),
        package => package.Equals("Autofac", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(
        ProjectReferences(app),
        reference => reference.EndsWith(
            "Wdem.Bootstrapper.csproj",
            StringComparison.OrdinalIgnoreCase));
    Assert.Contains(
        ProjectReferences(cli),
        reference => reference.EndsWith(
            "Wdem.Bootstrapper.csproj",
            StringComparison.OrdinalIgnoreCase));

    var projectsWithAutofac = Directory
        .EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories)
        .Where(project => PackageReferences(XDocument.Load(project)).Any(package =>
            package.StartsWith("Autofac", StringComparison.OrdinalIgnoreCase)))
        .Select(project => Path.GetFileNameWithoutExtension(project))
        .ToArray();

    Assert.Equal(["Wdem.Bootstrapper"], projectsWithAutofac);
  }

  [Fact]
  public void RuntimeAndCommands_WhenArchitectureIsInspected_ThenBelongToApplicationAndDomain()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();
    var application = LoadProject(
        repositoryRoot,
        "src",
        "Wdem.Application",
        "Wdem.Application.csproj");
    var windows = LoadProject(repositoryRoot, "src", "Wdem.Windows", "Wdem.Windows.csproj");

    Assert.Empty(PackageReferences(application));
    Assert.Contains(
        ProjectReferences(windows),
        reference => reference.EndsWith(
            "Wdem.Application.csproj",
            StringComparison.OrdinalIgnoreCase));
    Assert.True(File.Exists(Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Domain",
        "Tasks",
        "CommandDefinition.cs")));
    var legacyRuntimeDirectory = Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Core",
        "Runtime");
    Assert.False(
        Directory.Exists(legacyRuntimeDirectory) &&
        Directory.EnumerateFiles(legacyRuntimeDirectory, "*.cs").Any());
  }

  [Theory]
  [InlineData("Execution/TaskExecutionState.cs")]
  [InlineData("Execution/TaskOutcome.cs")]
  [InlineData("Events/IDomainEvent.cs")]
  [InlineData("Events/TaskWorkflowEvents.cs")]
  [InlineData("Workflows/TaskWorkflowTransition.cs")]
  [InlineData("Workflows/TaskWorkflowTransitionContext.cs")]
  [InlineData("Workflows/WorkflowActivityLocation.cs")]
  public void WorkflowVocabulary_WhenArchitectureIsInspected_ThenBelongsToDomain(string relativePath)
  {
    var repositoryRoot = RepositoryLocator.FindRoot();
    var domainPath = Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Domain",
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    Assert.True(File.Exists(domainPath), $"Expected Domain source '{domainPath}'.");
  }

  [Fact]
  public void TaskDefinition_WhenArchitectureIsInspected_ThenBelongsToDomainWithoutRuntimeState()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();
    var domainTask = Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Domain",
        "Tasks",
        "TaskDefinition.cs");
    var legacyTask = Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Core",
        "Tasks",
        "TaskDefinition.cs");

    Assert.True(File.Exists(domainTask));
    Assert.False(File.Exists(legacyTask));
    Assert.DoesNotContain("Wdem.Core", File.ReadAllText(domainTask), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("src/Wdem.Domain/Model/IAggregateRoot.cs")]
  [InlineData("src/Wdem.Domain/Workflows/TaskWorkflowDefinition.cs")]
  [InlineData("src/Wdem.Domain/Workflows/TaskWorkflowState.cs")]
  [InlineData("src/Wdem.Domain/Workflows/WorkflowActivity.cs")]
  [InlineData("src/Wdem.Application/Workflows/IWorkflowActivityExecutor.cs")]
  [InlineData("src/Wdem.Application/Execution/StepReport.cs")]
  public void WorkflowTypes_WhenArchitectureIsInspected_ThenSeparateDefinitionAndExecution(string relativePath)
  {
    var path = Path.Combine(
        RepositoryLocator.FindRoot(),
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    Assert.True(File.Exists(path), $"Expected architecture source '{path}'.");
  }

  [Fact]
  public void DomainEvents_WhenArchitectureIsInspected_ThenFollowLayerBoundaries()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();

    Assert.True(File.Exists(Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Domain",
        "Events",
        "TaskWorkflowEvents.cs")));
    Assert.True(File.Exists(Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Application",
        "Events",
        "DomainEventPublisher.cs")));
    Assert.True(File.Exists(Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Infrastructure",
        "Logging",
        "DomainEventSessionLogHandler.cs")));
  }

  [Theory]
  [InlineData("src/Wdem.Domain/Profiles/EnvironmentProfile.cs")]
  [InlineData("src/Wdem.Application/Planning/CreatePlanHandler.cs")]
  [InlineData("src/Wdem.Application/Inspection/InspectEnvironmentHandler.cs")]
  [InlineData("src/Wdem.Application/Inspection/InspectReport.cs")]
  [InlineData("src/Wdem.Application/Execution/WorkflowProgress.cs")]
  public void ProfileUseCases_WhenArchitectureIsInspected_ThenFollowLayerBoundaries(string relativePath)
  {
    var path = Path.Combine(
        RepositoryLocator.FindRoot(),
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    Assert.True(File.Exists(path), $"Expected architecture source '{path}'.");
  }

  [Fact]
  public void ExecutionCoordination_WhenArchitectureIsInspected_ThenBelongsToApplication()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();

    Assert.True(File.Exists(Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Application",
        "Execution",
        "ApplyPlanHandler.cs")));
    Assert.True(File.Exists(Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Application",
        "Execution",
        "WorkflowStateMachine.cs")));
    Assert.False(File.Exists(Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Core",
        "Wdem.Core.csproj")));
  }

  [Theory]
  [InlineData("src/Wdem.Application/Profiles/IProfileTrustStore.cs")]
  [InlineData("src/Wdem.Application/Logging/ISessionLog.cs")]
  [InlineData("src/Wdem.Infrastructure/Configuration/WdemUserSettingsStore.cs")]
  [InlineData("src/Wdem.Infrastructure/Logging/JsonLineSessionLog.cs")]
  public void PersistenceAdapters_WhenArchitectureIsInspected_ThenFollowApplicationPorts(string relativePath)
  {
    var path = Path.Combine(
        RepositoryLocator.FindRoot(),
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    Assert.True(File.Exists(path), $"Expected architecture source '{path}'.");
  }

  [Theory]
  [InlineData("src/Wdem.Infrastructure/Profiles/ProfileDocument.cs")]
  [InlineData("src/Wdem.Infrastructure/Profiles/ProfileDocumentDeserializer.cs")]
  [InlineData("src/Wdem.Infrastructure/Profiles/ProfileDocumentMapper.cs")]
  [InlineData("src/Wdem.Infrastructure/Profiles/HttpProfileDocumentSource.cs")]
  [InlineData("src/Wdem.Infrastructure/Profiles/ProfileDocumentCache.cs")]
  public void ProfileParsing_WhenArchitectureIsInspected_ThenUsesFocusedComponents(string relativePath)
  {
    var path = Path.Combine(
        RepositoryLocator.FindRoot(),
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    Assert.True(File.Exists(path), $"Expected architecture source '{path}'.");
  }

  [Theory]
  [InlineData("src/Wdem.Domain", "System.Diagnostics.Process")]
  [InlineData("src/Wdem.Domain", "System.IO.File")]
  [InlineData("src/Wdem.Domain", "System.Net.Http")]
  [InlineData("src/Wdem.Application", "System.Diagnostics.Process")]
  [InlineData("src/Wdem.Application", "System.IO.File")]
  [InlineData("src/Wdem.Application", "System.Net.Http")]
  public void InnerLayers_WhenArchitectureIsInspected_ThenAvoidPeripheralApis(
      string relativeDirectory,
      string forbiddenText)
  {
    var directory = Path.Combine(
        RepositoryLocator.FindRoot(),
        relativeDirectory.Replace('/', Path.DirectorySeparatorChar));

    var offendingFiles = Directory
        .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
        .Where(path => !IsBuildOutput(path))
        .Where(path => File.ReadAllText(path).Contains(forbiddenText, StringComparison.Ordinal))
        .Select(Path.GetFileName)
        .ToArray();

    Assert.Empty(offendingFiles);
  }

  private static XDocument LoadProject(string root, params string[] path) =>
      XDocument.Load(Path.Combine([root, .. path]));

  private static string[] ProjectReferences(XDocument project) =>
      project.Descendants("ProjectReference")
          .Select(reference => reference.Attribute("Include")?.Value)
          .Where(value => value is not null)
          .Cast<string>()
          .ToArray();

  private static string[] PackageReferences(XDocument project) =>
      project.Descendants("PackageReference")
          .Select(reference => reference.Attribute("Include")?.Value)
          .Where(value => value is not null)
          .Cast<string>()
          .ToArray();

  private static string[] TargetFrameworks(XDocument project) =>
      project.Descendants()
          .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
          .SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
          .Select(framework => framework.Trim())
          .ToArray();

  private static bool IsBuildOutput(string path) =>
      path.Split(Path.DirectorySeparatorChar).Any(segment =>
          segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
          segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

}
