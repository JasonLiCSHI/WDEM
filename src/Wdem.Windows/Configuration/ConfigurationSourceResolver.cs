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
  private readonly Action<string>? _afterSourceDirectoryLeased;

  public ConfigurationSourceResolver(string applicationRoot, string profileRoot)
      : this(applicationRoot, profileRoot, afterSourceDirectoryLeased: null)
  {
  }

  internal ConfigurationSourceResolver(
      string applicationRoot,
      string profileRoot,
      Action<string>? afterSourceDirectoryLeased)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
    ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
    _applicationRoot = Path.GetFullPath(applicationRoot);
    _profileRoot = Path.GetFullPath(profileRoot);
    _assetsRoot = Path.Combine(_applicationRoot, "profiles", "assets");
    _afterSourceDirectoryLeased = afterSourceDirectoryLeased;
  }

  public Task<ConfigurationSourceResolution> ResolveAsync(
      string source,
      string expectedSha256,
      CancellationToken cancellationToken) =>
      ResolveAsync(source, expectedSha256, null, cancellationToken);

  public async Task<ConfigurationSourceResolution> ResolveAsync(
      string source,
      string expectedSha256,
      string? profileSourcePath,
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
      var path = ResolvePath(source, profileSourcePath, out var requiredRoot);
      if (HasAlternateDataStream(path))
      {
        return Failure("NTFS alternate data stream configuration sources are not supported.");
      }

      if (requiredRoot is not null && !IsWithin(path, requiredRoot))
      {
        return Failure("The configuration source escapes its permitted root.");
      }

      byte[] contents;
      var directory = Path.GetDirectoryName(path) ?? throw new IOException(
          "The configuration source directory is invalid.");
      using (var directoryLease = ConfigurationDirectoryLease.AcquireExisting(directory))
      {
        _afterSourceDirectoryLeased?.Invoke(directory);
        await using var stream = new FileStream(
            directoryLease.OpenReadOnlyFile(path),
            FileAccess.Read,
            bufferSize: 81920,
            isAsync: false);
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
    catch (NotSupportedException exception)
    {
      return Failure(exception.Message, exception);
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        UnauthorizedAccessException or SecurityException)
    {
      return Failure("The configuration source could not be safely resolved and read.", exception);
    }
  }

  private string ResolvePath(
      string source,
      string? profileSourcePath,
      out string? requiredRoot)
  {
    if (Path.IsPathFullyQualified(source))
    {
      requiredRoot = null;
      return Path.GetFullPath(source);
    }

    if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
    {
      if (!uri.IsFile)
      {
        throw new NotSupportedException("Only file URI configuration sources are supported.");
      }

      requiredRoot = null;
      return Path.GetFullPath(uri.LocalPath);
    }

    if (LooksLikeUri(source))
    {
      throw new NotSupportedException("Only file URI configuration sources are supported.");
    }

    var normalized = source.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    var assetsPrefix = $"profiles{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}";
    if (normalized.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
    {
      requiredRoot = _assetsRoot;
      return Path.GetFullPath(Path.Combine(_applicationRoot, normalized));
    }

    requiredRoot = profileSourcePath is null
        ? _profileRoot
        : Path.GetDirectoryName(Path.GetFullPath(profileSourcePath)) ?? throw new IOException(
            "The profile source directory is invalid.");
    return Path.GetFullPath(Path.Combine(requiredRoot, normalized));
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
    if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
        uri.IsFile)
    {
      return HasAlternateDataStream(uri.LocalPath);
    }

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
