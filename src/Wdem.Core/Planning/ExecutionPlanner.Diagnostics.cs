using System.Text;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Planning;

public sealed partial class ExecutionPlanner
{
  private static bool TryNormalizeValidationDiagnostics(
      ResourceDefinition definition,
      ProviderValidationResult validation,
      out IReadOnlyList<StructuredError> normalized,
      out StructuredError? contractError)
  {
    if (!TryNormalizeDiagnostics(
            definition,
            validation.StructuredErrors,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            out var structuredDiagnostics,
            out contractError))
    {
      normalized = ReadOnly(Array.Empty<StructuredError>());
      return false;
    }

    if (validation.Errors.Count > MaxDiagnosticsPerResource)
    {
      normalized = ReadOnly(Array.Empty<StructuredError>());
      contractError = ProviderError(
          definition.Id,
          "Provider validation returned too many errors.",
          $"The diagnostic limit is {MaxDiagnosticsPerResource} per resource.");
      return false;
    }

    var merged = new List<StructuredError>(structuredDiagnostics.Count + validation.Errors.Count);
    var seenDetails = new HashSet<string>(StringComparer.Ordinal);
    foreach (var diagnostic in structuredDiagnostics)
    {
      if (seenDetails.Add(diagnostic.Detail))
      {
        merged.Add(diagnostic);
      }
    }

    foreach (var error in validation.Errors)
    {
      if (string.IsNullOrWhiteSpace(error))
      {
        normalized = ReadOnly(Array.Empty<StructuredError>());
        contractError = ProviderError(
            definition.Id,
            "Provider validation returned a malformed error.",
            "Legacy validation errors must contain non-empty diagnostic text.");
        return false;
      }

      var sanitized = SanitizeVisible(error);
      if (!seenDetails.Add(sanitized))
      {
        continue;
      }

      if (merged.Count >= MaxDiagnosticsPerResource)
      {
        normalized = ReadOnly(Array.Empty<StructuredError>());
        contractError = ProviderError(
            definition.Id,
            "Provider validation returned too many diagnostics.",
            $"The combined diagnostic limit is {MaxDiagnosticsPerResource} per resource.");
        return false;
      }

      merged.Add(ProviderError(
          definition.Id,
          "Provider validation rejected the resource.",
          sanitized));
    }

    normalized = ReadOnly(merged);
    contractError = null;
    return true;
  }

  private static bool TryNormalizeDiagnostics(
      ResourceDefinition definition,
      IReadOnlyList<StructuredError> diagnostics,
      IReadOnlyDictionary<string, string> stepIds,
      out IReadOnlyList<StructuredError> normalized,
      out StructuredError? contractError)
  {
    normalized = ReadOnly(Array.Empty<StructuredError>());
    contractError = null;
    if (diagnostics.Count > MaxDiagnosticsPerResource)
    {
      contractError = ProviderError(
          definition.Id,
          "Provider returned too many diagnostics.",
          $"The diagnostic limit is {MaxDiagnosticsPerResource} per resource.");
      return false;
    }

    var snapshots = new List<StructuredError>(diagnostics.Count);
    foreach (var diagnostic in diagnostics)
    {
      if (diagnostic is null || !Enum.IsDefined(diagnostic.Code))
      {
        contractError = ProviderError(
            definition.Id,
            "Provider returned a malformed diagnostic.",
            "A provider diagnostic is null or contains an undefined error code.");
        return false;
      }

      if (diagnostic.ResourceId is not null &&
          !IdComparer.Equals(diagnostic.ResourceId, definition.Id))
      {
        contractError = ProviderError(
            definition.Id,
            "Provider diagnostic identity does not match the resource.",
            "A provider diagnostic references a different resource identity.");
        return false;
      }

      string? canonicalStepId = null;
      if (diagnostic.StepId is not null &&
          (!IsValidStepId(diagnostic.StepId) ||
           !stepIds.TryGetValue(diagnostic.StepId, out canonicalStepId)))
      {
        contractError = ProviderError(
            definition.Id,
            "Provider diagnostic identity does not match a plan step.",
            "A provider diagnostic references a missing or malformed step identity.");
        return false;
      }

      snapshots.Add(StructuredError.CreateSnapshot(
          diagnostic.Code,
          SanitizeVisible(diagnostic.Summary),
          SanitizeVisible(diagnostic.Detail),
          diagnostic.ResourceId is null ? null : definition.Id,
          canonicalStepId,
          diagnostic.ProcessExitCode,
          SanitizeOptional(diagnostic.LogLocation),
          SanitizeOptional(diagnostic.SuggestedAction),
          diagnostic.IsRetryable,
          SanitizeOptional(diagnostic.UnderlyingExceptionType),
          SanitizeOptional(diagnostic.UnderlyingExceptionMessage)));
    }

    normalized = ReadOnly(snapshots);
    return true;
  }

