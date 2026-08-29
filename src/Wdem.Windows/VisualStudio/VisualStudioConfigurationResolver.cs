using System.Security;
using System.Security.Cryptography;
using Wdem.Core.Execution;

namespace Wdem.Windows.VisualStudio;

internal sealed class VisualStudioConfigurationResolver(
    Func<CancellationToken, Task>? afterSnapshot = null)
{
  public async Task<ResolvedVisualStudioOptions> ResolveAsync(
      VisualStudioResourceOptions options,
      string expectedSha256,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(options);
    if (options.VsConfigPath is null)
    {
      return new ResolvedVisualStudioOptions(options, null, null, null);
    }

    try
    {
      if (!VisualStudioInputValidation.IsSha256(expectedSha256))
      {
        return Failure(options, "The expected SHA-256 is invalid.");
      }

      var fullPath = Path.GetFullPath(options.VsConfigPath);
      byte[] snapshot;
      await using (var stream = new FileStream(
                       fullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: 81920,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
      {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        snapshot = memory.ToArray();
      }

      var actualBytes = SHA256.HashData(snapshot);
      var expectedBytes = Convert.FromHexString(expectedSha256);
      if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
      {
        return Failure(options, "The .vsconfig file does not match its expected SHA-256 hash.");
      }

      if (afterSnapshot is not null)
      {
        await afterSnapshot(cancellationToken).ConfigureAwait(false);
      }

      await using var immutable = new MemoryStream(snapshot, writable: false);
      var parsed = await VisualStudioConfigurationParser.ParseAsync(
          immutable,
          cancellationToken).ConfigureAwait(false);
      if (parsed.Error is not null)
      {
        return new ResolvedVisualStudioOptions(options, null, null, parsed.Error);
      }

      return new ResolvedVisualStudioOptions(
          options with
          {
            Workloads = Merge(options.Workloads, parsed.Configuration!.Workloads),
            Components = Merge(options.Components, parsed.Configuration.Components)
          },
          fullPath,
          Convert.ToHexString(actualBytes),
          null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        UnauthorizedAccessException or SecurityException or NotSupportedException)
    {
      return Failure(options, "The .vsconfig file could not be safely opened and read.");
    }
  }

  private static IReadOnlyList<string> Merge(
      IReadOnlyList<string> first,
      IReadOnlyList<string> second) => first.Concat(second)
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();

  private static ResolvedVisualStudioOptions Failure(
      VisualStudioResourceOptions options,
      string detail) => new(
          options,
          null,
          null,
          new StructuredError(
              WdemErrorCode.ConfigurationError,
              "Visual Studio configuration is invalid.",
              detail));
}

internal sealed record ResolvedVisualStudioOptions(
    VisualStudioResourceOptions Options,
    string? VerifiedPath,
    string? Sha256,
    StructuredError? Error);
