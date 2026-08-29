using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
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

internal static class WindowsVsixPlanArtifactClock
{
  private const int SystemBootEnvironmentInformation = 90;

  internal static Guid GetBootIdentifier()
  {
    if (!OperatingSystem.IsWindows())
    {
      throw new PlatformNotSupportedException("VSIX boot identity requires Windows.");
    }

    var status = NativeMethods.NtQuerySystemInformation(
        SystemBootEnvironmentInformation,
        out var information,
        (uint)Marshal.SizeOf<SystemBootEnvironment>(),
        out _);
    if (status < 0 || information.BootIdentifier == Guid.Empty)
    {
      throw new SecurityException(
          $"The Windows boot identity is unavailable (NTSTATUS 0x{status:X8}).");
    }

    return information.BootIdentifier;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct SystemBootEnvironment
  {
    public Guid BootIdentifier;
    public int FirmwareType;
    public ulong BootFlags;
  }

  private static class NativeMethods
  {
    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(
        int systemInformationClass,
        out SystemBootEnvironment systemInformation,
        uint systemInformationLength,
        out uint returnLength);
  }
}

internal readonly record struct VsixPlanArtifactLedgerState(
    DateTimeOffset ExpiresAtUtc,
    string ActivationCommitment,
    Guid BootIdentifier,
    long ExpiresAtUptimeMilliseconds,
    VsixPlanArtifactLedgerStatus Status,
    string? ClaimNonce = null)
{
  internal bool IsTerminal => Status is VsixPlanArtifactLedgerStatus.ClaimStarted or
      VsixPlanArtifactLedgerStatus.Consumed or VsixPlanArtifactLedgerStatus.Revoked;

  internal bool IsExpired(
      DateTimeOffset utcNow,
      Guid bootIdentifier,
      long uptimeMilliseconds) =>
      uptimeMilliseconds < 0 ||
      ExpiresAtUtc <= utcNow ||
      BootIdentifier != bootIdentifier ||
      ExpiresAtUptimeMilliseconds <= uptimeMilliseconds;
}

internal static class VsixPlanArtifactLedger
{
  private const int ExpiryDigits = 19;
  private const int CommitmentDigits = 64;
  private const int ClaimNonceDigits = 64;
  private const int BootIdentifierDigits = 32;
  private const int UptimeDigits = 19;
  private const int ReadBufferBytes = 4096;
  private const string IssuedPrefix = "wdem-vsix-issued-v1:";
  private const string ActivatedPrefix = "wdem-vsix-activated-v1:";
  private const string ClaimStartedPrefix = "wdem-vsix-claim-started-v1:";
  private const string ConsumedPrefix = "wdem-vsix-consumed-v1:";
  private const string RevokedPrefix = "wdem-vsix-revoked-v1:";

  internal static byte[] CreateIssuedRecord(
      string ownershipToken,
      string directoryName,
      DateTimeOffset expiresAtUtc,
      string activationCommitment,
      Guid bootIdentifier,
      long expiresAtUptimeMilliseconds)
  {
    var identity = CreateIdentity(ownershipToken, directoryName);
    if (expiresAtUtc == default)
    {
      throw new SecurityException("The VSIX issuance expiry is invalid.");
    }

    if (activationCommitment.Length != CommitmentDigits ||
        !activationCommitment.All(Uri.IsHexDigit))
    {
      throw new ArgumentException(
          "The activation commitment must contain exactly 64 hexadecimal characters.",
          nameof(activationCommitment));
    }

    if (bootIdentifier == Guid.Empty || expiresAtUptimeMilliseconds <= 0)
    {
      throw new SecurityException("The VSIX issuance monotonic deadline is invalid.");
    }

    return Encoding.ASCII.GetBytes(
        $"{IssuedPrefix}{identity}:{expiresAtUtc.UtcTicks.ToString("D19", CultureInfo.InvariantCulture)}:" +
        $"{activationCommitment.ToUpperInvariant()}:{bootIdentifier:N}:" +
        $"{expiresAtUptimeMilliseconds.ToString("D19", CultureInfo.InvariantCulture)}\n");
  }

  internal static byte[] CreateActivatedRecord(string ownershipToken, string directoryName) =>
      CreateFixedRecord(ActivatedPrefix, ownershipToken, directoryName);

  internal static byte[] CreateClaimStartedRecord(
      string ownershipToken,
      string directoryName,
      string claimNonce)
  {
    if (claimNonce.Length != ClaimNonceDigits || !claimNonce.All(Uri.IsHexDigit))
    {
      throw new ArgumentException(
          "The claim nonce must contain exactly 64 hexadecimal characters.",
          nameof(claimNonce));
    }

    return Encoding.ASCII.GetBytes(
        $"{ClaimStartedPrefix}{CreateIdentity(ownershipToken, directoryName)}:" +
        $"{claimNonce.ToUpperInvariant()}\n");
  }

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