  private static StructuredError ProviderException(
      string resourceId,
      string summary,
      Exception exception) => StructuredError.CreateSnapshot(
          WdemErrorCode.ProviderError,
          SanitizeVisible(summary),
          SanitizeVisible(exception.Message),
          resourceId,
          null,
          null,
          null,
          "Review provider diagnostics and create a new plan.",
          false,
          SanitizeVisible(exception.GetType().FullName ?? exception.GetType().Name),
          SanitizeVisible(exception.Message));

  private static StructuredError DetectionStateError(
      string resourceId,
      DetectionOutcome outcome,
      string detail) => new(
          outcome switch
          {
            DetectionOutcome.Cancelled => WdemErrorCode.CancellationError,
            DetectionOutcome.Unsupported => WdemErrorCode.ProviderError,
            _ => WdemErrorCode.DetectionError
          },
          outcome switch
          {
            DetectionOutcome.Cancelled => "Resource detection was cancelled.",
            DetectionOutcome.Unsupported => "Resource detection is unsupported.",
            _ => "Resource detection failed."
          },
          SanitizeVisible(detail))
      {
        ResourceId = resourceId,
        SuggestedAction = "Detect the resource again before creating a new plan."
      };

  private static StructuredError ProviderError(
      string resourceId,
      string summary,
      string detail) => new(
          WdemErrorCode.ProviderError,
          SanitizeVisible(summary),
          SanitizeVisible(detail))
      {
        ResourceId = resourceId,
        SuggestedAction = "Review provider diagnostics and create a new plan."
      };

  private static string? SanitizeOptional(string? value) =>
      value is null ? null : SanitizeVisible(value);

  private static string SanitizeVisible(string value)
  {
    ArgumentNullException.ThrowIfNull(value);

    var characterLimit = Math.Min(value.Length, MaxTextFieldByteCount * 2);
    var normalized = new StringBuilder(characterLimit);
    for (var index = 0; index < characterLimit; index++)
    {
      var character = value[index];
      normalized.Append(char.IsControl(character) ? ' ' : character);
    }

    return TruncateUtf8(
        DiagnosticTextSanitizer.Sanitize(normalized.ToString()),
        MaxTextFieldByteCount);
  }

  private static string TruncateUtf8(string value, int byteLimit)
  {
    if (Utf8ByteCount(value) <= byteLimit)
    {
      return value;
    }

    const string suffix = "...";
    var remaining = byteLimit - Utf8ByteCount(suffix);
    var result = new StringBuilder();
    foreach (var rune in value.EnumerateRunes())
    {
      if (rune.Utf8SequenceLength > remaining)
      {
        break;
      }

      result.Append(rune.ToString());
      remaining -= rune.Utf8SequenceLength;
    }

    return result.Append(suffix).ToString();
  }

  private static int MeasureProviderText(PlannedResource item)
  {
    var total = Utf8ByteCount(item.Reason);
    foreach (var step in item.ResourcePlan.Steps)
    {
      total += Utf8ByteCount(step.Description);
      total += Utf8ByteCount(step.Reason);
    }

    foreach (var diagnostic in item.Diagnostics)
    {
      total += Utf8ByteCount(diagnostic.Summary);
      total += Utf8ByteCount(diagnostic.Detail);
      total += Utf8ByteCount(diagnostic.LogLocation);
      total += Utf8ByteCount(diagnostic.SuggestedAction);
      total += Utf8ByteCount(diagnostic.UnderlyingExceptionType);
      total += Utf8ByteCount(diagnostic.UnderlyingExceptionMessage);
    }

    return total;
  }

  private static int Utf8ByteCount(string? value) =>
      value is null ? 0 : Encoding.UTF8.GetByteCount(value);
}
