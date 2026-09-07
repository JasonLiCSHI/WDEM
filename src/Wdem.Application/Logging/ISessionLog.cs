using Wdem.Application.Execution;

namespace Wdem.Application.Logging;

public interface ISessionLog : IProgress<WorkflowProgress>, IDisposable
{
  string? Path { get; }

  string DisplayPath { get; }

  string? LastError { get; }

  void Write(string category, string message, object? data = null);

  void WriteUserAction(
      string operation,
      UserActionOutcome outcome,
      string? profileId = null,
      IEnumerable<string>? taskIds = null);
}
