using System.Collections.Concurrent;
using System.Security;
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

internal interface IVsixPlanArtifactStore
{
  Task<VsixPlanArtifactStageResult> StageAsync(
      string resourceId,
      string sourcePath,
      string expectedSha256,
      string visualStudioInstanceId,
      CancellationToken cancellationToken);

  Task<VsixPlanArtifactClaimResult> ClaimAsync(
      string resourceId,
      string stepId,
      string expectedSha256,
      string visualStudioInstanceId,
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

  internal ClaimedVsixPlanArtifact(
      string directoryPath,
      string path,
      VsixManifest manifest,
      FileStream readLock,
      ArtifactLease lease)
  {
    _directoryPath = directoryPath;
    Path = path;
    Manifest = manifest;
    _readLock = readLock;
    _lease = lease;
  }

  public string Path { get; }
  public VsixManifest Manifest { get; }

  public ValueTask DisposeAsync()
  {
    Interlocked.Exchange(ref _readLock, null)?.Dispose();
    Interlocked.Exchange(ref _lease, null)?.Dispose();
    ArtifactCleanupQueue.Shared.DeleteDirectory(_directoryPath);
    return ValueTask.CompletedTask;
  }
}

internal sealed class VsixPlanArtifactStore : IVsixPlanArtifactStore
{
  private const string EvidencePrefix = "vsix-v1:";
  private const string OwnerFileName = ".wdem-vsix-owner";
  private readonly ISecureArtifactStager _stager;
  private readonly ITrustedFileVerifier _verifier;
  private readonly IVsixManifestReader _manifestReader;
  private readonly Action<string> _validateRestrictedDirectory;
  private readonly TimeSpan _handoffLifetime;
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
          TimeSpan.FromHours(24))
  {
  }

