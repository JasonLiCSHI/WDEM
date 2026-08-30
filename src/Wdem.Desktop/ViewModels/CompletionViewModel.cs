using Wdem.Core.Execution;
using Wdem.Core.Reporting;
using Wdem.Core.Resources;
using Wdem.Core.Runs;

namespace Wdem.Desktop.ViewModels;

public sealed class CompletionViewModel : ObservableObject
{
  private readonly IRunReportExporter _reportExporter;
  private readonly LogRedactor _redactor;
  private string? _errorMessage;

  public CompletionViewModel(
      ExecutionRun run,
      IRunReportExporter reportExporter,
      LogRedactor redactor,
      Func<Task>? returnToPlan = null,
      Func<Task>? returnToProfiles = null,
      Func<Task>? retryFailed = null)
  {
    ArgumentNullException.ThrowIfNull(run);
    _reportExporter = reportExporter ?? throw new ArgumentNullException(nameof(reportExporter));
    _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    Run = new ExecutionRunRedactor(redactor).Redact(run);
    var resources = Run.ResourceResults.Values
        .OrderBy(result => result.ResourceId, StringComparer.OrdinalIgnoreCase)
        .Select(result => new
        {
          Result = result,
          View = new CompletionResourceViewModel(
              redactor.Redact(result.ResourceId),
              result.State.ToString(),
              result.Outcome?.ToString() ?? "Unknown",
              result.FinalCompliance?.ToString() ?? "Unknown",
              result.RestartRequirement == RestartPolicy.RestartRequired,
              result.Error is null ? null : redactor.Redact(result.Error).Summary)
        })
        .ToArray();
    Satisfied = resources.Where(item => item.Result.Outcome == ExecutionOutcome.NotRequired)
        .Select(item => item.View).ToArray();
    Succeeded = resources.Where(item => item.Result.Outcome == ExecutionOutcome.Succeeded)
        .Select(item => item.View).ToArray();
    Failed = resources.Where(item => item.Result.Outcome == ExecutionOutcome.Failed)
        .Select(item => item.View).ToArray();
    Blocked = resources.Where(item => item.Result.State == ExecutionState.Blocked)
        .Select(item => item.View).ToArray();
    CancelledOrSkipped = resources.Where(item =>
            item.Result.State != ExecutionState.Blocked &&
            item.Result.Outcome is ExecutionOutcome.Cancelled or ExecutionOutcome.Skipped)
        .Select(item => item.View).ToArray();
    RestartRequired = resources.Where(item =>
            item.Result.RestartRequirement == RestartPolicy.RestartRequired)
        .Select(item => item.View).ToArray();
    bool isPartial = Run.ResourceResults.Values.Any(result =>
        result.State == ExecutionState.Blocked ||
        result.Outcome is ExecutionOutcome.Failed
            or ExecutionOutcome.Cancelled);
    Heading = isPartial
        ? "Environment Partially Configured"
        : "C# Developer Environment Ready";
    ProfileDisplay = $"{Run.ProfileId} {Run.ProfileVersion}";
    RunId = Run.RunId.ToString("D");
    ReturnToPlanCommand = new AsyncRelayCommand(
        _ => returnToPlan?.Invoke() ?? Task.CompletedTask,
        _ => returnToPlan is not null,
        ReportError);
    ReturnToProfilesCommand = new AsyncRelayCommand(
        _ => returnToProfiles?.Invoke() ?? Task.CompletedTask,
        _ => returnToProfiles is not null,
        ReportError);
    RetryFailedCommand = new AsyncRelayCommand(
        _ => retryFailed?.Invoke() ?? Task.CompletedTask,
        _ => retryFailed is not null && Failed.Count > 0,
        ReportError);
  }

  public ExecutionRun Run { get; }

  public string Heading { get; }

  public string ProfileDisplay { get; }

  public string RunId { get; }

  public IReadOnlyList<CompletionResourceViewModel> Satisfied { get; }

  public IReadOnlyList<CompletionResourceViewModel> Succeeded { get; }

  public IReadOnlyList<CompletionResourceViewModel> Failed { get; }

  public IReadOnlyList<CompletionResourceViewModel> Blocked { get; }

  public IReadOnlyList<CompletionResourceViewModel> CancelledOrSkipped { get; }

  public IReadOnlyList<CompletionResourceViewModel> RestartRequired { get; }

  public string? ErrorMessage
  {
    get => _errorMessage;
    private set => SetProperty(ref _errorMessage, value);
  }

  public AsyncRelayCommand ReturnToPlanCommand { get; }

  public AsyncRelayCommand ReturnToProfilesCommand { get; }

  public AsyncRelayCommand RetryFailedCommand { get; }

  public async Task ExportAsync(string filePath, CancellationToken cancellationToken = default)
  {
    try
    {
      ErrorMessage = null;
      await _reportExporter.ExportAsync(Run, filePath, cancellationToken);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      ReportError(exception);
    }
  }

  internal void ReportError(Exception exception) =>
      ErrorMessage = _redactor.Redact(UserErrorMessageFormatter.Format(exception));
}

public sealed record CompletionResourceViewModel(
    string ResourceId,
    string State,
    string Outcome,
    string Compliance,
    bool RequiresRestart,
    string? ErrorSummary);
