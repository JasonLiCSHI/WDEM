using System.Text.Json.Serialization;

namespace Wdem.Core.Execution;

public sealed record StructuredError
{
  private Exception? _underlyingException;
  private string _summary;
  private string _detail;
  private string? _underlyingExceptionType;
  private string? _underlyingExceptionMessage;

  [JsonConstructor]
  public StructuredError(WdemErrorCode code, string summary, string detail)
  {
    Code = code;
    _summary = DiagnosticTextSanitizer.Sanitize(
        summary ?? throw new ArgumentNullException(nameof(summary)));
    _detail = DiagnosticTextSanitizer.Sanitize(
        detail ?? throw new ArgumentNullException(nameof(detail)));
  }

  public WdemErrorCode Code { get; init; }

  public string Summary
  {
    get => _summary;
    init => _summary = DiagnosticTextSanitizer.Sanitize(
        value ?? throw new ArgumentNullException(nameof(value)));
  }

  public string Detail
  {
    get => _detail;
    init => _detail = DiagnosticTextSanitizer.Sanitize(
        value ?? throw new ArgumentNullException(nameof(value)));
  }

  public string? ResourceId { get; init; }
  public string? StepId { get; init; }
  public int? ProcessExitCode { get; init; }
  public string? LogLocation { get; init; }
  public string? SuggestedAction { get; init; }
  public bool IsRetryable { get; init; }

  [JsonIgnore]
  public Exception? UnderlyingException
  {
    get => _underlyingException;
    init
    {
      _underlyingException = value;
      UnderlyingExceptionType = value is null
          ? null
          : value.GetType().FullName ?? value.GetType().Name;
      UnderlyingExceptionMessage = value?.Message;
    }
  }

  [JsonInclude]
  public string? UnderlyingExceptionType
  {
    get => _underlyingExceptionType;
    private init => _underlyingExceptionType = value;
  }

  [JsonInclude]
  public string? UnderlyingExceptionMessage
  {
    get => _underlyingExceptionMessage;
    private init => _underlyingExceptionMessage = value is null
        ? null
        : DiagnosticTextSanitizer.Sanitize(value);
  }

  internal static StructuredError CreateSnapshot(
      WdemErrorCode code,
      string summary,
      string detail,
      string? resourceId,
      string? stepId,
      int? processExitCode,
      string? logLocation,
      string? suggestedAction,
      bool isRetryable,
      string? underlyingExceptionType,
      string? underlyingExceptionMessage) => new(code, summary, detail)
      {
        ResourceId = resourceId,
        StepId = stepId,
        ProcessExitCode = processExitCode,
        LogLocation = logLocation,
        SuggestedAction = suggestedAction,
        IsRetryable = isRetryable,
        UnderlyingExceptionType = underlyingExceptionType,
        UnderlyingExceptionMessage = underlyingExceptionMessage
      };
}