  internal VsixPlanArtifactStore(
      ISecureArtifactStager stager,
      ITrustedFileVerifier verifier,
      IVsixManifestReader manifestReader,
      Action<string> validateRestrictedDirectory,
      TimeSpan? handoffLifetime = null)
  {
    _stager = stager ?? throw new ArgumentNullException(nameof(stager));
    _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    _manifestReader = manifestReader ?? throw new ArgumentNullException(nameof(manifestReader));
    _validateRestrictedDirectory = validateRestrictedDirectory ??
        throw new ArgumentNullException(nameof(validateRestrictedDirectory));
    _handoffLifetime = handoffLifetime ?? TimeSpan.FromHours(24);
    if (_handoffLifetime <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(handoffLifetime));
    }
  }

  public async Task<VsixPlanArtifactStageResult> StageAsync(
      string resourceId,
      string sourcePath,
      string expectedSha256,
      string visualStudioInstanceId,
      CancellationToken cancellationToken)
  {
    SecureStagedArtifact? staged = null;
    var handedOff = false;
    try
    {
      var stageResult = await _stager.StageVerifiedAsync(
          sourcePath,
          expectedSha256,
          SecureArtifactKind.VisualStudioExtension,
          cancellationToken).ConfigureAwait(false);
      staged = stageResult.Artifact;
      if (staged is null)
      {
        return Failure(stageResult.Error, "The VSIX artifact could not be staged.");
      }

      var directory = Path.GetFullPath(staged.DirectoryPath);
      _validateRestrictedDirectory(directory);
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
          visualStudioInstanceId,
          cancellationToken).ConfigureAwait(false);
      if (manifestResult.Manifest is null || manifestResult.Error is not null)
      {
        return Failure(manifestResult.Error, "The staged VSIX manifest is invalid.");
      }

      var ownerToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
      WriteOwnerToken(directory, ownerToken);
      var evidence = new VsixPlanArtifactEvidence(
          1,
          resourceId,
          verification.VerifiedPath,
          verification.Sha256,
          manifestResult.Manifest.Id,
          manifestResult.Manifest.Version,
          manifestResult.Manifest.ManifestPath,
          manifestResult.Manifest.VisualStudioInstanceId,
          manifestResult.Manifest.Targets.ToArray(),
          directory,
          ownerToken);
      var encoded = EncodeEvidence(evidence);
      staged.ReleaseForHandoff();
      handedOff = true;
      RegisterHandoff(resourceId, directory);
      return new VsixPlanArtifactStageResult(encoded, manifestResult.Manifest, null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
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
      string visualStudioInstanceId,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!TryDecodeEvidence(resourceId, stepId, out var evidence))
    {
      return ClaimFailure("The approved VSIX plan evidence is invalid.");
    }

    FileStream? readLock = null;
    ArtifactLease? lease = null;
    var directoryValidated = false;
    var handedOff = false;
    try
    {
      ValidateEvidencePaths(evidence!);
      if (!string.Equals(evidence!.ResourceId, resourceId, StringComparison.Ordinal) ||
          !string.Equals(evidence.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase) ||
          !string.Equals(
              evidence.VisualStudioInstanceId,
              visualStudioInstanceId,
              StringComparison.Ordinal))
      {
        throw new InvalidDataException(
            "The approved VSIX evidence does not match the desired resource.");
      }

      _validateRestrictedDirectory(evidence!.OwnershipDirectory);
      lease = ArtifactLease.Acquire(evidence.OwnershipDirectory);
      ValidateOwnerToken(evidence.OwnershipDirectory, evidence.OwnershipToken);
      directoryValidated = true;
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

      var artifact = new ClaimedVsixPlanArtifact(
          evidence.OwnershipDirectory,
          evidence.ArtifactPath,
          manifestResult.Manifest,
          readLock,
          lease);
      readLock = null;
      lease = null;
      handedOff = true;
      RemoveHandoff(resourceId, evidence.OwnershipDirectory);
      return new VsixPlanArtifactClaimResult(artifact, null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        UnauthorizedAccessException or InvalidDataException or SecurityException)
    {
      return ClaimFailure(null, "The approved VSIX artifact could not be securely claimed.", exception);
    }
    finally
    {
      readLock?.Dispose();
      lease?.Dispose();
      if (directoryValidated && !handedOff)
      {
        RemoveHandoff(resourceId, evidence!.OwnershipDirectory);
        ArtifactCleanupQueue.Shared.DeleteDirectory(evidence!.OwnershipDirectory);
      }
    }
  }

  public Task AbandonAsync(
      string resourceId,
      string stepId,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!TryDecodeEvidence(resourceId, stepId, out var evidence))
    {
      return Task.CompletedTask;
    }

    ArtifactLease? lease = null;
    try
    {
      _validateRestrictedDirectory(evidence!.OwnershipDirectory);
      lease = ArtifactLease.Acquire(evidence.OwnershipDirectory);
      ValidateOwnerToken(evidence.OwnershipDirectory, evidence.OwnershipToken);
      RemoveHandoff(resourceId, evidence.OwnershipDirectory);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
        InvalidDataException or SecurityException)
    {
      return Task.CompletedTask;
    }
    finally
    {
      lease?.Dispose();
    }

    ArtifactCleanupQueue.Shared.DeleteDirectory(evidence!.OwnershipDirectory);
    return Task.CompletedTask;
  }

  internal static bool HasValidStepEvidence(string resourceId, string stepId) =>
      TryDecodeEvidence(resourceId, stepId, out _);

  private void RegisterHandoff(string resourceId, string directory)
  {
    var registration = new HandoffRegistration(directory, new CancellationTokenSource());
    _handoffs.AddOrUpdate(resourceId, registration, (_, existing) =>
    {
      existing.Cancellation.Cancel();
      ArtifactCleanupQueue.Shared.DeleteDirectory(existing.Directory);
      return registration;
    });

    _ = ExpireHandoffAsync(resourceId, registration);
  }

  private async Task ExpireHandoffAsync(string resourceId, HandoffRegistration registration)
  {
    try
    {
      await Task.Delay(_handoffLifetime, registration.Cancellation.Token).ConfigureAwait(false);
      if (_handoffs.TryRemove(
              new KeyValuePair<string, HandoffRegistration>(resourceId, registration)))
      {
        ArtifactCleanupQueue.Shared.DeleteDirectory(registration.Directory);
      }
    }
    catch (OperationCanceledException) when (registration.Cancellation.IsCancellationRequested)
    {
      // Claimed or superseded artifacts are cleaned by their owning lifecycle.
    }
    finally
    {
      registration.Cancellation.Dispose();
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

  private static void WriteOwnerToken(string directory, string token)
  {
    var bytes = Encoding.UTF8.GetBytes(token + "\n");
    using var stream = new FileStream(
        Path.Combine(directory, OwnerFileName),
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 1,
        FileOptions.WriteThrough);
    stream.Write(bytes);
    stream.Flush(flushToDisk: true);
  }

  private static void ValidateOwnerToken(string directory, string expectedToken)
  {
    var path = Path.Combine(directory, OwnerFileName);
    using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 1,
        FileOptions.SequentialScan);
    var expected = Encoding.UTF8.GetBytes(expectedToken + "\n");
    if (File.GetAttributes(stream.SafeFileHandle).HasFlag(FileAttributes.ReparsePoint) ||
        stream.Length != expected.Length)
    {
      throw new SecurityException("The approved VSIX ownership token is invalid.");
    }

    var actual = new byte[expected.Length];
    stream.ReadExactly(actual);
    if (!actual.AsSpan().SequenceEqual(expected))
    {
      throw new SecurityException("The approved VSIX ownership token is invalid.");
    }
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
        evidence.InstallationTargets is null ||
        evidence.InstallationTargets.Any(target =>
            target is null || string.IsNullOrWhiteSpace(target.Id)) ||
        string.IsNullOrWhiteSpace(evidence.OwnershipDirectory) ||
        string.IsNullOrWhiteSpace(evidence.OwnershipToken) ||
        evidence.OwnershipToken.Length != 32 || !evidence.OwnershipToken.All(Uri.IsHexDigit) ||
        !Path.IsPathFullyQualified(evidence.ArtifactPath) ||
        !Path.IsPathFullyQualified(evidence.OwnershipDirectory))
    {
      throw new InvalidDataException("The approved VSIX evidence is incomplete.");
    }

    var directory = Path.GetFullPath(evidence.OwnershipDirectory);
    var path = Path.GetFullPath(evidence.ArtifactPath);
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

  private static string EncodeEvidence(VsixPlanArtifactEvidence evidence)
  {
    var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions);
    return EvidencePrefix + Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
  }

  private static bool TryDecodeEvidence(
      string resourceId,
      string stepId,
      out VsixPlanArtifactEvidence? evidence)
  {
    evidence = null;
    var prefix = $"{resourceId}:install:{EvidencePrefix}";
    if (!stepId.StartsWith(prefix, StringComparison.Ordinal))
    {
      return false;
    }

    try
    {
      var encoded = stepId[prefix.Length..];
      if (encoded.Length == 0 || encoded.Any(character =>
              !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
      {
        return false;
      }

      var padded = encoded.Replace('-', '+').Replace('_', '/');
      padded += new string('=', (4 - padded.Length % 4) % 4);
      evidence = JsonSerializer.Deserialize<VsixPlanArtifactEvidence>(
          Convert.FromBase64String(padded),
          JsonOptions);
      if (evidence is null)
      {
        return false;
      }

      ValidateEvidencePaths(evidence);
      if (!string.Equals(evidence.ResourceId, resourceId, StringComparison.Ordinal))
      {
        return false;
      }

      return string.Equals(prefix + EncodeEvidence(evidence)[EvidencePrefix.Length..], stepId, StringComparison.Ordinal);
    }
    catch (Exception exception) when (exception is ArgumentException or FormatException or
        IOException or InvalidDataException or JsonException or NotSupportedException or
        SecurityException)
    {
      evidence = null;
      return false;
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
      IReadOnlyList<VsixInstallationTarget> InstallationTargets,
      string OwnershipDirectory,
      string OwnershipToken);

  private sealed record HandoffRegistration(
      string Directory,
      CancellationTokenSource Cancellation);
}
