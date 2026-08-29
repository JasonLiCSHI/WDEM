namespace Wdem.Windows.Security;

public sealed record ElevatedHostBootstrapOptions(
    string PipeName,
    string JobName,
    Guid RunId,
    string LocalApplicationData)
{
  public static ElevatedHostBootstrapOptions Parse(IReadOnlyList<string> arguments)
  {
    ArgumentNullException.ThrowIfNull(arguments);
    if (arguments.Count != 8 ||
        !string.Equals(arguments[0], "--pipe", StringComparison.Ordinal) ||
        !string.Equals(arguments[2], "--job", StringComparison.Ordinal) ||
        !string.Equals(arguments[4], "--run-id", StringComparison.Ordinal) ||
        !string.Equals(arguments[6], "--local-app-data", StringComparison.Ordinal))
    {
      throw new ArgumentException(
          "The elevated host accepts only the required bootstrap arguments.",
          nameof(arguments));
    }

    if (string.IsNullOrWhiteSpace(arguments[1]))
    {
      throw new ArgumentException("A pipe name is required.", nameof(arguments));
    }

    var expectedJobName = ElevatedHostProcessJob.NameForPipe(arguments[1]);
    if (!string.Equals(arguments[3], expectedJobName, StringComparison.Ordinal))
    {
      throw new ArgumentException(
          "The elevated host job name does not match the pipe bootstrap.",
          nameof(arguments));
    }

    if (!Guid.TryParseExact(arguments[5], "D", out var runId) || runId == Guid.Empty)
    {
      throw new ArgumentException(
          "A valid execution run identifier is required.",
          nameof(arguments));
    }

    if (string.IsNullOrWhiteSpace(arguments[7]))
    {
      throw new ArgumentException(
          "The local application data path is required.",
          nameof(arguments));
    }

    return new ElevatedHostBootstrapOptions(
        arguments[1],
        arguments[3],
        runId,
        Path.GetFullPath(arguments[7]));
  }
}
