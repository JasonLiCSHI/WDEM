using System.Security;
using System.Security.Cryptography;
using Wdem.Core.Execution;

namespace Wdem.Windows.Configuration;

public sealed record ResolvedConfigurationSource(
    string Path,
    string Sha256,
    ReadOnlyMemory<byte> Contents);

public sealed record ConfigurationSourceResolution(
    ResolvedConfigurationSource? Source,
    StructuredError? Error)
{
  public bool IsValid => Source is not null && Error is null;
}

public sealed class ConfigurationSourceResolver
{
  private const long MaxConfigurationBytes = 64L * 1024 * 1024;
  private readonly string _applicationRoot;
  private readonly string _profileRoot;
  private readonly string _assetsRoot;

  public ConfigurationSourceResolver(string applicationRoot, string profileRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
    ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
    _applicationRoot = Path.GetFullPath(applicationRoot);
    _profileRoot = Path.GetFullPath(profileRoot);
    _assetsRoot = Path.Combine(_applicationRoot, "profiles", "assets");
  }

  public async Task<ConfigurationSourceResolution> ResolveAsync(
      string source,
      string expectedSha256,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!TryDecodeSha256(expectedSha256, out var expectedBytes))
    {
      return Failure("The expected SHA-256 must contain exactly 64 hexadecimal characters.");
    }

    if (string.IsNullOrWhiteSpace(source) || source.Any(char.IsControl))
    {
      return Failure("The configuration source path is required and must not contain control characters.");
    }

    try
    {
      var path = ResolvePath(source, out var requiredRoot);
      if (HasAlternateDataStream(path))
      {
        return Failure("NTFS alternate data stream configuration sources are not supported.");
      }

      if (requiredRoot is not null && !IsWithin(path, requiredRoot))
      {
        return Failure("The configuration source escapes its permitted root.");
      }

      if (!File.Exists(path))
      {
        return Failure("The configuration source does not exist.");
      }

      var attributes = File.GetAttributes(path);
      if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
      {
        return Failure("The configuration source must be a regular non-reparse file.");
      }

      var pathRoot = Path.GetPathRoot(path);
      if (string.IsNullOrEmpty(pathRoot) || ContainsReparsePoint(pathRoot, path))
      {
        return Failure("The configuration source path contains an unsafe reparse point.");
      }

      byte[] contents;
      await using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: 81920,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
      {
        if (stream.Length > MaxConfigurationBytes)
        {
          return Failure("The configuration source exceeds the 64 MiB size limit.");
        }

        contents = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(contents, cancellationToken).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
        {
          return Failure("The configuration source changed while it was being read.");
        }
      }

      var actualBytes = SHA256.HashData(contents);
      if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
      {
        return Failure("The configuration source does not match the expected SHA-256 hash.");
      }

      return new ConfigurationSourceResolution(
          new ResolvedConfigurationSource(path, Convert.ToHexString(actualBytes), contents),
          null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        NotSupportedException or UnauthorizedAccessException or SecurityException)
    {
      return Failure("The configuration source could not be safely resolved and read.", exception);
    }
  }

  private string ResolvePath(string source, out string? requiredRoot)
  {
    if (Path.IsPathFullyQualified(source))
    {
      requiredRoot = null;
      return Path.GetFullPath(source);
    }

    if (Uri.TryCreate(source, UriKind.Absolute, out _) || LooksLikeUri(source))
    {
      throw new NotSupportedException("URI-style configuration sources are not supported.");
    }

    var normalized = source.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    var assetsPrefix = $"profiles{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}";
    if (normalized.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
    {
      requiredRoot = _assetsRoot;
      return Path.GetFullPath(Path.Combine(_applicationRoot, normalized));
    }

    requiredRoot = _profileRoot;
    return Path.GetFullPath(Path.Combine(_profileRoot, normalized));
  }

  private static bool LooksLikeUri(string value)
  {
    var colon = value.IndexOf(':');
    if (colon <= 0)
    {
      return false;
    }

    return value[..colon].All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '.');
  }

  private static bool ContainsReparsePoint(string root, string path)
  {
    var relative = Path.GetRelativePath(root, path);
    var current = root;
    foreach (var segment in relative.Split(
                 [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                 StringSplitOptions.RemoveEmptyEntries))
    {
      current = Path.Combine(current, segment);
      if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
      {
        return true;
      }
    }

    return false;
  }

  internal static bool IsWithin(string path, string root)
  {
    var relative = Path.GetRelativePath(root, path);
    return !Path.IsPathRooted(relative) &&
        !relative.Equals("..", StringComparison.Ordinal) &&
        !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
        !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
  }

  internal static bool IsSha256(string? value) =>
      value is { Length: 64 } && value.All(Uri.IsHexDigit);

  internal static bool HasAlternateDataStream(string path)
  {
    var root = Path.GetPathRoot(path) ?? string.Empty;
    return path.AsSpan(root.Length).Contains(':');
  }

  private static bool TryDecodeSha256(string? value, out byte[] bytes)
  {
    bytes = [];
    if (!IsSha256(value))
    {
      return false;
    }

    bytes = Convert.FromHexString(value!);
    return true;
  }

  private static ConfigurationSourceResolution Failure(
      string detail,
      Exception? exception = null) => new(
          null,
          new StructuredError(
              WdemErrorCode.ConfigurationError,
              "Configuration source is invalid.",
              detail)
          {
            UnderlyingException = exception
          });
}
