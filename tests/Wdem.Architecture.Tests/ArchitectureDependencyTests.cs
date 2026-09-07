using System.Xml.Linq;
using Xunit;

namespace Wdem.Architecture.Tests;

public sealed class ArchitectureDependencyTests
{
  [Fact]
  public void InnerLayersExistWithTheDeclaredDependencyDirection()
  {
    var repositoryRoot = FindRepositoryRoot();
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
  public void ProfileIoIsImplementedByInfrastructureBehindAnApplicationPort()
  {
    var repositoryRoot = FindRepositoryRoot();
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
  public void AutofacIsOwnedByTheBootstrapperCompositionRoot()
  {
    var repositoryRoot = FindRepositoryRoot();
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
  public void RuntimePortBelongsToApplicationAndCommandDefinitionBelongsToDomain()
  {
    var repositoryRoot = FindRepositoryRoot();
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
  [InlineData("Workflows/TaskWorkflowTransition.cs")]
  [InlineData("Workflows/TaskWorkflowTransitionContext.cs")]
  [InlineData("Workflows/WorkflowActivityLocation.cs")]
  public void WorkflowLifecycleVocabularyBelongsToDomain(string relativePath)
  {
    var repositoryRoot = FindRepositoryRoot();
    var domainPath = Path.Combine(
        repositoryRoot,
        "src",
        "Wdem.Domain",
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    Assert.True(File.Exists(domainPath), $"Expected Domain source '{domainPath}'.");
  }

  [Fact]
  public void TaskDefinitionBelongsToDomainAndDoesNotOwnExecutableWorkflowState()
  {
    var repositoryRoot = FindRepositoryRoot();
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
  [InlineData("src/Wdem.Domain/Workflows/TaskWorkflowDefinition.cs")]
  [InlineData("src/Wdem.Domain/Workflows/TaskWorkflowState.cs")]
  [InlineData("src/Wdem.Domain/Workflows/WorkflowActivity.cs")]
  [InlineData("src/Wdem.Application/Workflows/IWorkflowActivityExecutor.cs")]
  [InlineData("src/Wdem.Application/Execution/StepReport.cs")]
  public void WorkflowDefinitionsAndExecutionAreSeparated(string relativePath)
  {
    var path = Path.Combine(
        FindRepositoryRoot(),
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    Assert.True(File.Exists(path), $"Expected architecture source '{path}'.");
  }

  [Theory]
  [InlineData("src/Wdem.Domain/Profiles/EnvironmentProfile.cs")]
  [InlineData("src/Wdem.Application/Planning/CreatePlanHandler.cs")]
  [InlineData("src/Wdem.Application/Inspection/InspectEnvironmentHandler.cs")]
  [InlineData("src/Wdem.Application/Inspection/InspectReport.cs")]
  [InlineData("src/Wdem.Application/Execution/WorkflowProgress.cs")]
  public void ProfileUseCasesFollowTheLayerBoundaries(string relativePath)
  {
    var path = Path.Combine(
        FindRepositoryRoot(),
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    Assert.True(File.Exists(path), $"Expected architecture source '{path}'.");
  }

  [Fact]
  public void ExecutionCoordinationBelongsToApplicationAndCoreIsRemoved()
  {
    var repositoryRoot = FindRepositoryRoot();

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
  [InlineData("src/Wdem.Domain", "System.Diagnostics.Process")]
  [InlineData("src/Wdem.Domain", "System.IO.File")]
  [InlineData("src/Wdem.Domain", "System.Net.Http")]
  [InlineData("src/Wdem.Application", "System.Diagnostics.Process")]
  [InlineData("src/Wdem.Application", "System.IO.File")]
  [InlineData("src/Wdem.Application", "System.Net.Http")]
  public void InnerLayersDoNotUsePeripheralApis(string relativeDirectory, string forbiddenText)
  {
    var directory = Path.Combine(
        FindRepositoryRoot(),
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

  private static bool IsBuildOutput(string path) =>
      path.Split(Path.DirectorySeparatorChar).Any(segment =>
          segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
          segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      if (File.Exists(Path.Combine(directory.FullName, "Wdem.slnx")))
      {
        return directory.FullName;
      }
      directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Unable to locate the WDEM repository root.");
  }
}
