using Wdem.Core.Execution;

namespace Wdem.Core.Profiles;

internal static class ProfileErrorFactory
{
  public static StructuredError Create(
      string sourcePath,
      string summary,
      string detail,
      string pointer = "",
      Exception? exception = null)
  {
    var safeSummary = DiagnosticTextSanitizer.Sanitize(summary);
    var safeFileName = DiagnosticTextSanitizer.Sanitize(Path.GetFileName(sourcePath) ?? string.Empty);
    var safeDetail = DiagnosticTextSanitizer.Sanitize(detail);
    var location = string.IsNullOrEmpty(pointer) ? "" : $" at '{pointer}'";
    return new StructuredError(
        WdemErrorCode.ProfileError,
        safeSummary,
        $"Profile file '{safeFileName}'{location}: {safeDetail}")
    {
      UnderlyingException = exception,
      SuggestedAction = "Correct the profile and load it again."
    };
  }

  public static StructuredError FromException(
      string sourcePath,
      string summary,
      string safeContext,
      string pointer,
      Exception exception) =>
      Create(
          sourcePath,
          summary,
          $"{safeContext} {DiagnosticTextSanitizer.Sanitize(exception.Message)}",
          pointer,
          exception);
}
