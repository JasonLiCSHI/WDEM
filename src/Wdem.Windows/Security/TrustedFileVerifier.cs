using System.Security.Cryptography;
using Wdem.Core.Execution;

namespace Wdem.Windows.Security;

public interface ITrustedFileVerifier
{
  Task<TrustedFileVerificationResult> VerifySha256Async(
      string path,
      string expectedHash,
      CancellationToken cancellationToken);
}

public sealed record TrustedFileVerificationResult(
    bool IsTrusted,
    string? VerifiedPath,
    string? Sha256,
    StructuredError? Error);

public sealed class TrustedFileVerifier : ITrustedFileVerifier
{
  public async Task<TrustedFileVerificationResult> VerifySha256Async(
      string path,
      string expectedHash,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
      return Failure("The trusted file does not exist.");
    }

    if (!TryDecodeHash(expectedHash, out var expectedBytes))
    {
      return Failure("The expected SHA-256 must contain exactly 64 hexadecimal characters.");
    }

    var fullPath = Path.GetFullPath(path);
    await using var stream = new FileStream(
        fullPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    var actualBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    var actualHash = Convert.ToHexString(actualBytes);
    if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
    {
      return Failure("The trusted file does not match the expected SHA-256 hash.");
    }

    return new TrustedFileVerificationResult(true, fullPath, actualHash, null);
  }

  private static bool TryDecodeHash(string? value, out byte[] bytes)
  {
    bytes = [];
    if (value is null || value.Length != 64)
    {
      return false;
    }

    try
    {
      bytes = Convert.FromHexString(value);
      return true;
    }
    catch (FormatException)
    {
      return false;
    }
  }

  private static TrustedFileVerificationResult Failure(string detail) => new(
      false,
      null,
      null,
      new StructuredError(
          WdemErrorCode.ConfigurationError,
          "Trusted file verification failed.",
          detail));
}
