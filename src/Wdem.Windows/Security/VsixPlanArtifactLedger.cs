using System.Globalization;
using System.Security;
using System.Text;

namespace Wdem.Windows.Security;

internal enum VsixPlanArtifactLedgerStatus
{
  Pending,
  Active,
  ClaimStarted,
  Consumed,
  Revoked
}

internal readonly record struct VsixPlanArtifactLedgerState(
    DateTimeOffset ExpiresAtUtc,
    VsixPlanArtifactLedgerStatus Status)
{
  internal bool IsTerminal => Status is VsixPlanArtifactLedgerStatus.ClaimStarted or
      VsixPlanArtifactLedgerStatus.Consumed or VsixPlanArtifactLedgerStatus.Revoked;
}

internal static class VsixPlanArtifactLedger
{
  private const int ExpiryDigits = 19;
  private const int ReadBufferBytes = 4096;
  private const string IssuedPrefix = "wdem-vsix-issued-v1:";
  private const string ActivatedPrefix = "wdem-vsix-activated-v1:";
  private const string ClaimStartedPrefix = "wdem-vsix-claim-started-v1:";
  private const string ConsumedPrefix = "wdem-vsix-consumed-v1:";
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

  internal static byte[] CreateActivatedRecord(string ownershipToken, string directoryName) =>
      CreateFixedRecord(ActivatedPrefix, ownershipToken, directoryName);

  internal static byte[] CreateClaimStartedRecord(string ownershipToken, string directoryName) =>
      CreateFixedRecord(ClaimStartedPrefix, ownershipToken, directoryName);

  internal static byte[] CreateConsumedRecord(string ownershipToken, string directoryName) =>
      CreateFixedRecord(ConsumedPrefix, ownershipToken, directoryName);

  internal static byte[] CreateRevokedRecord(string ownershipToken, string directoryName) =>
      CreateFixedRecord(RevokedPrefix, ownershipToken, directoryName);

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
      string directoryName) =>
      ReadState(ledger, ownershipToken, directoryName).ExpiresAtUtc;

  internal static VsixPlanArtifactLedgerState ReadState(
      Stream ledger,
      string ownershipToken,
      string directoryName)
  {
    ArgumentNullException.ThrowIfNull(ledger);
    var identity = CreateIdentity(ownershipToken, directoryName);
    var issuedPrefix = Encoding.ASCII.GetBytes($"{IssuedPrefix}{identity}:");
    var activated = Encoding.ASCII.GetBytes($"{ActivatedPrefix}{identity}");
    var claimStarted = Encoding.ASCII.GetBytes($"{ClaimStartedPrefix}{identity}");
    var consumed = Encoding.ASCII.GetBytes($"{ConsumedPrefix}{identity}");
    var revoked = Encoding.ASCII.GetBytes($"{RevokedPrefix}{identity}");
    var maximumRecordLength = new[]
    {
      issuedPrefix.Length + ExpiryDigits + 1,
      activated.Length + 1,
      claimStarted.Length + 1,
      consumed.Length + 1,
      revoked.Length + 1
    }.Max();
    var buffer = new byte[ReadBufferBytes + maximumRecordLength - 1];
    DateTimeOffset? expiry = null;
    var activatedSeen = false;
    var claimStartedSeen = false;
    var consumedSeen = false;
    var revokedSeen = false;
    var invalid = false;
    var carry = 0;
    while (true)
    {
      var bytesRead = ledger.Read(buffer, carry, ReadBufferBytes);
      var available = carry + bytesRead;
      for (var offset = 0; offset < available; offset++)
      {
        var remaining = buffer.AsSpan(offset, available - offset);
        if (remaining.StartsWith(issuedPrefix))
        {
          var recordLength = issuedPrefix.Length + ExpiryDigits + 1;
          if (remaining.Length >= recordLength)
          {
            var ticks = ParseTicks(remaining.Slice(issuedPrefix.Length, ExpiryDigits));
            if (ticks is not null && remaining[recordLength - 1] == (byte)'\n')
            {
              var candidate = new DateTimeOffset(ticks.Value, TimeSpan.Zero);
              invalid |= activatedSeen && expiry is null;
              invalid |= expiry is not null && expiry != candidate;
              expiry ??= candidate;
            }
          }
        }

        activatedSeen |= IsCompleteFixedRecord(remaining, activated);
        claimStartedSeen |= IsCompleteFixedRecord(remaining, claimStarted);
        consumedSeen |= IsCompleteFixedRecord(remaining, consumed);
        revokedSeen |= IsCompleteFixedRecord(remaining, revoked);
      }

      if (bytesRead == 0)
      {
        break;
      }

      carry = Math.Min(maximumRecordLength - 1, available);
      buffer.AsSpan(available - carry, carry).CopyTo(buffer);
    }

    if (expiry is null || invalid)
    {
      throw new SecurityException("The VSIX issuance state is missing, conflicting, or invalid.");
    }

    var status = revokedSeen
        ? VsixPlanArtifactLedgerStatus.Revoked
        : consumedSeen
            ? VsixPlanArtifactLedgerStatus.Consumed
            : claimStartedSeen
                ? VsixPlanArtifactLedgerStatus.ClaimStarted
                : activatedSeen
                    ? VsixPlanArtifactLedgerStatus.Active
                    : VsixPlanArtifactLedgerStatus.Pending;
    return new VsixPlanArtifactLedgerState(expiry.Value, status);
  }

  private static bool IsCompleteFixedRecord(
      ReadOnlySpan<byte> remaining,
      ReadOnlySpan<byte> prefix)
  {
    if (!remaining.StartsWith(prefix))
    {
      return false;
    }

    if (remaining.Length <= prefix.Length)
    {
      return false;
    }

    if (remaining[prefix.Length] != (byte)'\n')
    {
      return false;
    }

    return true;
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

  private static byte[] CreateFixedRecord(
      string prefix,
      string ownershipToken,
      string directoryName) =>
      Encoding.ASCII.GetBytes($"{prefix}{CreateIdentity(ownershipToken, directoryName)}\n");

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
