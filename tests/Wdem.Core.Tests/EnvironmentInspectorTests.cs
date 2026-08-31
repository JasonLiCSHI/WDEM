using Wdem.Core.Profiles;
using Wdem.Core.Runs;
using Wdem.Core.Tests.TestDoubles;
using Xunit;

namespace Wdem.Core.Tests;

public sealed class EnvironmentInspectorTests
{
  [Fact]
  public async Task Inspect_MarksSatisfiedWhenDetectSucceedsAndConstraintMatches()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var runtime = new FakeRuntime()
        .WithDetect("git", exitCode: 0, stdout: "git version 2.52.0.windows.1");

    var report = await EnvironmentInspector.InspectAsync(profile, runtime);

    Assert.True(report.Tasks["git"].IsSatisfied);
    Assert.Equal(TaskComplianceState.Satisfied, report.Tasks["git"].Compliance);
    Assert.Equal("2.52.0.windows.1", report.Tasks["git"].DetectedVersion);
  }

  [Fact]
  public async Task Inspect_RequiresUpgradeWhenDetectedVersionIsBelowMinimum()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var runtime = new FakeRuntime()
        .WithDetect("git", exitCode: 0, stdout: "git version 2.40.0");

    var report = await EnvironmentInspector.InspectAsync(profile, runtime);

    Assert.False(report.Tasks["git"].IsSatisfied);
    Assert.Equal(TaskComplianceState.UpgradeRequired, report.Tasks["git"].Compliance);
    Assert.Equal(">= 2.50", report.Tasks["git"].VersionRequirement);
  }

  [Fact]
  public async Task Inspect_MarksNotSatisfiedWhenDetectFails()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var runtime = new FakeRuntime()
        .WithDetect("git", exitCode: 1, stdout: "not found");

    var report = await EnvironmentInspector.InspectAsync(profile, runtime);

    Assert.False(report.Tasks["git"].DetectSucceeded);
    Assert.False(report.Tasks["git"].IsSatisfied);
    Assert.Equal(TaskComplianceState.Missing, report.Tasks["git"].Compliance);
  }

  [Fact]
  public async Task Inspect_DistinguishesNonMinimumVersionMismatchFromUpgrade()
  {
    var profile = ProfileParser.Parse(ProfileJson.Replace(">= 2.50", "= 2.50"));
    var runtime = new FakeRuntime()
        .WithDetect("git", exitCode: 0, stdout: "git version 2.51.0");

    var report = await EnvironmentInspector.InspectAsync(profile, runtime);

    Assert.Equal(TaskComplianceState.VersionMismatch, report.Tasks["git"].Compliance);
  }

  [Fact]
  public async Task Inspect_ReportsTaskDetectionProgress()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var runtime = new FakeRuntime()
        .WithDetect("git", exitCode: 0, stdout: "git version 2.52.0");
    var updates = new List<WorkflowProgress>();
    var progress = new InlineProgress<WorkflowProgress>(updates.Add);

    await EnvironmentInspector.InspectAsync(profile, runtime, progress);

    Assert.Collection(
        updates,
        update => Assert.Equal(TaskExecutionState.Ready, update.State),
        update =>
        {
          Assert.Equal(TaskExecutionState.Detecting, update.State);
          Assert.Equal("detect", update.Stage);
        },
        update =>
        {
          Assert.Equal(TaskExecutionState.Satisfied, update.State);
          Assert.Equal(100, update.Percent);
        });
  }

  [Fact]
  public async Task Inspect_CancellationStopsTheActiveDetectAndPropagatesCancellation()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var runtime = new FakeRuntime().WithDetectThatWaitsForCancellation("git");
    using var cancellation = new CancellationTokenSource();

    var inspection = EnvironmentInspector.InspectAsync(profile, runtime, cancellation.Token);
    await runtime.WaitForCommandStartAsync("git", "detect");
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection);
    Assert.Single(runtime.Invocations);
  }

  private const string ProfileJson = """
    {
      "id": "inspect",
      "version": "1.0.0",
      "displayName": "Inspect",
      "tasks": {
        "git": {
          "displayName": "Git",
          "required": true,
          "version": ">= 2.50",
          "detect": {
            "executable": "git",
            "arguments": ["--version"],
            "versionPattern": "git version (?<version>\\d+(?:\\.\\d+)+(?:\\.[a-zA-Z0-9]+)*)"
          }
        }
      }
    }
    """;
}