  internal static bool IsAuthorizedClaimForConsumption(
      VsixPlanArtifactLedgerState state,
      string claimNonce,
      string activationCommitment,
      DateTimeOffset utcNow,
      Guid bootIdentifier,
      long uptimeMilliseconds) =>
      state.Status == VsixPlanArtifactLedgerStatus.ClaimStarted &&
      FixedTimeEqualsHex(state.ClaimNonce, claimNonce) &&
      FixedTimeEqualsHex(state.ActivationCommitment, activationCommitment) &&
      !state.IsExpired(utcNow, bootIdentifier, uptimeMilliseconds);

  internal static VsixPlanArtifactLedgerStatus? ReadFirstTerminalStatus(
      Stream ledger,
      string ownershipToken,
      string directoryName)
  {
    ArgumentNullException.ThrowIfNull(ledger);
    var identity = CreateIdentity(ownershipToken, directoryName);
    var consumed = Encoding.ASCII.GetBytes($"{ConsumedPrefix}{identity}");
    var revoked = Encoding.ASCII.GetBytes($"{RevokedPrefix}{identity}");
    var maximumRecordLength = Math.Max(consumed.Length, revoked.Length) + 1;
    var buffer = new byte[ReadBufferBytes + maximumRecordLength - 1];
    var carry = 0;
    while (true)
    {
      var bytesRead = ledger.Read(buffer, carry, ReadBufferBytes);
      var available = carry + bytesRead;
      for (var offset = 0; offset < available; offset++)
      {
        var remaining = buffer.AsSpan(offset, available - offset);
        if (IsCompleteFixedRecord(remaining, consumed))
        {
          return VsixPlanArtifactLedgerStatus.Consumed;
        }
        if (IsCompleteFixedRecord(remaining, revoked))
        {
          return VsixPlanArtifactLedgerStatus.Revoked;
        }
      }

      if (bytesRead == 0)
      {
        return null;
      }

      carry = Math.Min(maximumRecordLength - 1, available);
      buffer.AsSpan(available - carry, carry).CopyTo(buffer);
    }
  }

  internal static VsixPlanArtifactLedgerState ReadState(
      Stream ledger,
      string ownershipToken,
      string directoryName)
  {
    ArgumentNullException.ThrowIfNull(ledger);
    var identity = CreateIdentity(ownershipToken, directoryName);
    var issuedPrefix = Encoding.ASCII.GetBytes($"{IssuedPrefix}{identity}:");
    var activated = Encoding.ASCII.GetBytes($"{ActivatedPrefix}{identity}");
    var legacyClaimStarted = Encoding.ASCII.GetBytes($"{ClaimStartedPrefix}{identity}");
    var claimStartedPrefix = Encoding.ASCII.GetBytes($"{ClaimStartedPrefix}{identity}:");
    var consumed = Encoding.ASCII.GetBytes($"{ConsumedPrefix}{identity}");
    var revoked = Encoding.ASCII.GetBytes($"{RevokedPrefix}{identity}");
    var maximumRecordLength = new[]
    {
      issuedPrefix.Length + ExpiryDigits + 1 + CommitmentDigits + 1 +
          BootIdentifierDigits + 1 + UptimeDigits + 1,
      activated.Length + 1,
      claimStartedPrefix.Length + ClaimNonceDigits + 1,
      consumed.Length + 1,
      revoked.Length + 1
    }.Max();
    var buffer = new byte[ReadBufferBytes + maximumRecordLength - 1];
    DateTimeOffset? expiry = null;
    string? activationCommitment = null;
    Guid? bootIdentifier = null;
    long? expiresAtUptimeMilliseconds = null;
    var activatedSeen = false;
    var legacyClaimStartedSeen = false;
    string? claimNonce = null;
    VsixPlanArtifactLedgerStatus? terminalStatus = null;
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
          var recordLength = issuedPrefix.Length + ExpiryDigits + 1 + CommitmentDigits + 1 +
              BootIdentifierDigits + 1 + UptimeDigits + 1;
          if (remaining.Length >= recordLength)
          {
            var ticks = ParseTicks(remaining.Slice(issuedPrefix.Length, ExpiryDigits));
            var separatorOffset = issuedPrefix.Length + ExpiryDigits;
            var commitmentOffset = separatorOffset + 1;
            var commitmentBytes = remaining.Slice(commitmentOffset, CommitmentDigits);
            var bootSeparatorOffset = commitmentOffset + CommitmentDigits;
            var bootOffset = bootSeparatorOffset + 1;
            var bootBytes = remaining.Slice(bootOffset, BootIdentifierDigits);
            var uptimeSeparatorOffset = bootOffset + BootIdentifierDigits;
            var uptimeOffset = uptimeSeparatorOffset + 1;
            var uptimeBytes = remaining.Slice(uptimeOffset, UptimeDigits);
            if (ticks is not null &&
                remaining[separatorOffset] == (byte)':' &&
                IsHex(commitmentBytes) &&
                remaining[bootSeparatorOffset] == (byte)':' &&
                IsHex(bootBytes) &&
                remaining[uptimeSeparatorOffset] == (byte)':' &&
                ParsePositiveInt64(uptimeBytes) is { } candidateUptime &&
                remaining[recordLength - 1] == (byte)'\n')
            {
              var candidate = new DateTimeOffset(ticks.Value, TimeSpan.Zero);
              var candidateCommitment = Encoding.ASCII.GetString(commitmentBytes).ToUpperInvariant();
              var candidateBoot = Guid.ParseExact(Encoding.ASCII.GetString(bootBytes), "N");
              invalid |= activatedSeen && expiry is null;
              invalid |= expiry is not null && expiry != candidate;
              invalid |= activationCommitment is not null &&
                  !string.Equals(
                      activationCommitment,
                      candidateCommitment,
                      StringComparison.Ordinal);
              invalid |= bootIdentifier is not null && bootIdentifier != candidateBoot;
              invalid |= expiresAtUptimeMilliseconds is not null &&
                  expiresAtUptimeMilliseconds != candidateUptime;
              expiry ??= candidate;
              activationCommitment ??= candidateCommitment;
              bootIdentifier ??= candidateBoot;
              expiresAtUptimeMilliseconds ??= candidateUptime;
            }
          }
        }

