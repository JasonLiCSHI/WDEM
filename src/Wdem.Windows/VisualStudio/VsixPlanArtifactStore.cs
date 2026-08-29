using System.Collections.Concurrent;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wdem.Core.Execution;
using Wdem.Windows.Security;

namespace Wdem.Windows.VisualStudio;

internal sealed record VsixPlanArtifactStageResult(
    string? StepEvidence,
    VsixManifest? Manifest,
    StructuredError? Error);

internal sealed record VsixPlanArtifactClaimResult(
    ClaimedVsixPlanArtifact? Artifact,
    StructuredError? Error);

internal interface IVsixPlanArtifactRevocationStore
{
  void RecordIssued(
      string ownershipToken,
      string directoryName,
      DateTimeOffset expiresAtUtc,
      string activationCommitment,
      Guid bootIdentifier,
      long expiresAtUptimeMilliseconds);

  void Activate(string ownershipToken, string directoryName);

  void ClaimStarted(string ownershipToken, string directoryName);

  void Consume(string ownershipToken, string directoryName);

  VsixPlanArtifactLedgerState GetState(string ownershipToken, string directoryName);

  DateTimeOffset GetIssuedExpiry(string ownershipToken, string directoryName);

  void Revoke(string ownershipToken, string directoryName);

  bool IsRevoked(string ownershipToken, string directoryName);
}

internal sealed class WindowsVsixPlanArtifactRevocationStore(string planArtifactRoot)
    : IVsixPlanArtifactRevocationStore
{
  public void RecordIssued(
      string ownershipToken,
      string directoryName,
      DateTimeOffset expiresAtUtc,
      string activationCommitment,
      Guid bootIdentifier,
      long expiresAtUptimeMilliseconds) =>
      WindowsPlanArtifactDirectoryPolicy.AppendIssuance(
          planArtifactRoot,
          ownershipToken,
          directoryName,
          expiresAtUtc,
          activationCommitment,
          bootIdentifier,
          expiresAtUptimeMilliseconds);

  public DateTimeOffset GetIssuedExpiry(string ownershipToken, string directoryName) =>
      WindowsPlanArtifactDirectoryPolicy.GetIssuedExpiry(
          planArtifactRoot,
          ownershipToken,
          directoryName);

  public void Activate(string ownershipToken, string directoryName) =>
      WindowsPlanArtifactDirectoryPolicy.AppendActivation(
          planArtifactRoot,
          ownershipToken,
          directoryName);

  public void ClaimStarted(string ownershipToken, string directoryName) =>
      WindowsPlanArtifactDirectoryPolicy.AppendClaimStarted(
          planArtifactRoot,
          ownershipToken,
          directoryName);

  public void Consume(string ownershipToken, string directoryName) =>
      WindowsPlanArtifactDirectoryPolicy.AppendConsumed(
          planArtifactRoot,
          ownershipToken,
          directoryName);

  public VsixPlanArtifactLedgerState GetState(string ownershipToken, string directoryName) =>
      WindowsPlanArtifactDirectoryPolicy.GetLedgerState(
          planArtifactRoot,
          ownershipToken,
          directoryName);

  public void Revoke(string ownershipToken, string directoryName) =>
      WindowsPlanArtifactDirectoryPolicy.AppendRevocation(
          planArtifactRoot,
          ownershipToken,
          directoryName);

  public bool IsRevoked(string ownershipToken, string directoryName) =>
      WindowsPlanArtifactDirectoryPolicy.ContainsRevocation(
          planArtifactRoot,
          ownershipToken,
          directoryName);
}

internal sealed record VsixPlanVisualStudioIdentity(
    string InstanceId,
    string ProductId,
    string InstallationVersion)
{
  public static VsixPlanVisualStudioIdentity FromInstance(VisualStudioInstance instance) => new(
      instance.InstanceId,
      instance.ProductId,
      instance.InstallationVersion);
}

internal interface IVsixPlanArtifactStore
{
  Task<VsixPlanArtifactStageResult> StageAsync(
      string resourceId,
      string sourcePath,
      string expectedSha256,
      VsixPlanVisualStudioIdentity visualStudioIdentity,
      CancellationToken cancellationToken);

  Task<VsixPlanArtifactStageResult> StageAsync(
      string resourceId,
      Stream source,
      string expectedSha256,
      VsixPlanVisualStudioIdentity visualStudioIdentity,
      CancellationToken cancellationToken);

  Task<VsixPlanArtifactClaimResult> ClaimAsync(
      string resourceId,
      string stepId,
      string expectedSha256,
      VsixPlanVisualStudioIdentity visualStudioIdentity,
      CancellationToken cancellationToken);

  Task AbandonAsync(
      string resourceId,
      string stepId,
      CancellationToken cancellationToken);
}

internal sealed class ClaimedVsixPlanArtifact : IAsyncDisposable
{
  private readonly string _directoryPath;
  private FileStream? _readLock;
  private ArtifactLease? _lease;
  private IDisposable? _validatedDirectory;
  private Action<string>? _deleteDirectory;

  internal ClaimedVsixPlanArtifact(
      string directoryPath,
      string path,
      VsixManifest manifest,
      FileStream readLock,
      ArtifactLease lease,
      IDisposable validatedDirectory,
      Action<string> deleteDirectory)
  {
    _directoryPath = directoryPath;
    Path = path;
    Manifest = manifest;
    _readLock = readLock;
    _lease = lease;
    _validatedDirectory = validatedDirectory;
    _deleteDirectory = deleteDirectory;
  }

  public string Path { get; }
  public VsixManifest Manifest { get; }

  public ValueTask DisposeAsync()
  {
    Interlocked.Exchange(ref _readLock, null)?.Dispose();
    Interlocked.Exchange(ref _lease, null)?.Dispose();
    Interlocked.Exchange(ref _validatedDirectory, null)?.Dispose();
    Interlocked.Exchange(ref _deleteDirectory, null)?.Invoke(_directoryPath);
    return ValueTask.CompletedTask;
  }
}

