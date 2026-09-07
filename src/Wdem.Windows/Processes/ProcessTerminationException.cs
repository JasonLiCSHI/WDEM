namespace Wdem.Windows.Processes;

public sealed class ProcessTerminationException : InvalidOperationException
{
  public ProcessTerminationException(int processId, string message)
      : base(message)
  {
    ProcessId = processId;
  }

  public ProcessTerminationException(int processId, string message, Exception innerException)
      : base(message, innerException)
  {
    ProcessId = processId;
  }

  public int ProcessId { get; }
}
