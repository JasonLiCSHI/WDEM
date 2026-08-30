using Wdem.Core.Execution;
using Wdem.Core.Runs;

namespace Wdem.Desktop.ViewModels;

public sealed class LogEntryViewModel
{
  internal LogEntryViewModel(RunEvent runEvent, LogRedactor redactor)
  {
    RunEvent redacted = redactor.Redact(runEvent);
    Sequence = redacted.Sequence;
    Timestamp = redacted.TimestampUtc;
    Kind = redacted.Kind;
    ResourceId = redacted.ResourceId;
    StepId = redacted.StepId;
    Message = redacted.Message;
    Error = redacted.Error;
  }

  public long Sequence { get; }

  public DateTimeOffset Timestamp { get; }

  public string TimestampDisplay => Timestamp.ToLocalTime().ToString("HH:mm:ss");

  public RunEventKind Kind { get; }

  public string? ResourceId { get; }

  public string? StepId { get; }

  public string Message { get; }

  public StructuredError? Error { get; }

  public string? ErrorDetail => Error?.Detail;
}
