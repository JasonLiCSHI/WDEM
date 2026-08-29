using System.Security.Cryptography;
using System.Text;
using Wdem.Core.Providers;

namespace Wdem.Windows.Configuration;

internal static class ConfigurationExecutionPrecondition
{
  internal static string FromPathAndHash(string path, string sha256)
  {
    var canonical = string.Join(
        '\0',
        Path.GetFullPath(path).ToUpperInvariant(),
        sha256.ToUpperInvariant());
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
  }

  internal static string? FromDetectedState(DetectedState state, string pathEvidenceKey)
  {
    ArgumentNullException.ThrowIfNull(state);
    if (state.Outcome != DetectionOutcome.Succeeded ||
        !state.Evidence.TryGetValue(pathEvidenceKey, out var path) ||
        string.IsNullOrWhiteSpace(path))
    {
      return null;
    }

    try
    {
      var canonicalPath = Path.GetFullPath(path).ToUpperInvariant();
      var canonical = string.Join(
          '\0',
          canonicalPath,
          state.Exists ? "exists" : "missing",
          state.Exists ? state.ConfigurationHash?.ToUpperInvariant() ?? "invalid" : "none");
      return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
    {
      return null;
    }
  }

  internal static bool Matches(
      ResourcePlan plan,
      DetectedState currentState,
      string pathEvidenceKey) =>
      plan.ExecutionPreconditionFingerprint is { } expected &&
      string.Equals(
          expected,
          FromDetectedState(currentState, pathEvidenceKey),
          StringComparison.OrdinalIgnoreCase);
}