internal sealed class VsixPlanArtifactStore : IVsixPlanArtifactStore
{
  private const string EvidencePrefix = "vsix-v2:";
  private const string OwnerFileName = ".wdem-vsix-owner";
  private const int MaxOwnershipMarkerBytes = 16 * 1024;
  private readonly ISecureArtifactStager _stager;
  private readonly ITrustedFileVerifier _verifier;
  private readonly IVsixManifestReader _manifestReader;
  private readonly Action<string, string> _validateRestrictedDirectory;
  private readonly Func<string, string, IDisposable> _openValidatedDirectory;
  private readonly Action<string> _deleteDirectory;
  private readonly bool _protectTerminalState;
  private readonly IVsixPlanArtifactRevocationStore _revocationStore;
  private readonly Func<string> _getCurrentUserSid;
  private readonly Func<DateTimeOffset> _getUtcNow;
  private readonly Func<Guid> _getBootIdentifier;
  private readonly Func<long> _getUptimeMilliseconds;
  private readonly TimeSpan _handoffLifetime;
  private readonly string _planArtifactRoot;
  private readonly ConcurrentDictionary<string, HandoffRegistration> _handoffs =
      new(StringComparer.OrdinalIgnoreCase);
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
  };

  public VsixPlanArtifactStore(
      ISecureArtifactStager stager,
      ITrustedFileVerifier verifier,
      IVsixManifestReader manifestReader)
      : this(
          stager,
          verifier,
          manifestReader,
          WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory,
          WindowsPlanArtifactDirectoryPolicy.GetCurrentUserSid,
          TimeSpan.FromHours(24),
          identityNeutralPlanArtifactRoot: null,
          openValidatedDirectory: WindowsPlanArtifactDirectoryPolicy.OpenValidatedRestrictedDirectory,
          protectTerminalState: true)
  {
  }

  internal VsixPlanArtifactStore(
      ISecureArtifactStager stager,
      ITrustedFileVerifier verifier,
      IVsixManifestReader manifestReader,
      Action<string, string> validateRestrictedDirectory,
      Func<string> getCurrentUserSid,
      TimeSpan? handoffLifetime = null,
      string? identityNeutralPlanArtifactRoot = null,
      Func<DateTimeOffset>? getUtcNow = null,
      Func<string, string, IDisposable>? openValidatedDirectory = null,
      Action<string>? deleteDirectory = null,
      bool protectTerminalState = false,
      IVsixPlanArtifactRevocationStore? revocationStore = null,
      Func<Guid>? getBootIdentifier = null,
      Func<long>? getUptimeMilliseconds = null)
  {
    _stager = stager ?? throw new ArgumentNullException(nameof(stager));
    _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    _manifestReader = manifestReader ?? throw new ArgumentNullException(nameof(manifestReader));
    _validateRestrictedDirectory = validateRestrictedDirectory ??
        throw new ArgumentNullException(nameof(validateRestrictedDirectory));
    _openValidatedDirectory = openValidatedDirectory ??
        (static (_, _) => NoopDirectoryValidationLease.Instance);
    _deleteDirectory = deleteDirectory ?? ArtifactCleanupQueue.Shared.DeleteDirectory;
    _protectTerminalState = protectTerminalState;
    _getCurrentUserSid = getCurrentUserSid ?? throw new ArgumentNullException(nameof(getCurrentUserSid));
    _getUtcNow = getUtcNow ?? (static () => DateTimeOffset.UtcNow);
    _getBootIdentifier = getBootIdentifier ?? WindowsVsixPlanArtifactClock.GetBootIdentifier;
    _getUptimeMilliseconds = getUptimeMilliseconds ?? (static () => Environment.TickCount64);
    _handoffLifetime = handoffLifetime ?? TimeSpan.FromHours(24);
    _planArtifactRoot = GetPlanArtifactRoot(
        identityNeutralPlanArtifactRoot ??
            WindowsPlanArtifactDirectoryPolicy.GetIdentityNeutralPlanArtifactRoot());
    _revocationStore = revocationStore ?? (protectTerminalState
        ? new WindowsVsixPlanArtifactRevocationStore(_planArtifactRoot)
        : NoopRevocationStore.Instance);
    if (_handoffLifetime <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(handoffLifetime));
    }
  }

  public Task<VsixPlanArtifactStageResult> StageAsync(
      string resourceId,
      string sourcePath,
      string expectedSha256,
      VsixPlanVisualStudioIdentity visualStudioIdentity,
      CancellationToken cancellationToken) => StageCoreAsync(
          resourceId,
          expectedSha256,
          visualStudioIdentity,
          token => _stager.StageVerifiedAsync(
              sourcePath,
              expectedSha256,
              SecureArtifactKind.VisualStudioExtension,
              token),
          cancellationToken);

  internal Task<VsixPlanArtifactStageResult> StageAsync(
      string resourceId,
      string sourcePath,
      string expectedSha256,
      string visualStudioInstanceId,
      CancellationToken cancellationToken) => StageAsync(
          resourceId,
          sourcePath,
          expectedSha256,
          LegacyIdentity(visualStudioInstanceId),
          cancellationToken);

  public Task<VsixPlanArtifactStageResult> StageAsync(
      string resourceId,
      Stream source,
      string expectedSha256,
      VsixPlanVisualStudioIdentity visualStudioIdentity,
      CancellationToken cancellationToken) => StageCoreAsync(
          resourceId,
          expectedSha256,
          visualStudioIdentity,
          token => _stager.StageVerifiedAsync(
              source,
              expectedSha256,
              SecureArtifactKind.VisualStudioExtension,
              token),
          cancellationToken);

  internal Task<VsixPlanArtifactStageResult> StageAsync(
      string resourceId,
      Stream source,
      string expectedSha256,
      string visualStudioInstanceId,
      CancellationToken cancellationToken) => StageAsync(
          resourceId,
          source,
          expectedSha256,
          LegacyIdentity(visualStudioInstanceId),
          cancellationToken);

  private async Task<VsixPlanArtifactStageResult> StageCoreAsync(
      string resourceId,
      string expectedSha256,
      VsixPlanVisualStudioIdentity visualStudioIdentity,
      Func<CancellationToken, Task<SecureArtifactStageResult>> stage,
      CancellationToken cancellationToken)
  {
    SecureStagedArtifact? staged = null;
    var handedOff = false;
    try
    {
      var stageResult = await stage(cancellationToken).ConfigureAwait(false);
      staged = stageResult.Artifact;
      if (staged is null)
      {
        return Failure(stageResult.Error, "The VSIX artifact could not be staged.");
      }

      var directory = Path.GetFullPath(staged.DirectoryPath);
      var creatorSid = _getCurrentUserSid();
      _validateRestrictedDirectory(directory, creatorSid);
      var artifactPath = Path.Combine(directory, "extension.vsix");
      if (!string.Equals(staged.Path, artifactPath, StringComparison.OrdinalIgnoreCase))
      {
        File.Copy(staged.Path, artifactPath, overwrite: false);
      }
      var verification = await _verifier.VerifySha256Async(
          artifactPath,
          expectedSha256,
          cancellationToken).ConfigureAwait(false);
      if (!verification.IsTrusted || verification.VerifiedPath is null ||
          verification.Sha256 is null)
      {
        return Failure(verification.Error, "The staged VSIX hash could not be verified.");
      }

      var manifestResult = await _manifestReader.ReadSourceAsync(
          verification.VerifiedPath,
          visualStudioIdentity.InstanceId,
          cancellationToken).ConfigureAwait(false);
      if (manifestResult.Manifest is null || manifestResult.Error is not null)
      {
        return Failure(manifestResult.Error, "The staged VSIX manifest is invalid.");
      }

      var ownerToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
      var activationProof = CreateActivationProof();
      var activationCommitment = CreateActivationCommitment(activationProof);
      var expiresAtUtc = _getUtcNow().Add(_handoffLifetime);
      var bootIdentifier = _getBootIdentifier();
      var expiresAtUptimeMilliseconds = CreateExpirationUptimeDeadline();
      var evidence = new VsixPlanArtifactEvidence(
          1,
          resourceId,
          verification.VerifiedPath,
          verification.Sha256,
          manifestResult.Manifest.Id,
          manifestResult.Manifest.Version,
          manifestResult.Manifest.ManifestPath,
          manifestResult.Manifest.VisualStudioInstanceId,
          visualStudioIdentity.ProductId,
          visualStudioIdentity.InstallationVersion,
          manifestResult.Manifest.Targets.ToArray(),
          creatorSid,
          directory,
          ownerToken,
          expiresAtUtc,
          Revoked: false,
          Consumed: false);
      _revocationStore.RecordIssued(
          ownerToken,
          Path.GetFileName(directory),
          expiresAtUtc,
          activationCommitment,
          bootIdentifier,
          expiresAtUptimeMilliseconds);
      WriteOwnershipMarker(evidence);
      staged.ReleaseForHandoff();
      try
      {
        RegisterHandoff(
            resourceId,
            ownerToken,
            directory,
            expiresAtUtc,
            bootIdentifier,
            expiresAtUptimeMilliseconds);
        handedOff = true;
      }
      catch
      {
        throw;
      }

      return new VsixPlanArtifactStageResult(
          EncodeEvidence(evidence, activationProof),
          manifestResult.Manifest,
          null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (OwnershipMarkerSizeException exception)
    {
      return Failure(null, exception.Message, exception);
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
        JsonException or SecurityException)
    {
      return Failure(null, "The VSIX artifact could not be prepared for approved-plan handoff.", exception);
    }
    finally
    {
      if (staged is not null && !handedOff)
      {
        await staged.DisposeAsync().ConfigureAwait(false);
      }
    }
  }

  public async Task<VsixPlanArtifactClaimResult> ClaimAsync(
      string resourceId,
      string stepId,
      string expectedSha256,
      VsixPlanVisualStudioIdentity visualStudioIdentity,
    CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!TryGetRegistrationLocator(stepId, out var locator))
    {
      return ClaimFailure("The approved VSIX plan evidence is invalid.");
    }

    VsixPlanArtifactEvidence? evidence = null;
    FileStream? readLock = null;
    ArtifactLease? lease = null;
    IDisposable? validatedDirectory = null;
    var directoryValidated = false;
    var protectedTerminal = false;
    var handedOff = false;
    try
    {
      evidence = ReadRegisteredEvidence(resourceId, locator);
      ValidateEvidencePaths(evidence!);
      EnsureNoTerminalState(evidence, locator);
      if (evidence.Consumed || evidence.Revoked)
      {
        throw new SecurityException("The approved VSIX artifact is no longer claimable.");
      }

      if (!string.Equals(evidence!.ResourceId, resourceId, StringComparison.Ordinal) ||
          !string.Equals(evidence.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase) ||
          !string.Equals(
              evidence.VisualStudioInstanceId,
              visualStudioIdentity.InstanceId,
              StringComparison.Ordinal) ||
          !string.Equals(
              evidence.VisualStudioProductId,
              visualStudioIdentity.ProductId,
              StringComparison.Ordinal) ||
          !string.Equals(
              evidence.VisualStudioInstallationVersion,
              visualStudioIdentity.InstallationVersion,
              StringComparison.Ordinal))
      {
        throw new InvalidDataException(
            "The approved VSIX evidence does not match the desired resource.");
      }

      validatedDirectory = _openValidatedDirectory(
          evidence!.OwnershipDirectory,
          evidence.CreatorSid);
      _validateRestrictedDirectory(evidence.OwnershipDirectory, evidence.CreatorSid);
      lease = ArtifactLease.Acquire(evidence.OwnershipDirectory);
      ValidateOwnershipMarker(evidence, locator);
      directoryValidated = true;
      _revocationStore.ClaimStarted(
          evidence.OwnershipToken,
          Path.GetFileName(evidence.OwnershipDirectory));
      protectedTerminal = true;
      readLock = new FileStream(
          evidence.ArtifactPath,
          FileMode.Open,
          FileAccess.Read,
          FileShare.Read,
          bufferSize: 1,
          FileOptions.SequentialScan);
      if (File.GetAttributes(readLock.SafeFileHandle).HasFlag(FileAttributes.ReparsePoint))
      {
        throw new SecurityException("The approved VSIX artifact is redirected.");
      }

      var verification = await _verifier.VerifySha256Async(
          evidence.ArtifactPath,
          evidence.Sha256,
          cancellationToken).ConfigureAwait(false);
      if (!verification.IsTrusted || verification.VerifiedPath is null ||
          !string.Equals(verification.VerifiedPath, evidence.ArtifactPath, StringComparison.OrdinalIgnoreCase) ||
          !string.Equals(verification.Sha256, evidence.Sha256, StringComparison.OrdinalIgnoreCase))
      {
        return ClaimFailure(verification.Error, "The approved VSIX artifact hash is invalid.");
      }

      var manifestResult = await _manifestReader.ReadSourceAsync(
          evidence.ArtifactPath,
          evidence.VisualStudioInstanceId,
          cancellationToken).ConfigureAwait(false);
      if (manifestResult.Manifest is null || manifestResult.Error is not null ||
          !ManifestMatchesEvidence(manifestResult.Manifest, evidence))
      {
        return ClaimFailure(manifestResult.Error, "The approved VSIX manifest evidence no longer matches.");
      }

      _revocationStore.Consume(
          evidence.OwnershipToken,
          Path.GetFileName(evidence.OwnershipDirectory));
      PersistConsumedTerminalState(evidence);
      evidence = PersistConsumedEvidence(evidence);
      var artifact = new ClaimedVsixPlanArtifact(
          evidence.OwnershipDirectory,
          evidence.ArtifactPath,
          manifestResult.Manifest,
          readLock,
          lease,
          validatedDirectory,
          _deleteDirectory);
      readLock = null;
      lease = null;
      validatedDirectory = null;
      handedOff = true;
      RemoveHandoff(resourceId, evidence.OwnershipDirectory);
      return new VsixPlanArtifactClaimResult(artifact, null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        UnauthorizedAccessException or InvalidDataException or JsonException or SecurityException)
    {
      return ClaimFailure(null, "The approved VSIX artifact could not be securely claimed.", exception);
    }
    finally
    {
      readLock?.Dispose();
      lease?.Dispose();
      validatedDirectory?.Dispose();
      if (directoryValidated && protectedTerminal && !handedOff)
      {
        RemoveHandoff(resourceId, evidence!.OwnershipDirectory);
        _deleteDirectory(evidence!.OwnershipDirectory);
      }
    }
  }

  internal Task<VsixPlanArtifactClaimResult> ClaimAsync(
      string resourceId,
      string stepId,
      string expectedSha256,
      string visualStudioInstanceId,
      CancellationToken cancellationToken) => ClaimAsync(
          resourceId,
          stepId,
          expectedSha256,
          LegacyIdentity(visualStudioInstanceId),
          cancellationToken);

  public Task AbandonAsync(
      string resourceId,
      string stepId,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (TryAbandonDurable(resourceId, stepId))
    {
      _ = TryUnregisterHandoff(resourceId, stepId);
    }

    return Task.CompletedTask;
  }

  internal static bool HasValidStepEvidence(string resourceId, string stepId) =>
      !string.IsNullOrWhiteSpace(resourceId) && TryGetRegistrationLocator(stepId, out _);

  internal static bool IsPlanArtifactStep(string resourceId, string stepId) =>
      !string.IsNullOrWhiteSpace(resourceId) && TryGetRegistrationLocator(stepId, out _);

  internal static bool TryRevokeExpiredDirectory(
      string directory,
      DateTimeOffset utcNow,
      IVsixPlanArtifactRevocationStore revocationStore)
  {
    ArgumentNullException.ThrowIfNull(revocationStore);
    try
    {
      ValidateOwnershipDirectoryPath(directory);
      var evidence = ReadOwnershipMarker(directory);
      var locator = new VsixPlanArtifactLocator(
          evidence.OwnershipToken,
          Path.GetFileName(directory));
      ValidateRegistration(evidence, evidence.ResourceId, locator);
      if (!string.Equals(
              directory,
              evidence.OwnershipDirectory,
              StringComparison.OrdinalIgnoreCase))
      {
        return false;
      }

      WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory(
          directory,
          evidence.CreatorSid);
      using var lease = ArtifactLease.Acquire(directory);
      WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory(
          directory,
          evidence.CreatorSid);
      var sealedEvidence = ReadOwnershipMarker(directory);
      if (!EvidenceMatches(sealedEvidence, evidence))
      {
        return false;
      }

      var directoryName = Path.GetFileName(directory);
      var state = revocationStore.GetState(evidence.OwnershipToken, directoryName);
      if (!state.IsExpired(
              utcNow,
              WindowsVsixPlanArtifactClock.GetBootIdentifier(),
              Environment.TickCount64))
      {
        return false;
      }

      if (!state.IsTerminal)
      {
        revocationStore.Revoke(evidence.OwnershipToken, directoryName);
      }

      _ = PersistRevokedEvidence(sealedEvidence);
      return true;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
        JsonException or NotSupportedException or SecurityException)
    {
      return false;
    }
  }

  private void RegisterHandoff(
      string resourceId,
      string registrationToken,
      string directory,
      DateTimeOffset expiresAtUtc,
      Guid bootIdentifier,
      long expiresAtUptimeMilliseconds)
  {
    var registration = new HandoffRegistration(
        registrationToken,
        directory,
        expiresAtUtc,
        bootIdentifier,
        expiresAtUptimeMilliseconds,
        new CancellationTokenSource());
    try
    {
      _handoffs.AddOrUpdate(resourceId, registration, (_, existing) =>
      {
        RevokeDurable(resourceId, existing.RegistrationToken, existing.Directory);
        _deleteDirectory(existing.Directory);
        existing.Cancellation.Cancel();
        return registration;
      });
    }
    catch
    {
      registration.Cancellation.Dispose();
      throw;
    }

    try
    {
      _revocationStore.Activate(registrationToken, Path.GetFileName(directory));
    }
    catch
    {
      try
      {
        _revocationStore.Revoke(registrationToken, Path.GetFileName(directory));
        if (_handoffs.TryRemove(
                new KeyValuePair<string, HandoffRegistration>(resourceId, registration)))
        {
          registration.Cancellation.Cancel();
          registration.Cancellation.Dispose();
          _deleteDirectory(directory);
        }
      }
      catch (Exception cleanupException) when (cleanupException is ArgumentException or IOException or
          UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
          JsonException or NotSupportedException or SecurityException)
      {
        // Pending issuance is already unclaimable. Retain the leaf if terminal cleanup also fails.
      }

      throw;
    }

    _ = ExpireHandoffAsync(resourceId, registration);
  }

  private bool TryUnregisterHandoff(string resourceId, string stepId)
  {
    if (!TryGetRegistrationLocator(stepId, out var locator) ||
        !_handoffs.TryGetValue(resourceId, out var registration) ||
        !string.Equals(
            locator.RegistrationToken,
            registration.RegistrationToken,
            StringComparison.Ordinal) ||
        !string.Equals(
            locator.DirectoryName,
            Path.GetFileName(registration.Directory),
            StringComparison.OrdinalIgnoreCase) ||
        !_handoffs.TryRemove(
            new KeyValuePair<string, HandoffRegistration>(resourceId, registration)))
    {
      return false;
    }

    registration.Cancellation.Cancel();
    return true;
  }

  private bool TryAbandonDurable(string resourceId, string stepId)
  {
    if (!TryGetRegistrationLocator(stepId, out var locator))
    {
      return false;
    }

    ArtifactLease? lease = null;
    IDisposable? validatedDirectory = null;
    string? directory = null;
    try
    {
      var registration = ReadRegisteredEvidence(resourceId, locator);
      directory = registration.OwnershipDirectory;
      validatedDirectory = _openValidatedDirectory(
          registration.OwnershipDirectory,
          registration.CreatorSid);
      _validateRestrictedDirectory(registration.OwnershipDirectory, registration.CreatorSid);
      lease = ArtifactLease.Acquire(registration.OwnershipDirectory);
      _validateRestrictedDirectory(registration.OwnershipDirectory, registration.CreatorSid);
      var sealedRegistration = ReadOwnershipMarker(registration.OwnershipDirectory);
      if (!EvidenceMatches(sealedRegistration, registration))
      {
        throw new SecurityException("The approved VSIX registration changed during cleanup.");
      }

      var directoryName = Path.GetFileName(sealedRegistration.OwnershipDirectory);
      _revocationStore.Revoke(sealedRegistration.OwnershipToken, directoryName);

      _ = PersistRevokedEvidence(registration);
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
        JsonException or NotSupportedException or SecurityException)
    {
      return false;
    }
    finally
    {
      lease?.Dispose();
      validatedDirectory?.Dispose();
    }

    _deleteDirectory(directory!);
    return true;
  }

  private void RevokeDurable(
      string resourceId,
      string registrationToken,
      string directory)
  {
    var locator = new VsixPlanArtifactLocator(
        registrationToken,
        Path.GetFileName(directory));
    var registration = ReadRegisteredEvidence(resourceId, locator);
    if (!string.Equals(
            registration.OwnershipDirectory,
            directory,
            StringComparison.OrdinalIgnoreCase))
    {
      throw new SecurityException("The approved VSIX registration directory changed.");
    }

    using var validatedDirectory = _openValidatedDirectory(
        registration.OwnershipDirectory,
        registration.CreatorSid);
    _validateRestrictedDirectory(registration.OwnershipDirectory, registration.CreatorSid);
    using var lease = ArtifactLease.Acquire(registration.OwnershipDirectory);
    var sealedRegistration = ReadOwnershipMarker(registration.OwnershipDirectory);
    if (!EvidenceMatches(sealedRegistration, registration) || sealedRegistration.Consumed)
    {
      throw new SecurityException("The approved VSIX registration changed during revocation.");
    }

    var directoryName = Path.GetFileName(sealedRegistration.OwnershipDirectory);
    _revocationStore.Revoke(sealedRegistration.OwnershipToken, directoryName);

    _ = PersistRevokedEvidence(sealedRegistration);
  }

  private async Task ExpireHandoffAsync(string resourceId, HandoffRegistration registration)
  {
    var retainRegistration = false;
    try
    {
      while (true)
      {
        var remaining = GetRemainingLifetime(registration);
        if (remaining > TimeSpan.Zero)
        {
          await Task.Delay(remaining, registration.Cancellation.Token).ConfigureAwait(false);
          continue;
        }

        RevokeDurable(resourceId, registration.RegistrationToken, registration.Directory);
        if (_handoffs.TryRemove(
                new KeyValuePair<string, HandoffRegistration>(resourceId, registration)))
        {
          registration.Cancellation.Cancel();
          _deleteDirectory(registration.Directory);
        }

        break;
      }
    }
    catch (OperationCanceledException) when (registration.Cancellation.IsCancellationRequested)
    {
      // Claimed or superseded artifacts are cleaned by their owning lifecycle.
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
        JsonException or NotSupportedException or SecurityException)
    {
      // Durable expiry failed. Keep the active registration and leaf for a later lifecycle owner.
      retainRegistration = true;
    }
    finally
    {
      if (!retainRegistration)
      {
        registration.Cancellation.Dispose();
      }
    }
  }

  private TimeSpan GetRemainingLifetime(HandoffRegistration registration)
  {
    if (registration.BootIdentifier != _getBootIdentifier())
    {
      return TimeSpan.Zero;
    }

    var uptimeMilliseconds = _getUptimeMilliseconds();
    if (uptimeMilliseconds < 0)
    {
      return TimeSpan.Zero;
    }

    var remainingUptimeMilliseconds =
        registration.ExpiresAtUptimeMilliseconds - uptimeMilliseconds;
    var remainingUtc = registration.ExpiresAtUtc - _getUtcNow();
    if (remainingUptimeMilliseconds <= 0 || remainingUtc <= TimeSpan.Zero)
    {
      return TimeSpan.Zero;
    }

    return TimeSpan.FromMilliseconds(
        Math.Min(remainingUptimeMilliseconds, remainingUtc.TotalMilliseconds));
  }

  private long CreateExpirationUptimeDeadline()
  {
    var uptimeMilliseconds = _getUptimeMilliseconds();
    if (uptimeMilliseconds < 0)
    {
      throw new SecurityException("The Windows uptime clock is invalid.");
    }

    try
    {
      return checked(
          uptimeMilliseconds + (long)Math.Ceiling(_handoffLifetime.TotalMilliseconds));
    }
    catch (OverflowException exception)
    {
      throw new SecurityException("The VSIX uptime deadline is invalid.", exception);
    }
  }

  private void RemoveHandoff(string resourceId, string directory)
  {
    if (_handoffs.TryGetValue(resourceId, out var registration) &&
        string.Equals(registration.Directory, directory, StringComparison.OrdinalIgnoreCase) &&
        _handoffs.TryRemove(
            new KeyValuePair<string, HandoffRegistration>(resourceId, registration)))
    {
      registration.Cancellation.Cancel();
    }
  }

  private static void WriteOwnershipMarker(VsixPlanArtifactEvidence registration)
  {
    var bytes = JsonSerializer.SerializeToUtf8Bytes(registration, JsonOptions);
    if (bytes.Length > MaxOwnershipMarkerBytes)
    {
      throw new OwnershipMarkerSizeException(bytes.Length, MaxOwnershipMarkerBytes);
    }

    var markerPath = Path.Combine(registration.OwnershipDirectory, OwnerFileName);
    var temporaryPath = Path.Combine(
        registration.OwnershipDirectory,
        $".{OwnerFileName}.{Guid.NewGuid():N}.tmp");
    try
    {
      using (var stream = new FileStream(
                 temporaryPath,
                 FileMode.CreateNew,
                 FileAccess.Write,
                 FileShare.None,
                 bufferSize: 1,
                 FileOptions.WriteThrough))
      {
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
      }

      File.Move(temporaryPath, markerPath, overwrite: false);
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        File.Delete(temporaryPath);
      }
    }
  }

  private static VsixPlanArtifactEvidence PersistConsumedEvidence(
      VsixPlanArtifactEvidence evidence) =>
      PersistEvidence(evidence with { Consumed = true });

  private static VsixPlanArtifactEvidence PersistRevokedEvidence(
      VsixPlanArtifactEvidence evidence) =>
      PersistEvidence(evidence with { Revoked = true });

  private static VsixPlanArtifactEvidence PersistEvidence(
      VsixPlanArtifactEvidence updated)
  {
    var markerPath = Path.Combine(updated.OwnershipDirectory, OwnerFileName);
    var temporaryPath = Path.Combine(
        updated.OwnershipDirectory,
        $".{OwnerFileName}.{Guid.NewGuid():N}.tmp");
    try
    {
      var bytes = JsonSerializer.SerializeToUtf8Bytes(updated, JsonOptions);
      using (var stream = new FileStream(
                 temporaryPath,
                 FileMode.CreateNew,
                 FileAccess.Write,
                 FileShare.None,
                 bufferSize: 1,
                 FileOptions.WriteThrough))
      {
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
      }

      File.Replace(temporaryPath, markerPath, destinationBackupFileName: null);
      return updated;
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        ArtifactCleanupQueue.Shared.DeleteFile(temporaryPath);
      }
    }
  }

  private void EnsureNoTerminalState(
      VsixPlanArtifactEvidence evidence,
      VsixPlanArtifactLocator locator)
  {
    var path = GetTerminalStatePath(evidence);
    var state = _revocationStore.GetState(
        evidence.OwnershipToken,
        Path.GetFileName(evidence.OwnershipDirectory));
    if (File.Exists(path) || Directory.Exists(path) ||
        !IsAuthorizedActivation(state, locator) ||
        IsExpired(state))
    {
      throw new SecurityException("The approved VSIX artifact has a terminal claim state.");
    }
  }

  private void PersistConsumedTerminalState(VsixPlanArtifactEvidence evidence)
  {
    var path = GetTerminalStatePath(evidence);
    var bytes = Encoding.UTF8.GetBytes(
        $"wdem-vsix-terminal-v1\n{evidence.OwnershipToken}\nconsumed\n");
    if (_protectTerminalState)
    {
      WindowsPlanArtifactDirectoryPolicy.CreateAdministratorOnlyFile(path, bytes);
      return;
    }

    using var stream = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.Read,
        bufferSize: 1,
        FileOptions.WriteThrough);
    stream.Write(bytes);
    stream.Flush(flushToDisk: true);
  }

  private string GetTerminalStatePath(VsixPlanArtifactEvidence evidence)
  {
    var directoryName = Path.GetFileName(evidence.OwnershipDirectory);
    var path = Path.Combine(_planArtifactRoot, $".{directoryName}.wdem-vsix-terminal");
    if (!string.Equals(
            Path.GetDirectoryName(evidence.OwnershipDirectory),
            _planArtifactRoot,
            StringComparison.OrdinalIgnoreCase) ||
        !Guid.TryParseExact(directoryName, "N", out _) ||
        !string.Equals(Path.GetDirectoryName(path), _planArtifactRoot, StringComparison.OrdinalIgnoreCase))
    {
      throw new SecurityException("The approved VSIX terminal-state path is invalid.");
    }

    return path;
  }

  private void ValidateOwnershipMarker(
      VsixPlanArtifactEvidence evidence,
      VsixPlanArtifactLocator locator)
  {
    var registration = ReadOwnershipMarker(evidence.OwnershipDirectory);
    ValidateRegistration(registration, evidence.ResourceId, locator);
    var state = _revocationStore.GetState(
        evidence.OwnershipToken,
        Path.GetFileName(evidence.OwnershipDirectory));
    if (!EvidenceMatches(registration, evidence) ||
        !IsAuthorizedActivation(state, locator) ||
        IsExpired(state))
    {
      throw new SecurityException("The approved VSIX ownership registration is invalid or expired.");
    }
  }

  private static VsixPlanArtifactEvidence ReadOwnershipMarker(string directory)
  {
    var path = Path.Combine(directory, OwnerFileName);
    using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 1,
        FileOptions.SequentialScan);
    if (File.GetAttributes(stream.SafeFileHandle).HasFlag(FileAttributes.ReparsePoint) ||
        stream.Length <= 0 || stream.Length > MaxOwnershipMarkerBytes)
    {
      throw new SecurityException("The approved VSIX ownership registration is invalid.");
    }

    return JsonSerializer.Deserialize<VsixPlanArtifactEvidence>(stream, JsonOptions) ??
        throw new InvalidDataException("The approved VSIX ownership registration is empty.");
  }

  private static void ValidateRegistration(
      VsixPlanArtifactEvidence registration,
      string expectedResourceId,
      VsixPlanArtifactLocator locator)
  {
    var directory = registration.OwnershipDirectory;
    var artifactPath = Path.Combine(directory, "extension.vsix");
    if (registration.SchemaVersion != 1 ||
        !string.Equals(registration.ResourceId, expectedResourceId, StringComparison.Ordinal) ||
        string.IsNullOrWhiteSpace(registration.CreatorSid) ||
        registration.CreatorSid.Any(char.IsControl) ||
        !string.Equals(
            registration.OwnershipToken,
            locator.RegistrationToken,
            StringComparison.Ordinal) ||
        !string.Equals(
            Path.GetFileName(directory),
            locator.DirectoryName,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(registration.ArtifactPath, artifactPath, StringComparison.OrdinalIgnoreCase) ||
        registration.ExpiresAtUtc == default)
    {
      throw new SecurityException("The approved VSIX ownership registration does not match the plan.");
    }

    ValidateEvidencePaths(registration);
  }

  private static void ValidateEvidencePaths(VsixPlanArtifactEvidence evidence)
  {
    if (evidence.SchemaVersion != 1 ||
        string.IsNullOrWhiteSpace(evidence.ResourceId) ||
        string.IsNullOrWhiteSpace(evidence.ArtifactPath) ||
        string.IsNullOrWhiteSpace(evidence.Sha256) || evidence.Sha256.Length != 64 ||
        !evidence.Sha256.All(Uri.IsHexDigit) ||
        string.IsNullOrWhiteSpace(evidence.ManifestId) ||
        string.IsNullOrWhiteSpace(evidence.ManifestVersion) ||
        string.IsNullOrWhiteSpace(evidence.ManifestPath) ||
        string.IsNullOrWhiteSpace(evidence.VisualStudioInstanceId) ||
        string.IsNullOrWhiteSpace(evidence.VisualStudioProductId) ||
        string.IsNullOrWhiteSpace(evidence.VisualStudioInstallationVersion) ||
        evidence.InstallationTargets is null ||
        evidence.InstallationTargets.Any(target =>
            target is null || string.IsNullOrWhiteSpace(target.Id)) ||
        string.IsNullOrWhiteSpace(evidence.CreatorSid) ||
        evidence.CreatorSid.Any(char.IsControl) ||
        string.IsNullOrWhiteSpace(evidence.OwnershipDirectory) ||
        string.IsNullOrWhiteSpace(evidence.OwnershipToken) ||
        evidence.OwnershipToken.Length != 32 || !evidence.OwnershipToken.All(Uri.IsHexDigit) ||
        evidence.ExpiresAtUtc == default ||
        !Path.IsPathFullyQualified(evidence.ArtifactPath) ||
        !Path.IsPathFullyQualified(evidence.OwnershipDirectory))
    {
      throw new InvalidDataException("The approved VSIX evidence is incomplete.");
    }

    var directory = Path.GetFullPath(evidence.OwnershipDirectory);
    var path = Path.GetFullPath(evidence.ArtifactPath);
    ValidateOwnershipDirectoryPath(directory);
    if (!string.Equals(directory, evidence.OwnershipDirectory, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(path, evidence.ArtifactPath, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(path, Path.Combine(directory, "extension.vsix"), StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidDataException("The approved VSIX artifact path is not canonical.");
    }
  }

  private static bool ManifestMatchesEvidence(
      VsixManifest manifest,
      VsixPlanArtifactEvidence evidence) =>
      string.Equals(manifest.Id, evidence.ManifestId, StringComparison.Ordinal) &&
      string.Equals(manifest.Version, evidence.ManifestVersion, StringComparison.Ordinal) &&
      string.Equals(manifest.ManifestPath, evidence.ManifestPath, StringComparison.OrdinalIgnoreCase) &&
      string.Equals(
          manifest.VisualStudioInstanceId,
          evidence.VisualStudioInstanceId,
          StringComparison.Ordinal) &&
      manifest.Targets.SequenceEqual(evidence.InstallationTargets);

  private static bool EvidenceMatches(
      VsixPlanArtifactEvidence left,
      VsixPlanArtifactEvidence right) =>
      left.SchemaVersion == right.SchemaVersion &&
      string.Equals(left.ResourceId, right.ResourceId, StringComparison.Ordinal) &&
      string.Equals(left.ArtifactPath, right.ArtifactPath, StringComparison.OrdinalIgnoreCase) &&
      string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase) &&
      string.Equals(left.ManifestId, right.ManifestId, StringComparison.Ordinal) &&
      string.Equals(left.ManifestVersion, right.ManifestVersion, StringComparison.Ordinal) &&
      string.Equals(left.ManifestPath, right.ManifestPath, StringComparison.OrdinalIgnoreCase) &&
      string.Equals(
          left.VisualStudioInstanceId,
          right.VisualStudioInstanceId,
          StringComparison.Ordinal) &&
      string.Equals(
          left.VisualStudioProductId,
          right.VisualStudioProductId,
          StringComparison.Ordinal) &&
      string.Equals(
          left.VisualStudioInstallationVersion,
          right.VisualStudioInstallationVersion,
          StringComparison.Ordinal) &&
      left.InstallationTargets.SequenceEqual(right.InstallationTargets) &&
      string.Equals(left.CreatorSid, right.CreatorSid, StringComparison.Ordinal) &&
      string.Equals(
          left.OwnershipDirectory,
          right.OwnershipDirectory,
          StringComparison.OrdinalIgnoreCase) &&
      string.Equals(left.OwnershipToken, right.OwnershipToken, StringComparison.Ordinal) &&
      left.ExpiresAtUtc == right.ExpiresAtUtc &&
      left.Revoked == right.Revoked &&
      left.Consumed == right.Consumed;

  private static string EncodeEvidence(
      VsixPlanArtifactEvidence evidence,
      string activationProof) =>
      EvidencePrefix + evidence.OwnershipToken + ":" +
      Path.GetFileName(evidence.OwnershipDirectory) + ":" + activationProof;

  private static string CreateActivationProof() =>
      Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
          .TrimEnd('=')
          .Replace('+', '-')
          .Replace('/', '_');

  private static string CreateActivationCommitment(string activationProof) =>
      Convert.ToHexString(SHA256.HashData(DecodeActivationProof(activationProof)));

  private static byte[] DecodeActivationProof(string activationProof) =>
      Convert.FromBase64String(
          activationProof.Replace('-', '+').Replace('_', '/') + "=");

  private static bool IsAuthorizedActivation(
      VsixPlanArtifactLedgerState state,
      VsixPlanArtifactLocator locator)
  {
    if (state.Status != VsixPlanArtifactLedgerStatus.Active)
    {
      return false;
    }

    var expected = Convert.FromHexString(state.ActivationCommitment);
    var actual = SHA256.HashData(DecodeActivationProof(locator.ActivationProof));
    return CryptographicOperations.FixedTimeEquals(expected, actual);
  }

  private bool IsExpired(VsixPlanArtifactLedgerState state) =>
      state.IsExpired(
          _getUtcNow(),
          _getBootIdentifier(),
          _getUptimeMilliseconds());

  private static VsixPlanVisualStudioIdentity LegacyIdentity(string instanceId) =>
      new(instanceId, "unknown", "unknown");

  private static bool TryGetRegistrationLocator(
      string stepId,
      out VsixPlanArtifactLocator locator)
  {
    locator = null!;
    const int tokenLength = 32;
    const int directoryNameLength = 32;
    const int activationProofLength = 43;
    var prefix = EvidencePrefix;
    if (!stepId.StartsWith(prefix, StringComparison.Ordinal))
    {
      return false;
    }

    var tokenSeparator = stepId.IndexOf(':', prefix.Length);
    var directorySeparator = stepId.IndexOf(':', tokenSeparator + 1);
    if (tokenSeparator != prefix.Length + tokenLength ||
        directorySeparator != tokenSeparator + 1 + directoryNameLength ||
        stepId.Length != directorySeparator + 1 + activationProofLength)
    {
      return false;
    }

    var registrationToken = stepId[prefix.Length..tokenSeparator];
    var directoryName = stepId[(tokenSeparator + 1)..directorySeparator];
    var activationProof = stepId[(directorySeparator + 1)..];
    if (!registrationToken.All(Uri.IsHexDigit) ||
        !activationProof.All(IsBase64UrlCharacter) ||
        !Guid.TryParseExact(directoryName, "N", out _))
    {
      return false;
    }

    locator = new VsixPlanArtifactLocator(registrationToken, directoryName, activationProof);
    return true;
  }

  private static bool IsBase64UrlCharacter(char value) =>
      char.IsAsciiLetterOrDigit(value) || value is '-' or '_';

  private VsixPlanArtifactEvidence ReadRegisteredEvidence(
      string resourceId,
      VsixPlanArtifactLocator locator)
  {
    var candidate = Path.Combine(_planArtifactRoot, locator.DirectoryName);
    try
    {
      ValidateOwnershipDirectoryPath(candidate);
      var evidence = ReadOwnershipMarker(candidate);
      ValidateRegistration(evidence, resourceId, locator);
      if (!string.Equals(
              candidate,
              evidence.OwnershipDirectory,
              StringComparison.OrdinalIgnoreCase))
      {
        throw new SecurityException("The approved VSIX locator is invalid.");
      }

      return evidence;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        UnauthorizedAccessException or InvalidDataException or JsonException or
        NotSupportedException or SecurityException)
    {
      throw new InvalidDataException(
          "The approved VSIX locator does not resolve to a sealed registration.",
          exception);
    }
  }

  private static string GetPlanArtifactRoot(string identityNeutralPlanArtifactRoot)
  {
    if (string.IsNullOrWhiteSpace(identityNeutralPlanArtifactRoot) ||
        !Path.IsPathFullyQualified(identityNeutralPlanArtifactRoot))
    {
      throw new ArgumentException(
          "The identity-neutral plan artifact root must be fully qualified.",
          nameof(identityNeutralPlanArtifactRoot));
    }

    return Path.GetFullPath(identityNeutralPlanArtifactRoot);
  }

  private static void ValidateOwnershipDirectoryPath(string path)
  {
    if (string.IsNullOrWhiteSpace(path) ||
        path.Any(char.IsControl) ||
        !Path.IsPathFullyQualified(path))
    {
      throw new InvalidDataException("The approved VSIX ownership directory is invalid.");
    }

    var fullPath = Path.GetFullPath(path);
    var leaf = Path.GetFileName(fullPath);
    var root = Path.GetDirectoryName(fullPath);
    if (!string.Equals(fullPath, path, StringComparison.OrdinalIgnoreCase) ||
        !Guid.TryParseExact(leaf, "N", out _) ||
        !string.Equals(Path.GetFileName(root), "PlanArtifacts", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            Path.GetFileName(Path.GetDirectoryName(root)),
            "Wdem",
            StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidDataException("The approved VSIX ownership directory is outside the bounded root.");
    }
  }

  private static VsixPlanArtifactStageResult Failure(
      StructuredError? error,
      string detail,
      Exception? exception = null) => new(
          null,
          null,
          error ?? new StructuredError(
              WdemErrorCode.ConfigurationError,
              "VSIX approved artifact staging failed.",
              detail)
          {
            UnderlyingException = exception
          });

  private static VsixPlanArtifactClaimResult ClaimFailure(
      string detail,
      Exception? exception = null) => ClaimFailure(null, detail, exception);

  private static VsixPlanArtifactClaimResult ClaimFailure(
      StructuredError? error,
      string detail,
      Exception? exception = null) => new(
          null,
          error ?? new StructuredError(
              WdemErrorCode.ConfigurationError,
              "Approved VSIX artifact validation failed.",
              detail)
          {
            UnderlyingException = exception
          });

  private sealed record VsixPlanArtifactEvidence(
      int SchemaVersion,
      string ResourceId,
      string ArtifactPath,
      string Sha256,
      string ManifestId,
      string ManifestVersion,
      string ManifestPath,
      string VisualStudioInstanceId,
      string VisualStudioProductId,
      string VisualStudioInstallationVersion,
      IReadOnlyList<VsixInstallationTarget> InstallationTargets,
      string CreatorSid,
      string OwnershipDirectory,
      string OwnershipToken,
      DateTimeOffset ExpiresAtUtc,
      bool Revoked,
      bool Consumed);

  private sealed record VsixPlanArtifactLocator(
      string RegistrationToken,
      string DirectoryName,
      string ActivationProof = "");

  private sealed record HandoffRegistration(
      string RegistrationToken,
      string Directory,
      DateTimeOffset ExpiresAtUtc,
      Guid BootIdentifier,
      long ExpiresAtUptimeMilliseconds,
      CancellationTokenSource Cancellation);

  private sealed class NoopDirectoryValidationLease : IDisposable
  {
    public static NoopDirectoryValidationLease Instance { get; } = new();

    public void Dispose()
    {
    }
  }

  private sealed class NoopRevocationStore : IVsixPlanArtifactRevocationStore
  {
    public static NoopRevocationStore Instance { get; } = new();
    private readonly ConcurrentDictionary<(string Token, string Directory), VsixPlanArtifactLedgerState>
        _issuances = new();

    public void RecordIssued(
      string ownershipToken,
      string directoryName,
      DateTimeOffset expiresAtUtc,
      string activationCommitment,
      Guid bootIdentifier,
      long expiresAtUptimeMilliseconds) =>
        _issuances.TryAdd(
            (ownershipToken, directoryName),
            new VsixPlanArtifactLedgerState(
                expiresAtUtc,
                activationCommitment,
                bootIdentifier,
                expiresAtUptimeMilliseconds,
                VsixPlanArtifactLedgerStatus.Pending));

    public void Activate(string ownershipToken, string directoryName) =>
        SetStatus(ownershipToken, directoryName, VsixPlanArtifactLedgerStatus.Active);

    public void ClaimStarted(string ownershipToken, string directoryName) =>
        SetStatus(ownershipToken, directoryName, VsixPlanArtifactLedgerStatus.ClaimStarted);

    public void Consume(string ownershipToken, string directoryName) =>
        SetStatus(ownershipToken, directoryName, VsixPlanArtifactLedgerStatus.Consumed);

    public VsixPlanArtifactLedgerState GetState(string ownershipToken, string directoryName) =>
        _issuances.TryGetValue((ownershipToken, directoryName), out var state)
            ? state
            : throw new SecurityException("The VSIX issuance record is missing.");

    public DateTimeOffset GetIssuedExpiry(string ownershipToken, string directoryName) =>
        GetState(ownershipToken, directoryName).ExpiresAtUtc;

    public void Revoke(string ownershipToken, string directoryName) =>
        SetStatus(ownershipToken, directoryName, VsixPlanArtifactLedgerStatus.Revoked);

    public bool IsRevoked(string ownershipToken, string directoryName) =>
        GetState(ownershipToken, directoryName).Status == VsixPlanArtifactLedgerStatus.Revoked;

    private void SetStatus(
        string ownershipToken,
        string directoryName,
        VsixPlanArtifactLedgerStatus status) =>
        _issuances.AddOrUpdate(
            (ownershipToken, directoryName),
            static _ => throw new SecurityException("The VSIX issuance record is missing."),
            (_, existing) => existing.Status >= status
                ? existing
                : existing with { Status = status });
  }

  private sealed class OwnershipMarkerSizeException(int actualBytes, int maximumBytes)
      : IOException(
          $"The VSIX ownership marker is {actualBytes} bytes and exceeds the " +
          $"{maximumBytes}-byte limit.");
}
