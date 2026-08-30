using Wdem.Core.Execution;
using Wdem.Core.Reporting;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Desktop.ViewModels;
using Xunit;

namespace Wdem.Desktop.Tests.ViewModels;

public sealed class CompletionViewModelTests
{
  [Fact]
  public void CompletedRun_GroupsTerminalResourcesAndUsesPartialHeading()
  {
    var run = CreateRun(
        ("satisfied", ExecutionState.Completed, ExecutionOutcome.NotRequired, RestartPolicy.NoRestart),
        ("succeeded", ExecutionState.Completed, ExecutionOutcome.Succeeded, RestartPolicy.NoRestart),
        ("failed", ExecutionState.Completed, ExecutionOutcome.Failed, RestartPolicy.NoRestart),
        ("blocked", ExecutionState.Blocked, ExecutionOutcome.Skipped, RestartPolicy.NoRestart),
        ("cancelled", ExecutionState.Completed, ExecutionOutcome.Cancelled, RestartPolicy.NoRestart),
        ("skipped", ExecutionState.Completed, ExecutionOutcome.Skipped, RestartPolicy.NoRestart),
        ("restart", ExecutionState.Completed, ExecutionOutcome.Succeeded, RestartPolicy.RestartRequired));

    var viewModel = new CompletionViewModel(
        run,
        new RunReportExporter(new LogRedactor()));

    Assert.Equal("Environment Partially Configured", viewModel.Heading);
    Assert.Single(viewModel.Satisfied);
    Assert.Equal(2, viewModel.Succeeded.Count);
    Assert.Single(viewModel.Failed);
    Assert.Single(viewModel.Blocked);
    Assert.Equal(2, viewModel.CancelledOrSkipped.Count);
    Assert.Single(viewModel.RestartRequired);
  }

  [Fact]
  public void RunWithoutFailureBlockOrCancellation_UsesReadyHeading()
  {
    var run = CreateRun(
        ("satisfied", ExecutionState.Completed, ExecutionOutcome.NotRequired, RestartPolicy.NoRestart),
        ("succeeded", ExecutionState.Completed, ExecutionOutcome.Succeeded, RestartPolicy.NoRestart));

    var viewModel = new CompletionViewModel(
        run,
        new RunReportExporter(new LogRedactor()));

    Assert.Equal("C# Developer Environment Ready", viewModel.Heading);
  }

  private static ExecutionRun CreateRun(
      params (string Id, ExecutionState State, ExecutionOutcome Outcome, RestartPolicy Restart)[] resources) =>
      new()
      {
        RunId = Guid.NewGuid(),
        Mode = RunMode.Apply,
        ProfileSourcePath = Path.GetFullPath("profile.yaml"),
        ProfileId = "csharp-developer",
        ProfileVersion = "1.0.0",
        SelectedOptionalResourceIds = new HashSet<string>(),
        StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        EndedAtUtc = DateTimeOffset.UtcNow,
        State = ExecutionState.Completed,
        Outcome = resources.Any(resource => resource.Outcome == ExecutionOutcome.Failed)
            ? ExecutionOutcome.Failed
            : ExecutionOutcome.Succeeded,
        Machine = new MachineInformation("Windows", "X64", "machine", "user"),
        ResourceResults = resources.ToDictionary(
            resource => resource.Id,
            resource => new ResourceResult
            {
              ResourceId = resource.Id,
              State = resource.State,
              Outcome = resource.Outcome,
              RestartRequirement = resource.Restart
            },
            StringComparer.OrdinalIgnoreCase)
      };
}
