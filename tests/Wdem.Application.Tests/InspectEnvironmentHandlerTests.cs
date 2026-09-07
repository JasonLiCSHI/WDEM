using Wdem.Application.Execution;
using Wdem.Application.Inspection;
using Wdem.Application.Profiles;
using Wdem.Application.Tests.TestDoubles;
using Wdem.Domain.Execution;
using Wdem.Domain.Versions;
using Wdem.Infrastructure.Profiles;
using Xunit;

namespace Wdem.Application.Tests;

public sealed class InspectEnvironmentHandlerTests
{
  [Fact]
  public async Task Inspect_RejectsUntrustedRemoteProfileBeforeRunningDetect()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var loaded = new LoadedProfile(
        profile,
        ProfileOrigin.Remote,
        "https://profiles.example/test.json",
        "UNTRUSTED",
        "test-source");
    var runtime = new FakeRuntime();
    var handler = new InspectEnvironmentHandler(
        runtime,
        new ProfileExecutionAuthorizer(new FakeProfileTrustStore(trusted: false)));

    await Assert.ThrowsAsync<UntrustedProfileException>(() =>
        handler.HandleAsync(loaded));

    Assert.Empty(runtime.Invocations);
  }

  [Fact]
  public async Task Inspect_MarksSatisfiedWhenDetectSucceedsAndConstraintMatches()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var runtime = new FakeRuntime()
        .WithDetect("git", exitCode: 0, stdout: "git version 2.52.0.windows.1");

    var report = await CreateHandler(runtime).HandleAsync(Loaded(profile));

    Assert.True(report.Tasks["git"].IsSatisfied);
    Assert.Equal(ComplianceStatus.Satisfied, report.Tasks["git"].Compliance);
    Assert.Equal("2.52.0.windows.1", report.Tasks["git"].DetectedVersion);
  }

  [Fact]
  public async Task Inspect_RequiresUpgradeWhenDetectedVersionIsBelowMinimum()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var runtime = new FakeRuntime()
        .WithDetect("git", exitCode: 0, stdout: "git version 2.40.0");

    var report = await CreateHandler(runtime).HandleAsync(Loaded(profile));

    Assert.False(report.Tasks["git"].IsSatisfied);
    Assert.Equal(ComplianceStatus.UpgradeRequired, report.Tasks["git"].Compliance);
    Assert.Equal(">= 2.50", report.Tasks["git"].VersionRequirement);
  }

  [Fact]
  public async Task Inspect_MarksNotSatisfiedWhenDetectFails()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var runtime = new FakeRuntime()
        .WithDetect("git", exitCode: 1, stdout: "not found");

    var report = await CreateHandler(runtime).HandleAsync(Loaded(profile));

    Assert.False(report.Tasks["git"].DetectSucceeded);
    Assert.False(report.Tasks["git"].IsSatisfied);
    Assert.Equal(ComplianceStatus.Missing, report.Tasks["git"].Compliance);
  }

  [Fact]
  public async Task Inspect_DistinguishesNonMinimumVersionMismatchFromUpgrade()
  {
    var profile = ProfileParser.Parse(ProfileJson.Replace(">= 2.50", "= 2.50"));
    var runtime = new FakeRuntime()
        .WithDetect("git", exitCode: 0, stdout: "git version 2.51.0");

    var report = await CreateHandler(runtime).HandleAsync(Loaded(profile));

    Assert.Equal(ComplianceStatus.VersionMismatch, report.Tasks["git"].Compliance);
  }

  [Fact]
  public async Task Inspect_ReportsTaskDetectionProgress()
  {
    var profile = ProfileParser.Parse(ProfileJson);
    var runtime = new FakeRuntime()
        .WithDetect("git", exitCode: 0, stdout: "git version 2.52.0");
    var updates = new List<WorkflowProgress>();
    var progress = new InlineProgress<WorkflowProgress>(updates.Add);

    await CreateHandler(runtime).HandleAsync(Loaded(profile), progress);

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

    var inspection = CreateHandler(runtime).HandleAsync(
        Loaded(profile),
        cancellationToken: cancellation.Token);
    await runtime.WaitForCommandStartAsync("git", "detect");
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection);
    Assert.Single(runtime.Invocations);
  }

  private static InspectEnvironmentHandler CreateHandler(FakeRuntime runtime) =>
      new(runtime, new ProfileExecutionAuthorizer(new FakeProfileTrustStore()));

  private static LoadedProfile Loaded(Wdem.Domain.Profiles.EnvironmentProfile profile) =>
      new(profile, ProfileOrigin.Local, "test-profile.json", "TEST");

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