        activatedSeen |= IsCompleteFixedRecord(remaining, activated);
        legacyClaimStartedSeen |= IsCompleteFixedRecord(remaining, legacyClaimStarted);
        if (claimNonce is null && remaining.StartsWith(claimStartedPrefix))
        {
          var recordLength = claimStartedPrefix.Length + ClaimNonceDigits + 1;
          if (remaining.Length >= recordLength)
          {
            var nonceBytes = remaining.Slice(claimStartedPrefix.Length, ClaimNonceDigits);
            if (IsHex(nonceBytes) && remaining[recordLength - 1] == (byte)'\n')
            {
              claimNonce = Encoding.ASCII.GetString(nonceBytes).ToUpperInvariant();
            }
          }
        }
        if (terminalStatus is null && IsCompleteFixedRecord(remaining, consumed))
        {
          terminalStatus = VsixPlanArtifactLedgerStatus.Consumed;
        }
        if (terminalStatus is null && IsCompleteFixedRecord(remaining, revoked))
        {
          terminalStatus = VsixPlanArtifactLedgerStatus.Revoked;
        }
      }

      if (bytesRead == 0)
      {
        break;
      }

      carry = Math.Min(maximumRecordLength - 1, available);
      buffer.AsSpan(available - carry, carry).CopyTo(buffer);
    }

    if (expiry is null || activationCommitment is null || bootIdentifier is null ||
        expiresAtUptimeMilliseconds is null || invalid)
    {
      throw new SecurityException("The VSIX issuance state is missing, conflicting, or invalid.");
    }

    var status = terminalStatus ??
        (legacyClaimStartedSeen || claimNonce is not null
            ? VsixPlanArtifactLedgerStatus.ClaimStarted
            : activatedSeen
                ? VsixPlanArtifactLedgerStatus.Active
                : VsixPlanArtifactLedgerStatus.Pending);
    return new VsixPlanArtifactLedgerState(
        expiry.Value,
        activationCommitment,
        bootIdentifier.Value,
        expiresAtUptimeMilliseconds.Value,
        status,
        claimNonce);
  }

  private static bool FixedTimeEqualsHex(string? left, string right)
  {
    if (left is null || left.Length != 64 || right.Length != 64)
    {
      return false;
    }

    try
    {
      return CryptographicOperations.FixedTimeEquals(
          Convert.FromHexString(left),
          Convert.FromHexString(right));
    }
    catch (FormatException)
    {
      return false;
    }
  }

  private static long? ParsePositiveInt64(ReadOnlySpan<byte> value)
  {
    long result = 0;
    foreach (var current in value)
    {
      if (current < (byte)'0' || current > (byte)'9')
      {
        return null;
      }

      var digit = current - (byte)'0';
      if (result > (long.MaxValue - digit) / 10)
      {
        return null;
      }

      result = (result * 10) + digit;
    }

    return result > 0 ? result : null;
  }

  private static bool IsHex(ReadOnlySpan<byte> value)
  {
    foreach (var current in value)
    {
      if (!((current >= (byte)'0' && current <= (byte)'9') ||
            (current >= (byte)'A' && current <= (byte)'F') ||
            (current >= (byte)'a' && current <= (byte)'f')))
      {
        return false;
      }
    }

    return true;
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
