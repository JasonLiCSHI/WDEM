using System.Globalization;
using System.Security;
using System.Text;

namespace Wdem.Windows.Security;

internal static class VsixPlanArtifactLedger
{
  private const int ExpiryDigits = 19;
  private const int ReadBufferBytes = 4096;
  private const string IssuedPrefix = "wdem-vsix-issued-v1:";
  private const string RevokedPrefix = "wdem-vsix-revoked-v1:";

  internal static byte[] CreateIssuedRecord(
      string ownershipToken,
      string directoryName,
      DateTimeOffset expiresAtUtc)
  {
    var identity = CreateIdentity(ownershipToken, directoryName);
    if (expiresAtUtc == default)
    {
      throw new SecurityException("The VSIX issuance expiry is invalid.");
    }

    return Encoding.ASCII.GetBytes(
        $"{IssuedPrefix}{identity}:{expiresAtUtc.UtcTicks.ToString("D19", CultureInfo.InvariantCulture)}\n");
  }

  internal static byte[] CreateRevokedRecord(string ownershipToken, string directoryName) =>
      Encoding.ASCII.GetBytes($"{RevokedPrefix}{CreateIdentity(ownershipToken, directoryName)}\n");

  internal static bool ContainsRevokedRecord(
      ReadOnlySpan<byte> contents,
      string ownershipToken,
      string directoryName) =>
      contents.IndexOf(CreateRevokedRecord(ownershipToken, directoryName)) >= 0;

  internal static bool ContainsRevokedRecord(
      Stream ledger,
      string ownershipToken,
      string directoryName) =>
      ContainsFixedRecord(ledger, CreateRevokedRecord(ownershipToken, directoryName));

  internal static DateTimeOffset GetIssuedExpiry(
      Stream ledger,
      string ownershipToken,
      string directoryName)
  {
    ArgumentNullException.ThrowIfNull(ledger);
    var prefix = Encoding.ASCII.GetBytes(
        $"{IssuedPrefix}{CreateIdentity(ownershipToken, directoryName)}:");
    const int suffixLength = ExpiryDigits + 1;
    var recordLength = prefix.Length + suffixLength;
    var buffer = new byte[ReadBufferBytes + recordLength - 1];
    var carry = 0;
    while (true)
    {
      var bytesRead = ledger.Read(buffer, carry, ReadBufferBytes);
      var available = carry + bytesRead;
      for (var offset = 0; offset + recordLength <= available; offset++)
      {
        if (!buffer.AsSpan(offset, prefix.Length).SequenceEqual(prefix) ||
            buffer[offset + recordLength - 1] != (byte)'\n')
        {
          continue;
        }

        var ticks = ParseTicks(buffer.AsSpan(offset + prefix.Length, ExpiryDigits));
        if (ticks is not null)
        {
          return new DateTimeOffset(ticks.Value, TimeSpan.Zero);
        }
      }

      if (bytesRead == 0)
      {
        break;
      }

      carry = Math.Min(recordLength - 1, available);
      buffer.AsSpan(available - carry, carry).CopyTo(buffer);
    }

    throw new SecurityException("The VSIX issuance record is missing or invalid.");
  }

  private static bool ContainsFixedRecord(Stream ledger, byte[] record)
  {
    ArgumentNullException.ThrowIfNull(ledger);
    var buffer = new byte[ReadBufferBytes + record.Length - 1];
    var carry = 0;
    while (true)
    {
      var bytesRead = ledger.Read(buffer, carry, ReadBufferBytes);
      var available = carry + bytesRead;
      if (buffer.AsSpan(0, available).IndexOf(record) >= 0)
      {
        return true;
      }

      if (bytesRead == 0)
      {
        return false;
      }

      carry = Math.Min(record.Length - 1, available);
      buffer.AsSpan(available - carry, carry).CopyTo(buffer);
    }
  }

  private static long? ParseTicks(ReadOnlySpan<byte> value)
  {
    long ticks = 0;
    foreach (var character in value)
    {
      if (character is < (byte)'0' or > (byte)'9')
      {
        return null;
      }

      var digit = character - (byte)'0';
      if (ticks > (long.MaxValue - digit) / 10)
      {
        return null;
      }

      ticks = (ticks * 10) + digit;
    }

    return ticks <= DateTimeOffset.MaxValue.UtcTicks ? ticks : null;
  }

  private static string CreateIdentity(string ownershipToken, string directoryName)
  {
    if (ownershipToken.Length != 32 || !ownershipToken.All(Uri.IsHexDigit) ||
        !Guid.TryParseExact(directoryName, "N", out _))
    {
      throw new SecurityException("The VSIX ledger identity is invalid.");
    }

    return $"{ownershipToken.ToUpperInvariant()}:{directoryName.ToLowerInvariant()}";
  }
}
