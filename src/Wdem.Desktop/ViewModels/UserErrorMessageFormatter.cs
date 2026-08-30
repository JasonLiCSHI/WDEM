using Wdem.Core.Execution;
using Wdem.Core.Runs;

namespace Wdem.Desktop.ViewModels;

public sealed class StructuredErrorException : InvalidOperationException
{
  public StructuredErrorException(StructuredError error)
      : this(CreateDisplay(error))
  {
  }

  private StructuredErrorException(StructuredErrorDisplay display)
      : base(display.UserMessage)
  {
    Error = display.Error;
  }

  public StructuredError Error { get; }

  private static StructuredErrorDisplay CreateDisplay(StructuredError error)
  {
    StructuredError safeError = UserErrorMessageFormatter.Sanitize(error);
    return new StructuredErrorDisplay(safeError, UserErrorMessageFormatter.Format(safeError));
  }

  private sealed record StructuredErrorDisplay(StructuredError Error, string UserMessage);
}

internal sealed class UserMessageException : InvalidOperationException
{
  public UserMessageException(string message)
      : base(UserErrorMessageFormatter.Sanitize(message))
  {
  }
}

internal static class UserErrorMessageFormatter
{
  private const string GenericErrorMessage = "操作未完成。请检查输入后重试。";

  public static string Format(Exception exception)
  {
    ArgumentNullException.ThrowIfNull(exception);
    return exception switch
    {
      StructuredErrorException structuredException => Format(structuredException.Error),
      UserMessageException userMessageException => userMessageException.Message,
      _ => GenericErrorMessage
    };
  }

  internal static string Format(StructuredError error) => string.Join(
      Environment.NewLine,
      new[] { error.Summary, error.Detail, error.SuggestedAction }
          .Where(value => !string.IsNullOrWhiteSpace(value)));

  internal static StructuredError Sanitize(StructuredError error)
  {
    ArgumentNullException.ThrowIfNull(error);
    StructuredError safeError = new LogRedactor().Redact(error);
    return safeError with
    {
      SuggestedAction = safeError.SuggestedAction is null
          ? null
          : Sanitize(safeError.SuggestedAction)
    };
  }

  internal static string Sanitize(string message)
  {
    ArgumentNullException.ThrowIfNull(message);
    string redacted = new LogRedactor().Redact(message);
    return new StructuredError(WdemErrorCode.ProfileError, redacted, redacted).Detail;
  }
}
