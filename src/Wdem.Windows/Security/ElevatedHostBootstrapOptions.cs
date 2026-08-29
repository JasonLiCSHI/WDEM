namespace Wdem.Windows.Security;

public sealed record ElevatedHostBootstrapOptions(
    string PipeName,
    Guid RunId,
    string LocalApplicationData)
{
  public static ElevatedHostBootstrapOptions Parse(IReadOnlyList<string> arguments)
  {
    ArgumentNullException.ThrowIfNull(arguments);
    if (arguments.Count != 6 ||
        !string.Equals(arguments[0], "--pipe", StringComparison.Ordinal) ||
        !string.Equals(arguments[2], "--run-id", StringComparison.Ordinal) ||
        !string.Equals(arguments[4], "--local-app-data", StringComparison.Ordinal))
    {
      throw new ArgumentException(
          "The elevated host accepts only the required bootstrap arguments.",
          nameof(arguments));
    }

    if (string.IsNullOrWhiteSpace(arguments[1]))
    {
      throw new ArgumentException("A pipe name is required.", nameof(arguments));
    }

    if (!Guid.TryParseExact(arguments[3], "D", out var runId) || runId == Guid.Empty)
    {
      throw new ArgumentException(
          "A valid execution run identifier is required.",
          nameof(arguments));
    }

    if (string.IsNullOrWhiteSpace(arguments[5]))
    {
      throw new ArgumentException(
          "The local application data path is required.",
          nameof(arguments));
    }

    return new ElevatedHostBootstrapOptions(
        arguments[1],
        runId,
        Path.GetFullPath(arguments[5]));
  }
}
