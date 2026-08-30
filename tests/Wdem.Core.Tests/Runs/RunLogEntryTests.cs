using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Runs;
using Wdem.Core.Resources;
using Xunit;

namespace Wdem.Core.Tests.Runs;

public sealed class RunLogEntryTests
{
  [Fact]
  public void EventMapping_RoundTripsEveryDurableEventField()
  {
    var runId = Guid.Parse("51c21d61-1ad9-40ce-9e98-a7297a2a9323");
    var error = new StructuredError(
        WdemErrorCode.VerificationError,
        "Verification failed.",
        "The installed version did not match.")
    {
      ResourceId = "git",
      StepId = "verify",
      IsRetryable = true
    };
    var runEvent = new RunEvent(
        runId,
        42,
        DateTimeOffset.Parse("2026-08-29T08:09:10Z"),
        RunEventKind.StepProgress,
        "git",
        "verify",
        0.75,
        "Verifying Git.",
        error,
        ExecutionState.Completed,
        ExecutionOutcome.Failed,
        RestartPolicy.RestartRequired);

    var entry = RunLogEntry.FromEvent(runEvent, ProviderLogLevel.Warning);
    var restored = entry.ToEvent(runId);

    Assert.Equal(ProviderLogLevel.Warning, entry.Level);
    Assert.Equal(RestartPolicy.RestartRequired, entry.RestartRequirement);
    Assert.Equal(runEvent, restored);
  }
}
