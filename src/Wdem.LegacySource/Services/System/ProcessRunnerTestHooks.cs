namespace Wdem.LegacySource.Services.System;

internal sealed class ProcessRunnerTestHooks
{
  public TimeSpan ProcessTimeout { get; init; } =
      Wdem.Core.Processes.ProcessExecutionRequest.DefaultTimeout;

  public TimeSpan OutputDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

  public Func<Func<CancellationToken, Task>, CancellationToken, Task>? WaitForExitAsync
  {
    get;
    init;
  }

  public Func<int?, CancellationToken, Task>? AfterExitAsync { get; init; }

  public Func<Task[], CancellationToken, Task>? DrainOutputAsync { get; init; }
}
