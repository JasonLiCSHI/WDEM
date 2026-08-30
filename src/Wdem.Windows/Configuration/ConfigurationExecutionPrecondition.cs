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

  internal static string? FromDetectedState(
      DetectedState state,
      string pathEvidenceKey,
      params string[] additionalEvidenceKeys)
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
      var canonical = new List<string>
      {
        canonicalPath,
        state.Exists ? "exists" : "missing",
        state.Exists ? state.ConfigurationHash?.ToUpperInvariant() ?? "invalid" : "none"
      };
      foreach (var key in additionalEvidenceKeys)
      {
        if (!state.Evidence.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
          return null;
        }

        canonical.Add(key.EndsWith("Path", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(value).ToUpperInvariant()
            : value.Trim().ToUpperInvariant());
      }

      return Convert.ToHexString(SHA256.HashData(
          Encoding.UTF8.GetBytes(string.Join('\0', canonical))));
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
    {
      return null;
    }
  }

  internal static bool Matches(
      ResourcePlan plan,
      DetectedState currentState,
      string pathEvidenceKey,
      params string[] additionalEvidenceKeys) =>
      plan.ExecutionPreconditionFingerprint is { } expected &&
      string.Equals(
          expected,
          FromDetectedState(currentState, pathEvidenceKey, additionalEvidenceKeys),
          StringComparison.OrdinalIgnoreCase);
}

internal sealed record ConfigurationDestinationPrecondition(
    string Path,
    bool Exists,
    string? Sha256)
{
  internal static ConfigurationDestinationPrecondition? FromDetectedState(
      DetectedState state,
      string pathEvidenceKey)
  {
    if (state.Outcome != DetectionOutcome.Succeeded ||
        !state.Evidence.TryGetValue(pathEvidenceKey, out var path) ||
        string.IsNullOrWhiteSpace(path) ||
        (state.Exists && !ConfigurationSourceResolver.IsSha256(state.ConfigurationHash)))
    {
      return null;
    }

    try
    {
      return new ConfigurationDestinationPrecondition(
          System.IO.Path.GetFullPath(path),
          state.Exists,
          state.Exists ? state.ConfigurationHash!.ToUpperInvariant() : null);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
    {
      return null;
    }
  }
}
