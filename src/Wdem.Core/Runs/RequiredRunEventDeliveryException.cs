namespace Wdem.Core.Runs;

public sealed class RequiredRunEventDeliveryException(Exception cause) : Exception(
    "A required run event subscriber failed.",
    cause)
{
  public Exception Cause { get; } = cause;
}
