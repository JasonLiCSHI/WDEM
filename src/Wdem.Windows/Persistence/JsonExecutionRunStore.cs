using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Windows.Execution;
using Wdem.Windows.Security;

namespace Wdem.Windows.Persistence;

public sealed class JsonExecutionRunStore : IExecutionRunStore, IApprovedResourceStore
{
  private const int SharingViolationHResult = unchecked((int)0x80070020);
  private const int LockViolationHResult = unchecked((int)0x80070021);
  private const int MaximumLogPageSize = 1000;
  private const int MaximumListRunLockBatchSize = 32;
  private const int MaximumLegacyMigrationCandidateFiles = 4096;
  private const long MaximumLegacyMigrationCandidateBytes = 16 * 1024 * 1024;
  private const int MaximumSnapshotBytes = 16 * 1024 * 1024;
  private const int MaximumSnapshotFormatIndexBytes = 16 * 1024 * 1024;
  private const int MaximumSnapshotFormatAnchorBytes = 64 * 1024;
  private const int MaximumSnapshotFormatCommitmentBytes = 64 * 1024;
  private const int LogIndexRecordSize = sizeof(long) * 3;
  private const string ProtectedPlanApprovalPropertyName = "protectedPlanApproval";
  private const int CurrentSnapshotFormatVersion = 1;
  private static readonly TimeSpan MaximumClaimClockSkew = TimeSpan.FromMinutes(5);
  private static readonly UTF8Encoding Utf8WithoutBom = new(false);
  private readonly object _diagnosticsGate = new();
  private readonly object _logIndexGate = new();
  private readonly List<StructuredError> _diagnostics = [];
  private readonly Dictionary<string, ValidatedLogIndex> _validatedLogIndexes =
      new(StringComparer.OrdinalIgnoreCase);
  private readonly WdemDataPaths _paths;
  private readonly LogRedactor _redactor;
  private readonly JsonSerializerOptions _snapshotJsonOptions;
  private readonly JsonSerializerOptions _logJsonOptions;
  private readonly Func<string, IAsyncDisposable> _recoveryLockOpener;
  private readonly IApprovedResourceProtector _approvedResourceProtector;
  private readonly TimeProvider _timeProvider;

  public JsonExecutionRunStore(
      WdemDataPaths paths,
      LogRedactor redactor,
      TimeProvider? timeProvider = null)
      : this(
          paths,
          redactor,
          OpenRecoveryLock,
          new CurrentUserApprovedResourceProtector(),
          timeProvider)
  {
  }

  internal JsonExecutionRunStore(
      WdemDataPaths paths,
      LogRedactor redactor,
      IApprovedResourceProtector approvedResourceProtector,
      TimeProvider? timeProvider = null)
      : this(paths, redactor, OpenRecoveryLock, approvedResourceProtector, timeProvider)
  {
  }

  internal JsonExecutionRunStore(
      WdemDataPaths paths,
      LogRedactor redactor,
      Func<string, IAsyncDisposable> recoveryLockOpener,
      TimeProvider? timeProvider = null)
      : this(
          paths,
          redactor,
          recoveryLockOpener,
          new CurrentUserApprovedResourceProtector(),
          timeProvider)
  {
  }

  internal JsonExecutionRunStore(
      WdemDataPaths paths,
      LogRedactor redactor,
      Func<string, IAsyncDisposable> recoveryLockOpener,
      IApprovedResourceProtector approvedResourceProtector,
      TimeProvider? timeProvider = null)
  {
    _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    _recoveryLockOpener = recoveryLockOpener ??
        throw new ArgumentNullException(nameof(recoveryLockOpener));
    _approvedResourceProtector = approvedResourceProtector ??
        throw new ArgumentNullException(nameof(approvedResourceProtector));
    _snapshotJsonOptions = CreateJsonOptions(writeIndented: true);
    _logJsonOptions = CreateJsonOptions(writeIndented: false);
    _timeProvider = timeProvider ?? TimeProvider.System;
  }

  public IReadOnlyList<StructuredError> Diagnostics
  {
    get
    {
      lock (_diagnosticsGate)
      {
        return _diagnostics.ToArray();
      }
    }
  }

  public string SnapshotPath(Guid runId) =>
      Path.Combine(_paths.RunsDirectory, $"{runId:D}.json");

  public string LogPath(Guid runId) =>
      Path.Combine(_paths.RunsDirectory, $"{runId:D}.ndjson");

  public string LogIndexPath(Guid runId) => LogPath(runId) + ".index";

  public string ApprovedResourcesPath(Guid runId) =>
      Path.Combine(_paths.RunsDirectory, $"{runId:D}.approved.json");

  private string SnapshotFormatCommitmentPath(Guid runId) =>
      Path.Combine(_paths.RunsDirectory, $"{runId:D}.snapshot-format");

  private string SnapshotFormatIndexPath() =>
      Path.Combine(_paths.RunsDirectory, ".snapshot-format-index");

  private string SnapshotFormatAnchorPath() =>
      Path.Combine(_paths.Root, ".snapshot-format-anchor");

  public Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken) =>
      CreateAsync(run, [], cancellationToken);

  public async Task CreateAsync(
      ExecutionRun run,
      IReadOnlyList<ApprovedResourceSeal> approvedResources,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(run);
    ArgumentNullException.ThrowIfNull(approvedResources);
    ValidateRunForPersistence(run);
    if (run.PlanApproval is { } approval &&
        !FixedEquals(approval.InitialPlanFingerprint, run.Plan!.Fingerprint))
    {
      throw new ArgumentException(
          "The initial plan approval must match the plan being created.",
          nameof(run));
    }

    cancellationToken.ThrowIfCancellationRequested();
    await using var runLock = await AcquireRunLockAsync(run.RunId, cancellationToken)
        .ConfigureAwait(false);
    var path = SnapshotPath(run.RunId);
    if (File.Exists(path))
    {
      throw new InvalidOperationException($"Execution run '{run.RunId:D}' already exists.");
    }

    var approvedPath = ApprovedResourcesPath(run.RunId);
    var approvedWritten = false;
    try
    {
      approvedWritten = await WriteApprovedResourcesAsync(
          run,
          approvedResources,
          cancellationToken)
          .ConfigureAwait(false);
      await PersistSnapshotWithCommitmentAsync(
          run,
          SerializeSnapshot(run),
          cancellationToken)
          .ConfigureAwait(false);
    }
    catch
    {
      if (approvedWritten && File.Exists(approvedPath))
      {
        File.Delete(approvedPath);
      }

      throw;
    }
  }

  public async Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    using var existingDirectoryScope = AcquireExistingRunsDirectoryScope();
    if (existingDirectoryScope is null)
    {
      return null;
    }

    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        runId,
        cancellationToken)
        .ConfigureAwait(false);
    var run = await ReadSnapshotAsync(runId, cancellationToken).ConfigureAwait(false);
    if (run?.State == ExecutionState.Completed)
    {
      DeleteApprovedResources(runId);
    }

    return run;
  }

  public async Task SealApprovedResourceAsync(
      Guid runId,
      ApprovedResourceSeal approvedResource,
      CancellationToken cancellationToken)
  {
    if (runId == Guid.Empty)
    {
      throw new ArgumentException("An execution run identifier is required.", nameof(runId));
    }

    ArgumentNullException.ThrowIfNull(approvedResource);
    cancellationToken.ThrowIfCancellationRequested();
    ValidateDefinition(approvedResource.Definition);
    ValidateResourcePlan(approvedResource.Plan);
    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        runId,
        cancellationToken).ConfigureAwait(false);
    var run = await ReadSnapshotAsync(runId, cancellationToken).ConfigureAwait(false) ??
        throw new KeyNotFoundException($"Execution run '{runId:D}' does not exist.");
    if (run.Mode != RunMode.Apply || run.State == ExecutionState.Completed || run.Plan is null)
    {
      throw new InvalidOperationException(
          "Approved resources can only be added to an active apply run.");
    }

    var expectedElevated = run.Plan.Resources.Where(resource =>
        resource.Status == PlannedResourceStatus.Ready &&
        resource.RequiresElevation &&
        resource.ResourcePlan.IsExecutable).ToArray();
    var expected = expectedElevated.Where(resource =>
        string.Equals(
            resource.Definition.Id,
            approvedResource.Definition.Id,
            StringComparison.OrdinalIgnoreCase)).ToArray();
    var definitionFingerprint = ResourceDefinitionFingerprint.Create(
        approvedResource.Definition);
    var plan = approvedResource.Plan;
    if (expected.Length != 1 ||
        !FixedEquals(expected[0].ResourcePlan.DesiredStateFingerprint, definitionFingerprint) ||
        !plan.IsExecutable ||
        !string.Equals(plan.ResourceId, approvedResource.Definition.Id, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(plan.ResourceType, approvedResource.Definition.Type, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(plan.ProviderName, approvedResource.Definition.Provider, StringComparison.OrdinalIgnoreCase) ||
        !FixedEquals(plan.DesiredStateFingerprint, definitionFingerprint) ||
        !FixedEquals(
            ApprovedResourceFingerprint.Create(approvedResource.Definition, plan),
            ApprovedResourceFingerprint.Create(
                approvedResource.Definition,
                expected[0].ResourcePlan)) ||
        !plan.Steps.Any(step =>
            step.Action != PlanAction.None &&
            step.PrivilegeRequirement == PrivilegeRequirement.Administrator))
    {
      throw new InvalidOperationException(
          "The deferred approved resource does not match the original execution plan.");
    }

    var approvedPath = ApprovedResourcesPath(runId);
    try
    {
      var entries = new List<ProtectedApprovedResource>();
      var expectedExisting = expectedElevated.Where(resource => !string.Equals(
          resource.Definition.Id,
          approvedResource.Definition.Id,
          StringComparison.OrdinalIgnoreCase)).ToArray();
      if (File.Exists(approvedPath))
      {
        await using var stream = new FileStream(
            approvedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var existing = await JsonSerializer.DeserializeAsync<ApprovedResourceEnvelope>(
            stream,
            _snapshotJsonOptions,
            cancellationToken).ConfigureAwait(false);
        ValidateApprovedResourceEnvelope(runId, existing, expectedExisting);
        entries.AddRange(existing!.Resources);
      }
      else if (expectedExisting.Length > 0)
      {
        throw new InvalidOperationException(
            "The approved resource snapshot is missing existing elevated resources.");
      }

      if (entries.Any(entry => string.Equals(
              entry.ResourceId,
              approvedResource.Definition.Id,
              StringComparison.OrdinalIgnoreCase)))
      {
        throw new InvalidOperationException(
            "The deferred resource already has an approved execution seal.");
      }

      var fingerprint = ApprovedResourceFingerprint.Create(
          approvedResource.Definition,
          plan);
      var approved = new ApprovedResource(
          approvedResource.Definition,
          plan,
          fingerprint);
      var plaintext = JsonSerializer.SerializeToUtf8Bytes(approved, _snapshotJsonOptions);
      var protectedData = _approvedResourceProtector.Protect(
          plaintext,
          ApprovedResourceEntropy(
              runId,
              approvedResource.Definition.Id,
              fingerprint));
      entries.Add(new ProtectedApprovedResource(
          approvedResource.Definition.Id,
          fingerprint,
          Convert.ToBase64String(protectedData)));
      var bytes = JsonSerializer.SerializeToUtf8Bytes(
          new ApprovedResourceEnvelope(runId, entries),
          _snapshotJsonOptions);
      await WriteBytesAtomicallyAsync(approvedPath, bytes, cancellationToken)
          .ConfigureAwait(false);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      throw ApprovedResourceStorageFailure(
          runId,
          approvedResource.Definition.Id,
          exception,
          isRetryable: true);
    }
    catch (Exception exception) when (
        exception is CryptographicException
            or JsonException
            or FormatException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException)
    {
      throw ApprovedResourceStorageFailure(
          runId,
          approvedResource.Definition.Id,
          exception,
          isRetryable: false);
    }
  }

  public async Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(
      CancellationToken cancellationToken)
  {
    var runs = await ListAsync(cancellationToken).ConfigureAwait(false);
    return runs.Where(run => run.State != ExecutionState.Completed).ToArray();
  }

  public async Task<IReadOnlyList<ExecutionRun>> ListAsync(
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    using var directoryScope = AcquireExistingRunsDirectoryScope();
    if (directoryScope is null)
    {
      return [];
    }

    var runIds = Directory.EnumerateFiles(_paths.RunsDirectory, "*.json")
        .Select(Path.GetFileNameWithoutExtension)
        .Select(name => Guid.TryParse(name, out var runId) ? runId : (Guid?)null)
        .Where(runId => runId.HasValue)
        .Select(runId => runId!.Value)
        .Distinct()
        .OrderBy(runId => runId)
        .ToArray();
    var runs = new List<ExecutionRun>();
    if (runIds.Length == 0)
    {
      await using var stateLock = await AcquireSnapshotFormatStateLockAsync(cancellationToken)
          .ConfigureAwait(false);
      await ReadAuthenticatedSnapshotFormatIndexAsync(cancellationToken)
          .ConfigureAwait(false);
      return runs;
    }

    foreach (Guid[] batch in runIds.Chunk(MaximumListRunLockBatchSize))
    {
      var runLocks = new List<FileStream>(batch.Length);
      try
      {
        foreach (var runId in batch)
        {
          runLocks.Add(await AcquireRunLockFileAsync(
              runId,
              cancellationToken,
              deleteOnCloseWhenSnapshotMissing: true).ConfigureAwait(false));
        }

        await using var stateLock = await AcquireSnapshotFormatStateLockAsync(cancellationToken)
            .ConfigureAwait(false);
        var batchState = new BatchSnapshotFormatState(
            await ReadAuthenticatedSnapshotFormatIndexAsync(cancellationToken)
                .ConfigureAwait(false));
        foreach (var runId in batch)
        {
          cancellationToken.ThrowIfCancellationRequested();
          var run = await ReadSnapshotAsync(runId, cancellationToken, batchState)
              .ConfigureAwait(false);
          if (run?.State == ExecutionState.Completed)
          {
            DeleteApprovedResources(runId);
          }

          if (run is not null)
          {
            runs.Add(run);
          }
        }

        if (batchState.IsChanged)
        {
          await PersistSnapshotFormatIndexAsync(batchState.Index, cancellationToken)
              .ConfigureAwait(false);
        }
      }
      finally
      {
        for (int index = runLocks.Count - 1; index >= 0; index--)
        {
          await runLocks[index].DisposeAsync().ConfigureAwait(false);
        }
      }
    }

    return runs;
  }

  public Task<IAsyncDisposable?> TryAcquireRecoveryOperationAsync(
      Guid runId,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var directoryScope = AcquireRunsDirectoryScope();
    try
    {
      var lockPath = Path.Combine(_paths.RunsDirectory, $"{runId:D}.recovery.lock");
      var lease = new OwnedAsyncLease(_recoveryLockOpener(lockPath), directoryScope);
      return Task.FromResult<IAsyncDisposable?>(lease);
    }
    catch (IOException exception) when (IsLockContention(exception))
    {
      directoryScope.Dispose();
      return Task.FromResult<IAsyncDisposable?>(null);
    }
    catch (IOException exception)
    {
      directoryScope.Dispose();
      var diagnostic = new StructuredError(
          WdemErrorCode.DetectionError,
          "Recovery operation lock could not be acquired.",
          $"Run '{runId:D}' recovery lock failed: {exception.Message}")
      {
        IsRetryable = false
      };
      lock (_diagnosticsGate)
      {
        _diagnostics.Add(diagnostic);
      }

      throw;
    }
    catch
    {
      directoryScope.Dispose();
      throw;
    }
  }

  private static bool IsLockContention(IOException exception) =>
      exception.HResult is SharingViolationHResult or LockViolationHResult;

  private static IAsyncDisposable OpenRecoveryLock(string lockPath) =>
      SecureBoundedFileReader.OpenLockFile(
          lockPath,
          Path.GetDirectoryName(lockPath)!,
          deleteOnClose: false,
          "recovery operation lock");

  public async Task<ExecutionRun> SaveAsync(
      ExecutionRun run,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(run);
    ValidateRunForPersistence(run);
    if (run.Revision == long.MaxValue - 1)
    {
      throw new ArgumentException(
          "The execution run revision cannot be incremented to a supported revision.",
          nameof(run));
    }

    cancellationToken.ThrowIfCancellationRequested();
    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        run.RunId,
        cancellationToken)
        .ConfigureAwait(false);
    var path = SnapshotPath(run.RunId);
    EnsureRunExists(run.RunId, path);
    var current = await ReadSnapshotAsync(run.RunId, cancellationToken).ConfigureAwait(false) ??
        throw new KeyNotFoundException($"Execution run '{run.RunId:D}' does not exist.");
    if (current.Revision != run.Revision ||
        current.RecoveryClaimId != run.RecoveryClaimId)
    {
      throw new InvalidOperationException(
          $"Execution run '{run.RunId:D}' revision or recovery claim is stale.");
    }

    EnsurePlanApprovalUnchanged(current.PlanApproval, run.PlanApproval);
    ValidatePersistenceTransition(current, run);

    var saved = run with { Revision = checked(run.Revision + 1) };
    ValidateRunForPersistence(saved);
    await PersistSnapshotWithCommitmentAsync(
        saved,
        SerializeSnapshot(saved),
        cancellationToken)
        .ConfigureAwait(false);
    if (saved.State == ExecutionState.Completed)
    {
      DeleteApprovedResources(saved.RunId);
    }

    return saved;
  }

  public async Task<bool> TrySaveAsync(
      ExecutionRun run,
      long expectedRevision,
      Guid? expectedRecoveryClaimId,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(run);
    if (expectedRevision < 0 ||
        expectedRevision == long.MaxValue ||
        run.Revision != expectedRevision + 1)
    {
      throw new ArgumentOutOfRangeException(
          nameof(expectedRevision),
          expectedRevision,
          "The replacement run revision must immediately follow the expected revision.");
    }

    ValidateRunForPersistence(run);
    cancellationToken.ThrowIfCancellationRequested();
    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        run.RunId,
        cancellationToken)
        .ConfigureAwait(false);
    var path = SnapshotPath(run.RunId);
    EnsureRunExists(run.RunId, path);
    var current = await ReadSnapshotAsync(run.RunId, cancellationToken).ConfigureAwait(false) ??
        throw new KeyNotFoundException($"Execution run '{run.RunId:D}' does not exist.");
    if (current.Revision != expectedRevision ||
        current.RecoveryClaimId != expectedRecoveryClaimId)
    {
      return false;
    }

    EnsurePlanApprovalUnchanged(current.PlanApproval, run.PlanApproval);
    ValidatePersistenceTransition(current, run);

    await PersistSnapshotWithCommitmentAsync(
        run,
        SerializeSnapshot(run),
        cancellationToken)
        .ConfigureAwait(false);
    if (run.State == ExecutionState.Completed)
    {
      DeleteApprovedResources(run.RunId);
    }

    return true;
  }

  public async Task<ApprovedResourceClaim?> ClaimApprovedResourceAsync(
      Guid runId,
      string resourceId,
      string planFingerprint,
      CancellationToken cancellationToken)
  {
    if (runId == Guid.Empty)
    {
      throw new ArgumentException("An execution run identifier is required.", nameof(runId));
    }

    ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
    ArgumentException.ThrowIfNullOrWhiteSpace(planFingerprint);
    cancellationToken.ThrowIfCancellationRequested();
    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        runId,
        cancellationToken).ConfigureAwait(false);
    var run = await ReadSnapshotAsync(runId, cancellationToken).ConfigureAwait(false);
    if (run is null || run.Mode != RunMode.Apply || run.State != ExecutionState.Running ||
        run.Plan is null || !run.Plan.IsExecutable)
    {
      return null;
    }

    var path = ApprovedResourcesPath(runId);
    if (!File.Exists(path))
    {
      return null;
    }

    try
    {
      ApprovedResourceEnvelope? envelope;
      await using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       4096,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
      {
        envelope = await JsonSerializer.DeserializeAsync<ApprovedResourceEnvelope>(
            stream,
            _snapshotJsonOptions,
            cancellationToken).ConfigureAwait(false);
      }

      var expectedElevated = run.Plan.Resources.Where(resource =>
          resource.Status == PlannedResourceStatus.Ready &&
          resource.RequiresElevation &&
          resource.ResourcePlan.IsExecutable).ToArray();
      var approvedById = ValidateApprovedResourceEnvelope(
          runId,
          envelope,
          expectedElevated);

      var plannedMatches = run.Plan.Resources.Where(resource => string.Equals(
          resource.Definition.Id,
          resourceId,
          StringComparison.OrdinalIgnoreCase)).ToArray();
      if (plannedMatches.Length != 1)
      {
        return null;
      }

      var planned = plannedMatches[0];
      if (!run.ResourceResults.TryGetValue(planned.Definition.Id, out var persistedResult) ||
          persistedResult.State != ExecutionState.Running ||
          persistedResult.Outcome is not null ||
          planned.Status != PlannedResourceStatus.Ready ||
          !planned.RequiresElevation ||
          !planned.ResourcePlan.IsExecutable ||
          !planned.ResourcePlan.RequiresApply ||
          !planned.ResourcePlan.Steps.Any(step =>
              step.Action != PlanAction.None &&
              step.PrivilegeRequirement == PrivilegeRequirement.Administrator))
      {
        return null;
      }

      var entryIndexes = envelope!.Resources
          .Select((entry, index) => (entry, index))
          .Where(candidate => string.Equals(
              candidate.entry.ResourceId,
              resourceId,
              StringComparison.OrdinalIgnoreCase))
          .ToArray();
      if (entryIndexes.Length != 1)
      {
        throw new InvalidOperationException(
            "The approved resource snapshot does not contain exactly one matching resource.");
      }

      var (entry, entryIndex) = entryIndexes[0];
      var approved = approvedById[entry.ResourceId];
      if ((entry.ClaimedPlanFingerprints ?? []).Contains(
              planFingerprint,
              StringComparer.OrdinalIgnoreCase))
      {
        return null;
      }

      if (!approved.Definition.Dependencies.All(dependency =>
              run.ResourceResults.TryGetValue(dependency, out var dependencyResult) &&
              dependencyResult.State == ExecutionState.Completed &&
              dependencyResult.Outcome is ExecutionOutcome.Succeeded or
                  ExecutionOutcome.NotRequired))
      {
        throw new InvalidOperationException(
            "The protected approved resource does not match the persisted execution plan.");
      }

      var segments = PrivilegePlanSegments.Split(approved.Plan)
          .Where(segment => segment.Steps.Any(step =>
              step.Action != PlanAction.None &&
              step.PrivilegeRequirement == PrivilegeRequirement.Administrator))
          .Where(segment => FixedEquals(
              planFingerprint,
              ApprovedResourceFingerprint.Create(approved.Definition, segment)))
          .ToArray();
      if (segments.Length != 1)
      {
        throw new InvalidOperationException(
            "The protected approved resource does not contain exactly one matching administrator segment.");
      }

      var entries = envelope.Resources.ToArray();
      entries[entryIndex] = entry with
      {
        ClaimedPlanFingerprints =
        [
          .. entry.ClaimedPlanFingerprints ?? [],
          planFingerprint
        ]
      };
      await WriteBytesAtomicallyAsync(
          path,
          JsonSerializer.SerializeToUtf8Bytes(
              envelope with { Resources = entries },
              _snapshotJsonOptions),
          cancellationToken).ConfigureAwait(false);
      return new ApprovedResourceClaim(
          approved.Definition,
          approved.Plan,
          segments[0],
          approved.Fingerprint);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      throw ApprovedResourceStorageFailure(
          runId,
          resourceId,
          exception,
          isRetryable: true);
    }
    catch (Exception exception) when (
        exception is CryptographicException
            or JsonException
            or FormatException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException)
    {
      throw ApprovedResourceStorageFailure(
          runId,
          resourceId,
          exception,
          isRetryable: false);
    }
  }

  public async Task<ApprovedResource?> GetApprovedResourceAsync(
      Guid runId,
      string resourceId,
      CancellationToken cancellationToken)
  {
    if (runId == Guid.Empty)
    {
      throw new ArgumentException("An execution run identifier is required.", nameof(runId));
    }

    ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
    cancellationToken.ThrowIfCancellationRequested();
    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        runId,
        cancellationToken).ConfigureAwait(false);
    var run = await ReadSnapshotAsync(runId, cancellationToken).ConfigureAwait(false);
    if (run is null)
    {
      return null;
    }

    var path = ApprovedResourcesPath(runId);
    if (!File.Exists(path))
    {
      return null;
    }

    try
    {
      await using var stream = new FileStream(
          path,
          FileMode.Open,
          FileAccess.Read,
          FileShare.Read,
          4096,
          FileOptions.Asynchronous | FileOptions.SequentialScan);
      var envelope = await JsonSerializer.DeserializeAsync<ApprovedResourceEnvelope>(
          stream,
          _snapshotJsonOptions,
          cancellationToken).ConfigureAwait(false);
      var expectedElevated = run.Plan?.Resources.Where(resource =>
          resource.Status == PlannedResourceStatus.Ready &&
          resource.RequiresElevation &&
          resource.ResourcePlan.IsExecutable).ToArray() ?? [];
      var approvedById = ValidateApprovedResourceEnvelope(
          runId,
          envelope,
          expectedElevated);
      if (!approvedById.TryGetValue(resourceId, out var approved))
      {
        return null;
      }

      return approved;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      throw ApprovedResourceStorageFailure(
          runId,
          resourceId,
          exception,
          isRetryable: true);
    }
    catch (Exception exception) when (
        exception is CryptographicException
            or JsonException
            or FormatException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException)
    {
      throw ApprovedResourceStorageFailure(
          runId,
          resourceId,
          exception,
          isRetryable: false);
    }
  }

  public async Task AppendLogAsync(
      Guid runId,
      RunLogEntry entry,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(entry);
    ValidateLogEntryForPersistence(entry);
    cancellationToken.ThrowIfCancellationRequested();
    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        runId,
        cancellationToken)
        .ConfigureAwait(false);
    EnsureRunExists(runId, SnapshotPath(runId));
    var logPath = LogPath(runId);
    var index = await ReconcileLogIndexAsync(logPath, cancellationToken)
        .ConfigureAwait(false);
    if (entry.Sequence <= index.LastSequence)
    {
      throw new InvalidOperationException(
          $"Log sequence {entry.Sequence} must be greater than {index.LastSequence} for run '{runId:D}'.");
    }

    var persistedEntry = _redactor.Redact(entry);
    var line = JsonSerializer.Serialize(persistedEntry, _logJsonOptions) + "\n";
    var bytes = Utf8WithoutBom.GetBytes(line);
    await using var stream = SecureBoundedFileReader.OpenMutableFile(
        logPath,
        _paths.RunsDirectory,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        "run log");
    stream.Position = stream.Length;
    var startOffset = stream.Length;
    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    stream.Flush(flushToDisk: true);
    await AppendLogIndexRecordAsync(
        logPath,
        new LogIndexRecord(entry.Sequence, startOffset, stream.Length),
        cancellationToken).ConfigureAwait(false);
    RememberValidatedLogIndex(
        logPath,
        new LogIndexState(index.Count + 1, entry.Sequence, stream.Length));
  }

  public async Task<IReadOnlyList<RunLogEntry>> ReadLogPageAsync(
      Guid runId,
      long afterSequence,
      int take,
      CancellationToken cancellationToken)
  {
    if (afterSequence < 0)
    {
      throw new ArgumentOutOfRangeException(nameof(afterSequence));
    }

    if (take is < 1 or > MaximumLogPageSize)
    {
      throw new ArgumentOutOfRangeException(nameof(take));
    }

    cancellationToken.ThrowIfCancellationRequested();
    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        runId,
        cancellationToken)
        .ConfigureAwait(false);
    EnsureRunExists(runId, SnapshotPath(runId));
    var logPath = LogPath(runId);
    if (!File.Exists(logPath))
    {
      return [];
    }

    var index = await ReconcileLogIndexAsync(logPath, cancellationToken)
        .ConfigureAwait(false);
    return await ReadIndexedLogPageAsync(
        logPath,
        index.Count,
        afterSequence,
        take,
        cancellationToken).ConfigureAwait(false);
  }

  private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
  {
    var options = new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      PropertyNameCaseInsensitive = true,
      RespectNullableAnnotations = true,
      RespectRequiredConstructorParameters = true,
      WriteIndented = writeIndented
    };
    options.Converters.Add(new JsonStringEnumConverter(
        namingPolicy: null,
        allowIntegerValues: false));
    options.Converters.Add(new ReadOnlyStringSetJsonConverter());
    return options;
  }

  private SecureDirectoryScope AcquireRootDirectoryScope()
  {
    var scope = new SecureDirectoryScope();
    try
    {
      var rootParent = Path.GetDirectoryName(Path.GetFullPath(_paths.Root)) ??
          throw new InvalidOperationException("The WDEM root directory must have a parent.");
      Directory.CreateDirectory(rootParent);
      scope.Add(SecureBoundedFileReader.OpenDirectoryLease(
          rootParent,
          "local application data"));
      Directory.CreateDirectory(_paths.Root);
      scope.Add(SecureBoundedFileReader.OpenDirectoryLease(_paths.Root, "WDEM root"));
      return scope;
    }
    catch
    {
      scope.Dispose();
      throw;
    }
  }

  private SecureDirectoryScope? AcquireExistingRootDirectoryScope()
  {
    var scope = new SecureDirectoryScope();
    try
    {
      var rootParent = Path.GetDirectoryName(Path.GetFullPath(_paths.Root)) ??
          throw new InvalidOperationException("The WDEM root directory must have a parent.");
      var rootParentLease = SecureBoundedFileReader.TryOpenDirectoryLease(
          rootParent,
          "local application data");
      if (rootParentLease is null)
      {
        scope.Dispose();
        return null;
      }

      scope.Add(rootParentLease);
      var rootLease = SecureBoundedFileReader.TryOpenDirectoryLease(_paths.Root, "WDEM root");
      if (rootLease is null)
      {
        scope.Dispose();
        return null;
      }

      scope.Add(rootLease);
      return scope;
    }
    catch
    {
      scope.Dispose();
      throw;
    }
  }

  private SecureDirectoryScope? AcquireExistingRunsDirectoryScope()
  {
    var scope = AcquireExistingRootDirectoryScope();
    if (scope is null)
    {
      return null;
    }

    try
    {
      var runsLease = SecureBoundedFileReader.TryOpenDirectoryLease(
          _paths.RunsDirectory,
          "execution runs");
      if (runsLease is null)
      {
        scope.Dispose();
        return null;
      }

      scope.Add(runsLease);
      return scope;
    }
    catch
    {
      scope.Dispose();
      throw;
    }
  }

  private SecureDirectoryScope AcquireRunsDirectoryScope()
  {
    var scope = AcquireRootDirectoryScope();
    try
    {
      Directory.CreateDirectory(_paths.RunsDirectory);
      scope.Add(SecureBoundedFileReader.OpenDirectoryLease(
          _paths.RunsDirectory,
          "execution runs"));
      return scope;
    }
    catch
    {
      scope.Dispose();
      throw;
    }
  }

  private async Task<OwnedAsyncLease> AcquireRunLockAsync(
      Guid runId,
      CancellationToken cancellationToken,
      bool deleteOnCloseWhenSnapshotMissing = false)
  {
    var directoryScope = AcquireRunsDirectoryScope();
    try
    {
      var stream = await AcquireRunLockFileAsync(
          runId,
          cancellationToken,
          deleteOnCloseWhenSnapshotMissing).ConfigureAwait(false);
      return new OwnedAsyncLease(stream, directoryScope);
    }
    catch
    {
      directoryScope.Dispose();
      throw;
    }
  }

  private async Task<FileStream> AcquireRunLockFileAsync(
      Guid runId,
      CancellationToken cancellationToken,
      bool deleteOnCloseWhenSnapshotMissing)
  {
    var lockPath = Path.Combine(_paths.RunsDirectory, $"{runId:D}.lock");
    bool deleteOnClose = deleteOnCloseWhenSnapshotMissing &&
        !File.Exists(SnapshotPath(runId));
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        return SecureBoundedFileReader.OpenLockFile(
            lockPath,
            _paths.RunsDirectory,
            deleteOnClose,
            "execution run lock");
      }
      catch (IOException exception) when (IsLockContention(exception))
      {
        await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
            .ConfigureAwait(false);
      }
    }
  }

  private Task<OwnedAsyncLease> AcquireRunLockForExistingSnapshotAsync(
      Guid runId,
      CancellationToken cancellationToken) =>
      AcquireRunLockAsync(
          runId,
          cancellationToken,
          deleteOnCloseWhenSnapshotMissing: true);

  private async Task<OwnedAsyncLease> AcquireSnapshotFormatStateLockAsync(
      CancellationToken cancellationToken)
  {
    var directoryScope = AcquireRootDirectoryScope();
    try
    {
      var lockPath = SnapshotFormatAnchorPath() + ".lock";
      while (true)
      {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
          var stream = SecureBoundedFileReader.OpenLockFile(
              lockPath,
              _paths.Root,
              deleteOnClose: false,
              "snapshot format state lock");
          return new OwnedAsyncLease(stream, directoryScope);
        }
        catch (IOException exception) when (IsLockContention(exception))
        {
          await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
              .ConfigureAwait(false);
        }
      }
    }
    catch
    {
      directoryScope.Dispose();
      throw;
    }
  }

  private async Task<ExecutionRun?> ReadSnapshotAsync(
      Guid runId,
      CancellationToken cancellationToken,
      BatchSnapshotFormatState? batchState = null)
  {
    var path = SnapshotPath(runId);
    if (!File.Exists(path))
    {
      return null;
    }

    byte[] snapshotBytes;
    try
    {
      snapshotBytes = await SecureBoundedFileReader.ReadAsync(
          path,
          _paths.RunsDirectory,
          MaximumSnapshotBytes,
          "snapshot",
          cancellationToken).ConfigureAwait(false);
    }
    catch (Exception exception) when (IsSnapshotCorruptionException(exception))
    {
      PreserveCorruptedSnapshot(runId, path, exception);
      return null;
    }

    if (batchState is not null)
    {
      var batchResult = TryRestoreSnapshot(
          runId,
          path,
          snapshotBytes,
          batchState.Index);
      batchState.IsChanged |= batchResult.IndexChanged;
      return batchResult.Run;
    }

    await using var stateLock = await AcquireSnapshotFormatStateLockAsync(cancellationToken)
        .ConfigureAwait(false);
    var index = await ReadAuthenticatedSnapshotFormatIndexAsync(cancellationToken)
        .ConfigureAwait(false);
    var result = TryRestoreSnapshot(runId, path, snapshotBytes, index);
    if (result.IndexChanged)
    {
      await PersistSnapshotFormatIndexAsync(index, cancellationToken).ConfigureAwait(false);
    }

    return result.Run;
  }

  private (ExecutionRun? Run, bool IndexChanged) TryRestoreSnapshot(
      Guid runId,
      string path,
      byte[] snapshotBytes,
      SnapshotFormatIndex index)
  {
    bool indexChanged = false;
    try
    {
      var formatCommitment = ReconcileSnapshotFormatCommitment(
          runId,
          snapshotBytes,
          index,
          out indexChanged);
      var document = JsonNode.Parse(snapshotBytes) as JsonObject ??
          throw new JsonException("The execution run snapshot must be a JSON object.");
      var protectedPlanApproval = document[ProtectedPlanApprovalPropertyName]?.DeepClone();
      document.Remove(ProtectedPlanApprovalPropertyName);
      var run = document.Deserialize<ExecutionRun>(_snapshotJsonOptions);
      if (run is null || run.RunId != runId)
      {
        throw new JsonException("The execution run snapshot has no matching run identifier.");
      }

      ValidateRun(run);
      if (formatCommitment.ExpectedRevision is { } expectedRevision &&
          run.Revision != expectedRevision)
      {
        throw new InvalidOperationException(
            "The execution run snapshot revision does not match its format commitment.");
      }

      if (formatCommitment.IsCurrent &&
          (run.Plan is null ||
           run.PlanApproval is null ||
           protectedPlanApproval is null))
      {
        throw new InvalidOperationException(
            "The current execution run snapshot must contain a plan, approval, and protected " +
            "plan approval.");
      }

      if (!formatCommitment.IsCurrent &&
          run.PlanApproval is not null &&
          !formatCommitment.IsLegacyMigrationCandidate)
      {
        throw new InvalidOperationException(
            "The legacy approved execution run was not authenticated during migration.");
      }

      if (protectedPlanApproval is not null)
      {
        var envelope = protectedPlanApproval.Deserialize<ProtectedPlanApproval>(
            _snapshotJsonOptions) ??
            throw new JsonException("The protected plan approval is null.");
        run = RestoreProtectedPlanApproval(run, envelope);
        ValidateRun(run);
      }

      return (SnapshotRestoredRun(run), indexChanged);
    }
    catch (Exception exception) when (IsSnapshotCorruptionException(exception))
    {
      PreserveCorruptedSnapshot(runId, path, exception);
      return (null, indexChanged);
    }
  }

  private static bool IsSnapshotCorruptionException(Exception exception) =>
      exception is JsonException
          or NotSupportedException
          or CryptographicException
          or FormatException
          or InvalidDataException
          or InvalidOperationException
          or ArgumentException;

  private void PreserveCorruptedSnapshot(Guid runId, string path, Exception exception)
  {
    var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'");
    var preservedPath = $"{path}.corrupted.{timestamp}.{Guid.NewGuid():N}";
    string preservation;
    try
    {
      File.Move(path, preservedPath);
      preservation = $"Preserved as '{preservedPath}'.";
    }
    catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException)
    {
      preservation = $"Could not preserve the corrupt snapshot: {moveException.Message}";
    }

    var diagnostic = new StructuredError(
        WdemErrorCode.DetectionError,
        "Execution run snapshot is malformed.",
        $"Run '{runId:D}' could not be loaded: {exception.Message} {preservation}")
    {
      IsRetryable = false
    };
    lock (_diagnosticsGate)
    {
      _diagnostics.Add(diagnostic);
    }
  }

  private async Task<bool> WriteApprovedResourcesAsync(
      ExecutionRun run,
      IReadOnlyList<ApprovedResourceSeal> approvedResources,
      CancellationToken cancellationToken)
  {
    if (run.State == ExecutionState.Completed ||
        run.Mode != RunMode.Apply ||
        run.Plan is null ||
        approvedResources.Count == 0)
    {
      return false;
    }

    var expected = run.Plan.Resources.Where(resource =>
        resource.Status == PlannedResourceStatus.Ready &&
        resource.RequiresElevation &&
        resource.ResourcePlan.IsExecutable).ToArray();
    if (expected.Length != approvedResources.Count)
    {
      throw new InvalidOperationException(
          "The approved resource seals do not match the elevated execution plan.");
    }

    var entries = new List<ProtectedApprovedResource>();
    var sealedResourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var approvedResource in approvedResources)
    {
      var matches = expected.Where(resource => string.Equals(
          resource.Definition.Id,
          approvedResource.Definition.Id,
          StringComparison.OrdinalIgnoreCase)).ToArray();
      if (!sealedResourceIds.Add(approvedResource.Definition.Id) ||
          matches.Length != 1 ||
          !FixedEquals(
              ApprovedResourceFingerprint.Create(
                  approvedResource.Definition,
                  approvedResource.Plan),
              ApprovedResourceFingerprint.Create(
                  approvedResource.Definition,
                  matches[0].ResourcePlan)))
      {
        throw new InvalidOperationException(
            "An approved resource seal does not match the elevated execution plan.");
      }

      var fingerprint = ApprovedResourceFingerprint.Create(
          approvedResource.Definition,
          approvedResource.Plan);
      var approved = new ApprovedResource(
          approvedResource.Definition,
          approvedResource.Plan,
          fingerprint);
      var plaintext = JsonSerializer.SerializeToUtf8Bytes(approved, _snapshotJsonOptions);
      var protectedData = _approvedResourceProtector.Protect(
          plaintext,
          ApprovedResourceEntropy(
              run.RunId,
              approvedResource.Definition.Id,
              fingerprint));
      entries.Add(new ProtectedApprovedResource(
          approvedResource.Definition.Id,
          fingerprint,
          Convert.ToBase64String(protectedData)));
    }

    if (entries.Count == 0)
    {
      return false;
    }

    var bytes = JsonSerializer.SerializeToUtf8Bytes(
        new ApprovedResourceEnvelope(run.RunId, entries),
        _snapshotJsonOptions);
    await WriteBytesAtomicallyAsync(
        ApprovedResourcesPath(run.RunId),
        bytes,
        cancellationToken).ConfigureAwait(false);
    return true;
  }

  private IReadOnlyDictionary<string, ApprovedResource> ValidateApprovedResourceEnvelope(
      Guid runId,
      ApprovedResourceEnvelope? envelope,
      IReadOnlyList<PlannedResource> expectedResources)
  {
    if (envelope is null || envelope.RunId != runId || envelope.Resources is null ||
        envelope.Resources.Count != expectedResources.Count)
    {
      throw new InvalidOperationException(
          "The approved resource snapshot does not match the elevated execution plan.");
    }

    var expectedById = expectedResources.ToDictionary(
        resource => resource.Definition.Id,
        StringComparer.OrdinalIgnoreCase);
    var observedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var approvedById = new Dictionary<string, ApprovedResource>(
        StringComparer.OrdinalIgnoreCase);
    foreach (var entry in envelope.Resources)
    {
      if (entry is null || string.IsNullOrWhiteSpace(entry.ResourceId) ||
          !observedIds.Add(entry.ResourceId) ||
          !expectedById.TryGetValue(entry.ResourceId, out var expected))
      {
        throw new InvalidOperationException(
            "The approved resource snapshot contains a missing, extra, or duplicate resource.");
      }

      ValidateSha256(entry.Fingerprint, "approved resource fingerprint");
      var protectedData = Convert.FromBase64String(entry.ProtectedPayload);
      var plaintext = _approvedResourceProtector.Unprotect(
          protectedData,
          ApprovedResourceEntropy(runId, entry.ResourceId, entry.Fingerprint));
      var approved = JsonSerializer.Deserialize<ApprovedResource>(
          plaintext,
          _snapshotJsonOptions);
      if (approved?.Definition is null || approved.Plan is null)
      {
        throw new InvalidOperationException(
            "A protected approved resource payload is incomplete.");
      }

      ValidateDefinition(approved.Definition);
      ValidateResourcePlan(approved.Plan);
      var expectedFingerprint = ApprovedResourceFingerprint.Create(
          approved.Definition,
          expected.ResourcePlan);
      if (!string.Equals(
              approved.Definition.Id,
              entry.ResourceId,
              StringComparison.OrdinalIgnoreCase) ||
          !FixedEquals(approved.Fingerprint, entry.Fingerprint) ||
          !FixedEquals(
              approved.Fingerprint,
              ApprovedResourceFingerprint.Create(approved.Definition, approved.Plan)) ||
          !DependenciesEqual(expected.Dependencies, approved.Definition.Dependencies) ||
          !FixedEquals(
              approved.Fingerprint,
              expectedFingerprint))
      {
        throw new InvalidOperationException(
            "A protected approved resource does not match its snapshot entry.");
      }

      var administratorSegments = PrivilegePlanSegments.Split(approved.Plan)
          .Where(segment => segment.Steps.Any(step =>
              step.Action != PlanAction.None &&
              step.PrivilegeRequirement == PrivilegeRequirement.Administrator))
          .Select(segment => ApprovedResourceFingerprint.Create(
              approved.Definition,
              segment))
          .ToHashSet(StringComparer.OrdinalIgnoreCase);
      var observedClaims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var claimedFingerprint in entry.ClaimedPlanFingerprints ?? [])
      {
        ValidateSha256(claimedFingerprint, "claimed approved plan fingerprint");
        if (!observedClaims.Add(claimedFingerprint) ||
            !administratorSegments.Contains(claimedFingerprint))
        {
          throw new InvalidOperationException(
              "The approved resource snapshot contains an invalid or duplicate claim.");
        }
      }

      approvedById.Add(entry.ResourceId, approved);
    }

    return approvedById;
  }

  private ApprovedResourceStoreException ApprovedResourceStorageFailure(
      Guid runId,
      string resourceId,
      Exception exception,
      bool isRetryable)
  {
    var error = new StructuredError(
        WdemErrorCode.PermissionError,
        isRetryable
            ? "Approved resource snapshot is temporarily unavailable."
            : "Approved resource snapshot could not be opened.",
        isRetryable
            ? $"Run '{runId:D}' resource '{resourceId}' could not be authorized because its protected snapshot could not be opened."
            : $"Run '{runId:D}' resource '{resourceId}' was not authorized because its protected snapshot is invalid.")
    {
      ResourceId = resourceId,
      IsRetryable = isRetryable,
      UnderlyingException = exception
    };
    lock (_diagnosticsGate)
    {
      _diagnostics.Add(error);
    }

    return new ApprovedResourceStoreException(error, exception);
  }

  private static byte[] ApprovedResourceEntropy(
      Guid runId,
      string resourceId,
      string fingerprint) => SHA256.HashData(Utf8WithoutBom.GetBytes(
          $"wdem-approved-resource\0{runId:D}\0{resourceId}\0{fingerprint}"));

  private static bool FixedEquals(string? left, string? right)
  {
    if (left is null || right is null || left.Length != 64 || right.Length != 64)
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

  private static bool DependenciesEqual(
      IReadOnlyList<string> planned,
      IReadOnlyList<string> approved) =>
      planned.Count == approved.Count &&
      planned.SequenceEqual(approved, StringComparer.OrdinalIgnoreCase);

  private void EnsurePlanApprovalUnchanged(PlanApproval? current, PlanApproval? replacement)
  {
    var currentBytes = JsonSerializer.SerializeToUtf8Bytes(current, _snapshotJsonOptions);
    var replacementBytes = JsonSerializer.SerializeToUtf8Bytes(replacement, _snapshotJsonOptions);
    if (!currentBytes.AsSpan().SequenceEqual(replacementBytes))
    {
      throw new InvalidOperationException(
          "The original plan approval proof is immutable for the lifetime of the run.");
    }
  }

  private static void ValidatePersistenceTransition(
      ExecutionRun current,
      ExecutionRun replacement)
  {
    if (current.State == ExecutionState.Completed &&
        replacement.State != ExecutionState.Completed)
    {
      throw new InvalidOperationException(
          "A completed execution run cannot return to a non-terminal state.");
    }

    if (current.State == ExecutionState.Completed &&
        replacement.State == ExecutionState.Completed &&
        !IsAllowedTerminalOutcomeTransition(current.Outcome, replacement.Outcome))
    {
      throw new InvalidOperationException(
          "An authoritative terminal execution run outcome cannot be replaced.");
    }

    foreach (var pair in current.ResourceResults)
    {
      if (pair.Value.State is not (ExecutionState.Completed or ExecutionState.Blocked))
      {
        continue;
      }

      if (!replacement.ResourceResults.TryGetValue(pair.Key, out var next) ||
          next.State is not (ExecutionState.Completed or ExecutionState.Blocked))
      {
        throw new InvalidOperationException(
            $"Terminal resource result '{pair.Key}' cannot return to a non-terminal state.");
      }

      if (!IsAllowedTerminalOutcomeTransition(pair.Value.Outcome, next.Outcome))
      {
        throw new InvalidOperationException(
            $"Authoritative terminal resource result '{pair.Key}' cannot be replaced.");
      }
    }
  }

  private static bool IsAllowedTerminalOutcomeTransition(
      ExecutionOutcome? current,
      ExecutionOutcome? replacement) =>
      current == replacement ||
      current == ExecutionOutcome.Cancelled &&
      replacement is ExecutionOutcome.Succeeded or
          ExecutionOutcome.NotRequired or
          ExecutionOutcome.Failed;

  private void DeleteApprovedResources(Guid runId)
  {
    var path = ApprovedResourcesPath(runId);
    try
    {
      if (File.Exists(path))
      {
        File.Delete(path);
      }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      var diagnostic = new StructuredError(
          WdemErrorCode.PermissionError,
          "Approved resource snapshot could not be removed.",
          $"Run '{runId:D}' remains terminal, but its protected approval snapshot could not be removed.")
      {
        SuggestedAction = "Retry loading or saving the terminal run after the file becomes available.",
        IsRetryable = true,
        UnderlyingException = exception
      };
      lock (_diagnosticsGate)
      {
        _diagnostics.Add(diagnostic);
      }
    }
  }

  private static async Task WriteBytesAtomicallyAsync(
      string path,
      byte[] bytes,
      CancellationToken cancellationToken)
  {
    var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
    try
    {
      await using (var stream = new FileStream(
          temporaryPath,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.None,
          4096,
          FileOptions.Asynchronous | FileOptions.WriteThrough))
      {
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
      }

      cancellationToken.ThrowIfCancellationRequested();
      if (File.Exists(path))
      {
        File.Replace(temporaryPath, path, destinationBackupFileName: null);
      }
      else
      {
        File.Move(temporaryPath, path);
      }
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        File.Delete(temporaryPath);
      }
    }
  }

  private async Task<LogIndexState> ReconcileLogIndexAsync(
      string logPath,
      CancellationToken cancellationToken)
  {
    await using var log = SecureBoundedFileReader.TryOpenMutableFile(
        logPath,
        _paths.RunsDirectory,
        FileAccess.ReadWrite,
        "run log");
    if (log is null)
    {
      return new LogIndexState(0, 0, 0);
    }

    var indexPath = logPath + ".index";
    var state = await ValidateLogIndexAsync(log, indexPath, cancellationToken)
        .ConfigureAwait(false);
    if (state is null)
    {
      return await RebuildLogIndexAsync(log, indexPath, cancellationToken)
          .ConfigureAwait(false);
    }

    log.Position = state.Value.IndexedLength;
    await using var index = SecureBoundedFileReader.OpenMutableFile(
        indexPath,
        _paths.RunsDirectory,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        "run log index");
    index.Position = state.Value.Count * LogIndexRecordSize;
    var reconciled = await AppendLogTailToIndexAsync(
        log,
        index,
        state.Value,
        logPath,
        cancellationToken).ConfigureAwait(false);
    await FlushLogAndIndexAsync(log, index, cancellationToken).ConfigureAwait(false);
    RememberValidatedLogIndex(logPath, reconciled);
    return reconciled;
  }

  private async Task<LogIndexState?> ValidateLogIndexAsync(
      FileStream log,
      string indexPath,
      CancellationToken cancellationToken)
  {
    if (!File.Exists(indexPath))
    {
      return new LogIndexState(0, 0, 0);
    }

    var fingerprint = GetLogIndexFingerprint(indexPath);
    lock (_logIndexGate)
    {
      if (_validatedLogIndexes.TryGetValue(indexPath, out var validated)
          && validated.Fingerprint == fingerprint
          && validated.State.IndexedLength <= log.Length)
      {
        return validated.State;
      }
    }

    if (fingerprint.Length % LogIndexRecordSize != 0)
    {
      return null;
    }

    await using var index = SecureBoundedFileReader.OpenMutableFile(
        indexPath,
        _paths.RunsDirectory,
        FileMode.Open,
        FileAccess.Read,
        "run log index");
    var count = index.Length / LogIndexRecordSize;
    var lastSequence = 0L;
    var indexedLength = 0L;
    for (var ordinal = 0L; ordinal < count; ordinal++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var record = await ReadLogIndexRecordAsync(index, ordinal, cancellationToken)
          .ConfigureAwait(false);
      if (record.Sequence <= lastSequence
          || record.StartOffset != indexedLength
          || record.EndOffset <= record.StartOffset
          || record.EndOffset > log.Length
          || record.EndOffset - record.StartOffset > int.MaxValue)
      {
        return null;
      }

      var bytes = new byte[(int)(record.EndOffset - record.StartOffset)];
      log.Position = record.StartOffset;
      await log.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
      if (bytes[^1] != (byte)'\n')
      {
        return null;
      }

      RunLogEntry entry;
      try
      {
        entry = DeserializeLogEntry(DecodeLogLine(bytes), log.Name);
      }
      catch (InvalidDataException)
      {
        return null;
      }

      if (entry.Sequence != record.Sequence)
      {
        return null;
      }

      lastSequence = record.Sequence;
      indexedLength = record.EndOffset;
    }

    return new LogIndexState(count, lastSequence, indexedLength);
  }

  private async Task<LogIndexState> RebuildLogIndexAsync(
      FileStream log,
      string indexPath,
      CancellationToken cancellationToken)
  {
    var logPath = indexPath[..^".index".Length];
    var temporaryPath = $"{indexPath}.{Guid.NewGuid():N}.tmp";
    try
    {
      log.Position = 0;
      LogIndexState rebuilt;
      await using (var temporaryIndex = SecureBoundedFileReader.OpenMutableFile(
          temporaryPath,
          _paths.RunsDirectory,
          FileMode.CreateNew,
          FileAccess.Write,
          "temporary run log index"))
      {
        rebuilt = await AppendLogTailToIndexAsync(
            log,
            temporaryIndex,
            new LogIndexState(0, 0, 0),
            logPath,
            cancellationToken).ConfigureAwait(false);
        await FlushLogAndIndexAsync(log, temporaryIndex, cancellationToken)
            .ConfigureAwait(false);
      }

      cancellationToken.ThrowIfCancellationRequested();
      if (File.Exists(indexPath))
      {
        File.Replace(temporaryPath, indexPath, destinationBackupFileName: null);
      }
      else
      {
        File.Move(temporaryPath, indexPath);
      }

      RememberValidatedLogIndex(logPath, rebuilt);
      return rebuilt;
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        File.Delete(temporaryPath);
      }
    }
  }

  private async Task<LogIndexState> AppendLogTailToIndexAsync(
      FileStream log,
      FileStream index,
      LogIndexState initialState,
      string logPath,
      CancellationToken cancellationToken)
  {
    var count = initialState.Count;
    var lastSequence = initialState.LastSequence;
    var indexedLength = initialState.IndexedLength;
    while (log.Position < log.Length)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var startOffset = log.Position;
      var (lineBytes, terminated) = ReadRawLogLine(log, cancellationToken);
      if (lineBytes.Length == 0 && !terminated)
      {
        break;
      }

      RunLogEntry entry;
      try
      {
        entry = DeserializeLogEntry(DecodeLogLine(lineBytes), logPath);
      }
      catch (InvalidDataException) when (!terminated)
      {
        log.SetLength(startOffset);
        break;
      }

      if (entry.Sequence <= lastSequence)
      {
        throw new InvalidDataException(
            $"Run log '{logPath}' contains a non-increasing sequence.");
      }

      if (!terminated)
      {
        log.Position = log.Length;
        await log.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
      }

      var record = new LogIndexRecord(entry.Sequence, startOffset, log.Position);
      await WriteLogIndexRecordAsync(index, record, cancellationToken).ConfigureAwait(false);
      count++;
      lastSequence = entry.Sequence;
      indexedLength = record.EndOffset;
    }

    return new LogIndexState(count, lastSequence, indexedLength);
  }

  private static async Task FlushLogAndIndexAsync(
      FileStream log,
      FileStream index,
      CancellationToken cancellationToken)
  {
    await log.FlushAsync(cancellationToken).ConfigureAwait(false);
    log.Flush(flushToDisk: true);
    await index.FlushAsync(cancellationToken).ConfigureAwait(false);
    index.Flush(flushToDisk: true);
  }

  private void RememberValidatedLogIndex(string logPath, LogIndexState state)
  {
    var indexPath = logPath + ".index";
    var validated = new ValidatedLogIndex(GetLogIndexFingerprint(indexPath), state);
    lock (_logIndexGate)
    {
      _validatedLogIndexes[indexPath] = validated;
    }
  }

  private static LogIndexFingerprint GetLogIndexFingerprint(string indexPath)
  {
    var info = new FileInfo(indexPath);
    info.Refresh();
    return new LogIndexFingerprint(info.Length, info.LastWriteTimeUtc.Ticks);
  }

  private async Task<IReadOnlyList<RunLogEntry>> ReadIndexedLogPageAsync(
      string logPath,
      long recordCount,
      long afterSequence,
      int take,
      CancellationToken cancellationToken)
  {
    await using var index = SecureBoundedFileReader.OpenMutableFile(
        logPath + ".index",
        _paths.RunsDirectory,
        FileMode.Open,
        FileAccess.Read,
        "run log index");
    var low = 0L;
    var high = recordCount;
    while (low < high)
    {
      var middle = low + ((high - low) / 2);
      var record = await ReadLogIndexRecordAsync(index, middle, cancellationToken)
          .ConfigureAwait(false);
      if (record.Sequence <= afterSequence)
      {
        low = middle + 1;
      }
      else
      {
        high = middle;
      }
    }

    var entries = new List<RunLogEntry>(take);
    await using var log = SecureBoundedFileReader.OpenMutableFile(
        logPath,
        _paths.RunsDirectory,
        FileMode.Open,
        FileAccess.Read,
        "run log");
    for (var ordinal = low; ordinal < recordCount && entries.Count < take; ordinal++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var record = await ReadLogIndexRecordAsync(index, ordinal, cancellationToken)
          .ConfigureAwait(false);
      var byteCount = record.EndOffset - record.StartOffset;
      if (byteCount is <= 0 or > int.MaxValue)
      {
        throw new InvalidDataException($"Run log index for '{logPath}' is invalid.");
      }

      var bytes = new byte[(int)byteCount];
      log.Position = record.StartOffset;
      await log.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
      var entry = DeserializeLogEntry(DecodeLogLine(bytes), logPath);
      if (entry.Sequence != record.Sequence)
      {
        throw new InvalidDataException($"Run log index for '{logPath}' is inconsistent.");
      }

      entries.Add(entry);
    }

    return entries;
  }

  private static (byte[] Bytes, bool Terminated) ReadRawLogLine(
      FileStream stream,
      CancellationToken cancellationToken)
  {
    using var line = new MemoryStream();
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var value = stream.ReadByte();
      if (value < 0)
      {
        return (line.ToArray(), false);
      }

      if (value == '\n')
      {
        return (line.ToArray(), true);
      }

      line.WriteByte((byte)value);
    }
  }

  private static string DecodeLogLine(byte[] bytes)
  {
    var length = bytes.Length;
    while (length > 0 && bytes[length - 1] is (byte)'\r' or (byte)'\n')
    {
      length--;
    }

    return Utf8WithoutBom.GetString(bytes, 0, length);
  }

  private async Task AppendLogIndexRecordAsync(
      string logPath,
      LogIndexRecord record,
      CancellationToken cancellationToken)
  {
    await using var index = SecureBoundedFileReader.OpenMutableFile(
        logPath + ".index",
        _paths.RunsDirectory,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        "run log index");
    index.Position = index.Length;
    await WriteLogIndexRecordAsync(index, record, cancellationToken).ConfigureAwait(false);
    await index.FlushAsync(cancellationToken).ConfigureAwait(false);
    index.Flush(flushToDisk: true);
  }

  private static async Task<LogIndexRecord> ReadLogIndexRecordAsync(
      FileStream index,
      long ordinal,
      CancellationToken cancellationToken)
  {
    var bytes = new byte[LogIndexRecordSize];
    index.Position = ordinal * LogIndexRecordSize;
    await index.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
    return new LogIndexRecord(
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0, sizeof(long))),
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(sizeof(long), sizeof(long))),
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(sizeof(long) * 2, sizeof(long))));
  }

  private static async Task WriteLogIndexRecordAsync(
      FileStream index,
      LogIndexRecord record,
      CancellationToken cancellationToken)
  {
    var bytes = new byte[LogIndexRecordSize];
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0, sizeof(long)), record.Sequence);
    BinaryPrimitives.WriteInt64LittleEndian(
        bytes.AsSpan(sizeof(long), sizeof(long)),
        record.StartOffset);
    BinaryPrimitives.WriteInt64LittleEndian(
        bytes.AsSpan(sizeof(long) * 2, sizeof(long)),
        record.EndOffset);
    await index.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
  }

  private RunLogEntry DeserializeLogEntry(string line, string logPath)
  {
    try
    {
      var entry = JsonSerializer.Deserialize<RunLogEntry>(line, _logJsonOptions)
          ?? throw new JsonException("The log entry was null.");
      ValidateLogEntryForPersistence(entry);
      return entry;
    }
    catch (Exception exception) when (exception is JsonException or ArgumentException)
    {
      throw new InvalidDataException($"Run log '{logPath}' contains malformed NDJSON.", exception);
    }
  }

  private static void EnsureRunExists(Guid runId, string path)
  {
    if (!File.Exists(path))
    {
      throw new KeyNotFoundException($"Execution run '{runId:D}' does not exist.");
    }
  }

  private void ValidateRunForPersistence(ExecutionRun run)
  {
    ValidateRun(run);

    if (run.RecoveryClaimedAtUtc is { } claimedAt)
    {
      var now = _timeProvider.GetUtcNow();
      if (claimedAt > now && claimedAt - now > MaximumClaimClockSkew)
      {
        throw new ArgumentException(
            "A recovery claim timestamp cannot be excessively far in the future.",
            nameof(run));
      }
    }
  }

  private static void ValidateRun(ExecutionRun run)
  {
    if (run.Revision < 0 || run.Revision == long.MaxValue)
    {
      throw new ArgumentException(
          "An execution run revision must be non-negative and incrementable.",
          nameof(run));
    }

    if (run.RecoveryClaimId.HasValue != run.RecoveryClaimedAtUtc.HasValue)
    {
      throw new ArgumentException(
          "A recovery claim requires both an identifier and a claim timestamp.",
          nameof(run));
    }

    ValidateEnum(run.Mode, "run mode");
    ValidateEnum(run.State, "run state");
    ValidateOptionalEnum(run.Outcome, "run outcome");
    ValidateElements(run.SelectedOptionalResourceIds, "selected optional resource identifiers");
    ValidateElements(
        run.AcknowledgedRestartResourceIds,
        "acknowledged restart resource identifiers");
    ValidateElements(run.ResourceResults.Values, "resource results");
    ValidateElements(run.RestartReasons, "restart reasons");
    foreach (var restartRequirement in run.RestartRequirements)
    {
      ValidateEnum(restartRequirement, "restart requirement");
    }

    if (run.Graph is not null)
    {
      ValidateGraph(run.Graph);
    }

    if (run.Plan is not null)
    {
      ValidatePlan(run.Plan);
    }

    ValidatePlanApproval(run);

    foreach (var result in run.ResourceResults.Values)
    {
      ValidateResourceResult(result);
    }

    if (run.State == ExecutionState.Completed
        && (run.Outcome is null || run.EndedAtUtc is null))
    {
      throw new ArgumentException(
          "A completed execution run requires both an outcome and an end timestamp.",
          nameof(run));
    }

    if (run.State != ExecutionState.Completed
        && (run.Outcome is not null || run.EndedAtUtc is not null))
    {
      throw new ArgumentException(
          "An incomplete execution run cannot have an outcome or end timestamp.",
          nameof(run));
    }
  }

  private static void ValidateGraph(ResourceGraph graph)
  {
    ArgumentNullException.ThrowIfNull(graph.Nodes);
    ValidateElements(graph.Nodes.Values, "graph nodes");
    ValidateElements(graph.TopologicalLayers, "graph layers");
    foreach (var node in graph.Nodes.Values)
    {
      ValidateEnum(node.Origin, "resource origin");
      ValidateElements(node.RequiredBy, "resource dependants");
      ValidateDefinition(node.Definition);
    }

    foreach (var layer in graph.TopologicalLayers)
    {
      ValidateElements(layer.ResourceIds, "graph layer resource identifiers");
    }
  }

  private static void ValidatePlan(ExecutionPlan plan)
  {
    ValidateElements(plan.Layers, "plan layers");
    ValidateElements(plan.Resources, "planned resources");
    ValidateElements(plan.Errors, "plan errors");
    foreach (var layer in plan.Layers)
    {
      ValidateElements(layer.ResourceIds, "plan layer resource identifiers");
    }

    foreach (var resource in plan.Resources)
    {
      ValidateDefinition(resource.Definition);
      ValidateEnum(resource.Origin, "planned resource origin");
      ValidateEnum(resource.Status, "planned resource status");
      ValidateEnum(resource.Risk, "plan risk");
      ValidateEnum(resource.RestartPolicy, "planned resource restart policy");
      if (resource.Status == PlannedResourceStatus.Deferred &&
          resource.DeferredAuthorization is null)
      {
        throw new ArgumentException(
            "A deferred planned resource requires an authorization boundary.",
            nameof(plan));
      }

      if (resource.Status != PlannedResourceStatus.Deferred &&
          resource.DeferredAuthorization is not null)
      {
        throw new ArgumentException(
            "Only a deferred planned resource may retain an authorization boundary.",
            nameof(plan));
      }

      if (resource.DeferredAuthorization is { } authorization &&
          (authorization.AllowedActions.Count == 0 ||
              authorization.AllowedActions.Any(action => action == PlanAction.None)))
      {
        throw new ArgumentException(
            "A deferred authorization requires at least one modifying action.",
            nameof(plan));
      }

      if (resource.DeferredAuthorization is { } deferredAuthorization)
      {
        foreach (var action in deferredAuthorization.AllowedActions)
        {
          ValidateEnum(action, "deferred authorization action");
        }

        ValidateEnum(
            deferredAuthorization.MaximumPrivilege,
            "deferred authorization maximum privilege");
        ValidateEnum(
            deferredAuthorization.MaximumRestartPolicy,
            "deferred authorization maximum restart policy");
        ValidateEnum(
            deferredAuthorization.MaximumRisk,
            "deferred authorization maximum risk");
        if (string.IsNullOrWhiteSpace(deferredAuthorization.DynamicPlanNotice))
        {
          throw new ArgumentException(
              "A deferred authorization requires a dynamic planning notice.",
              nameof(plan));
        }

        if (resource.RequiresElevation !=
                (deferredAuthorization.MaximumPrivilege ==
                    PrivilegeRequirement.Administrator) ||
            resource.RestartPolicy != deferredAuthorization.MaximumRestartPolicy ||
            resource.Risk != deferredAuthorization.MaximumRisk ||
            resource.IsDestructive != deferredAuthorization.AllowDestructive)
        {
          throw new ArgumentException(
              "A deferred resource summary must match its authorization boundary.",
              nameof(plan));
        }

        if (deferredAuthorization.MaximumPrivilege >
                resource.Definition.PrivilegeRequirement ||
            deferredAuthorization.MaximumRestartPolicy >
                resource.Definition.RestartPolicy)
        {
          throw new ArgumentException(
              "A deferred authorization cannot exceed its resource definition.",
              nameof(plan));
        }
      }

      ValidateElements(resource.Dependencies, "planned resource dependencies");
      ValidateElements(resource.BlockedBy, "blocking resource identifiers");
      ValidateElements(resource.Diagnostics, "planned resource diagnostics");
      foreach (var diagnostic in resource.Diagnostics)
      {
        ValidateStructuredError(diagnostic);
      }

      ValidateResourcePlan(resource.ResourcePlan);
    }

    foreach (var error in plan.Errors)
    {
      ValidateStructuredError(error);
    }
  }

  private static void ValidatePlanApproval(ExecutionRun run)
  {
    if (run.PlanApproval is null)
    {
      if (run.Mode == RunMode.Apply &&
          run.Plan is { IsExecutable: true } plan &&
          plan.Resources.Any(resource => resource.Status == PlannedResourceStatus.Deferred))
      {
        throw new ArgumentException(
            "An apply plan with deferred resources requires approval proof.",
            nameof(run));
      }

      return;
    }

    if (run.Mode != RunMode.Apply || run.Plan is null)
    {
      throw new ArgumentException(
          "Plan approval proof is only valid for an apply run with a plan.",
          nameof(run));
    }

    var approval = run.PlanApproval;
    ValidateSha256(approval.InitialPlanFingerprint, "initial approved plan fingerprint");
    if (approval.ConfirmedAtUtc == default)
    {
      throw new ArgumentException(
          "Plan approval proof requires a confirmation timestamp.",
          nameof(run));
    }

    ValidateEnum(approval.Source, "plan approval source");
    ValidateElements(approval.DeferredAuthorizations, "deferred authorization proofs");
    var proofIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var proof in approval.DeferredAuthorizations)
    {
      ArgumentException.ThrowIfNullOrWhiteSpace(proof.ResourceId);
      ArgumentException.ThrowIfNullOrWhiteSpace(proof.ResourceType);
      ArgumentException.ThrowIfNullOrWhiteSpace(proof.ProviderName);
      ValidateSha256(proof.DefinitionFingerprint, "deferred definition fingerprint");
      ValidateEnum(proof.Origin, "deferred resource origin");
      ValidateEnum(proof.MaximumPrivilege, "deferred proof maximum privilege");
      ValidateEnum(proof.MaximumRestartPolicy, "deferred proof maximum restart policy");
      ValidateEnum(proof.MaximumRisk, "deferred proof maximum risk");
      ValidateElements(proof.Dependencies, "deferred proof dependencies");
      ValidateElements(proof.AllowedActions, "deferred proof actions");
      if (!proofIds.Add(proof.ResourceId) ||
          proof.AllowedActions.Count == 0 ||
          proof.AllowedActions.Any(action => action == PlanAction.None))
      {
        throw new ArgumentException(
            "Deferred authorization proofs require unique resources and modifying actions.",
            nameof(run));
      }

      foreach (var action in proof.AllowedActions)
      {
        ValidateEnum(action, "deferred proof action");
      }

      var matches = run.Plan.Resources.Where(resource => string.Equals(
          resource.Definition.Id,
          proof.ResourceId,
          StringComparison.OrdinalIgnoreCase)).ToArray();
      if (matches.Length != 1 || !IsApprovedRefinement(matches[0], proof))
      {
        throw new ArgumentException(
            "The current plan is not a refinement of its deferred approval proof.",
            nameof(run));
      }
    }

    foreach (var deferred in run.Plan.Resources.Where(resource =>
                 resource.Status == PlannedResourceStatus.Deferred))
    {
      if (!proofIds.Contains(deferred.Definition.Id))
      {
        throw new ArgumentException(
            "Every deferred resource requires persistent approval proof.",
            nameof(run));
      }
    }
  }

  private static bool IsApprovedRefinement(
      PlannedResource resource,
      DeferredAuthorizationProof proof)
  {
    if (!string.Equals(resource.Definition.Id, proof.ResourceId, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(resource.Definition.Type, proof.ResourceType, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(resource.Definition.Provider, proof.ProviderName, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            resource.ResourcePlan.DesiredStateFingerprint,
            proof.DefinitionFingerprint,
            StringComparison.Ordinal) ||
        resource.Origin != proof.Origin ||
        resource.Dependencies.Count != proof.Dependencies.Count ||
        resource.Dependencies.Any(dependency =>
            !proof.Dependencies.Contains(dependency, StringComparer.OrdinalIgnoreCase)))
    {
      return false;
    }

    if (resource.Status == PlannedResourceStatus.Deferred)
    {
      var authorization = resource.DeferredAuthorization;
      return authorization is not null &&
          authorization.AllowedActions.SequenceEqual(proof.AllowedActions) &&
          authorization.MaximumPrivilege == proof.MaximumPrivilege &&
          authorization.MaximumRestartPolicy == proof.MaximumRestartPolicy &&
          authorization.MaximumRisk == proof.MaximumRisk &&
          authorization.AllowDestructive == proof.AllowDestructive;
    }

    if (resource.Status is not (PlannedResourceStatus.Ready or
            PlannedResourceStatus.AlreadySatisfied) ||
        resource.DeferredAuthorization is not null ||
        resource.Risk > proof.MaximumRisk ||
        resource.RequiresElevation &&
            proof.MaximumPrivilege != PrivilegeRequirement.Administrator ||
        resource.IsDestructive && !proof.AllowDestructive ||
        resource.RestartPolicy > proof.MaximumRestartPolicy)
    {
      return false;
    }

    return resource.ResourcePlan.Steps.All(step =>
        PlanStepAuthorizationPolicy.IsWithinBoundary(
            step,
            proof.AllowedActions,
            proof.MaximumPrivilege,
            proof.MaximumRestartPolicy,
            proof.AllowDestructive));
  }

  private static void ValidateSha256(string value, string field)
  {
    if (string.IsNullOrWhiteSpace(value) ||
        value.Length != 64 ||
        !value.All(Uri.IsHexDigit))
    {
      throw new ArgumentException($"The persisted {field} must be a SHA-256 hexadecimal value.");
    }
  }

  private static void ValidateResourcePlan(ResourcePlan plan)
  {
    ArgumentNullException.ThrowIfNull(plan);
    if (plan.ExecutionPreconditionFingerprint is { } precondition &&
        (precondition.Length != 64 || !precondition.All(Uri.IsHexDigit)))
    {
      throw new ArgumentException(
          "A resource plan execution precondition must be a SHA-256 hexadecimal value.",
          nameof(plan));
    }

    ValidateEnum(plan.Compliance, "resource plan compliance");
    ValidateElements(plan.Steps, "plan steps");
    ValidateElements(plan.StructuredErrors, "resource plan errors");
    foreach (var step in plan.Steps)
    {
      ValidateEnum(step.Action, "plan action");
      ValidateEnum(step.PrivilegeRequirement, "plan step privilege requirement");
      ValidateEnum(step.RestartPolicy, "plan step restart policy");
      if (!PlanStepAuthorizationPolicy.IsSafeDeclaration(step))
      {
        throw new ArgumentException(
            "A non-modifying plan step cannot require elevation, restart, or destructive execution.",
            nameof(plan));
      }
    }

    foreach (var error in plan.StructuredErrors)
    {
      ValidateStructuredError(error);
    }
  }

  private static void ValidateDefinition(ResourceDefinition definition)
  {
    ArgumentNullException.ThrowIfNull(definition);
    ValidateEnum(definition.PrivilegeRequirement, "resource privilege requirement");
    ValidateEnum(definition.RestartPolicy, "resource restart policy");
    ValidateElements(definition.Dependencies, "resource dependencies");
  }

  private static void ValidateResourceResult(ResourceResult result)
  {
    ValidateEnum(result.State, "resource result state");
    ValidateOptionalEnum(result.Outcome, "resource result outcome");
    ValidateOptionalEnum(result.FinalCompliance, "final compliance");
    ValidateEnum(result.RestartRequirement, "resource restart requirement");
    ValidateElements(result.StepResults, "step results");
    if (result.DetectedBefore is not null)
    {
      ValidateDetectedState(result.DetectedBefore);
    }

    if (result.DetectedAfter is not null)
    {
      ValidateDetectedState(result.DetectedAfter);
    }

    if (result.Error is not null)
    {
      ValidateStructuredError(result.Error);
    }

    foreach (var step in result.StepResults)
    {
      ValidateEnum(step.State, "step result state");
      ValidateOptionalEnum(step.Outcome, "step result outcome");
      if (step.Error is not null)
      {
        ValidateStructuredError(step.Error);
      }
    }
  }

  private static void ValidateDetectedState(DetectedState state)
  {
    ValidateEnum(state.Outcome, "detection outcome");
    ValidateElements(state.InstalledVersions, "installed versions");
    if (state.StructuredError is not null)
    {
      ValidateStructuredError(state.StructuredError);
    }
  }

  private static void ValidateStructuredError(StructuredError error) =>
      ValidateEnum(error.Code, "structured error code");

  private static void ValidateLogEntryForPersistence(RunLogEntry entry)
  {
    ValidateEnum(entry.Level, "log level");
    ValidateOptionalEnum(entry.Kind, "run event kind");
    ValidateOptionalEnum(entry.State, "execution state");
    ValidateOptionalEnum(entry.Outcome, "execution outcome");
    ValidateOptionalEnum(entry.RestartRequirement, "restart requirement");
    if (entry.Error is not null)
    {
      ValidateStructuredError(entry.Error);
    }
  }

  private static void ValidateEnum<TEnum>(TEnum value, string field)
      where TEnum : struct, Enum
  {
    if (!Enum.IsDefined(value))
    {
      throw new ArgumentException($"The persisted {field} has an undefined value.");
    }
  }

  private static void ValidateOptionalEnum<TEnum>(TEnum? value, string field)
      where TEnum : struct, Enum
  {
    if (value is TEnum defined)
    {
      ValidateEnum(defined, field);
    }
  }

  private static void ValidateElements<T>(IEnumerable<T> values, string field)
  {
    ArgumentNullException.ThrowIfNull(values);
    if (values.Any(value => value is null))
    {
      throw new ArgumentException($"The persisted {field} cannot contain null elements.");
    }
  }

  private static ExecutionRun SnapshotRestoredRun(ExecutionRun run) => run with
  {
    Graph = run.Graph is null ? null : SnapshotRestoredGraph(run.Graph),
    Plan = run.Plan is null ? null : SnapshotRestoredPlan(run.Plan)
  };

  private static ResourceGraph SnapshotRestoredGraph(ResourceGraph graph) => graph with
  {
    Nodes = graph.Nodes.ToFrozenDictionary(
        pair => pair.Key,
        pair => pair.Value with
        {
          Definition = SnapshotRestoredDefinition(pair.Value.Definition),
          RequiredBy = pair.Value.RequiredBy.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
        },
        StringComparer.OrdinalIgnoreCase),
    TopologicalLayers = SnapshotRestoredLayers(graph.TopologicalLayers)
  };

  private static ExecutionPlan SnapshotRestoredPlan(ExecutionPlan plan) => plan with
  {
    Layers = SnapshotRestoredLayers(plan.Layers),
    Resources = ReadOnly(plan.Resources.Select(SnapshotRestoredResource)),
    Errors = ReadOnly(plan.Errors)
  };

  private static PlannedResource SnapshotRestoredResource(PlannedResource resource) =>
      resource with
      {
        Definition = SnapshotRestoredDefinition(resource.Definition),
        Dependencies = ReadOnly(resource.Dependencies),
        ResourcePlan = resource.ResourcePlan with
        {
          Steps = ReadOnly(resource.ResourcePlan.Steps),
          StructuredErrors = ReadOnly(resource.ResourcePlan.StructuredErrors)
        },
        BlockedBy = ReadOnly(resource.BlockedBy),
        Diagnostics = ReadOnly(resource.Diagnostics)
      };

  private static IReadOnlyList<ResourceGraphLayer> SnapshotRestoredLayers(
      IEnumerable<ResourceGraphLayer> layers) =>
      ReadOnly(layers.Select(layer => layer with
      {
        ResourceIds = ReadOnly(layer.ResourceIds)
      }));

  private static ResourceDefinition SnapshotRestoredDefinition(ResourceDefinition definition) =>
      definition with
      {
        Dependencies = ReadOnly(definition.Dependencies),
        Parameters = definition.Parameters.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
      };

  private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
      Array.AsReadOnly(values.ToArray());

  private async Task PersistSnapshotWithCommitmentAsync(
      ExecutionRun run,
      byte[] snapshotBytes,
      CancellationToken cancellationToken)
  {
    SecureBoundedFileReader.EnsureLengthWithinMaximum(
        snapshotBytes.LongLength,
        MaximumSnapshotBytes,
        "snapshot");
    var path = SnapshotPath(run.RunId);
    if (run.Plan is null || run.PlanApproval is null)
    {
      await WriteBytesAtomicallyAsync(path, snapshotBytes, cancellationToken)
          .ConfigureAwait(false);
      return;
    }

    await using var stateLock = await AcquireSnapshotFormatStateLockAsync(cancellationToken)
        .ConfigureAwait(false);
    var index = await ReadAuthenticatedSnapshotFormatIndexAsync(cancellationToken)
        .ConfigureAwait(false);
    var key = run.RunId.ToString("D");
    SnapshotRevisionCommitment? committed = null;
    SnapshotRevisionCommitment? legacy = null;
    byte[]? previousBytes = File.Exists(path)
        ? await SecureBoundedFileReader.ReadAsync(
            path,
            _paths.RunsDirectory,
            MaximumSnapshotBytes,
            "snapshot",
            cancellationToken).ConfigureAwait(false)
        : null;

    if (index.Runs.TryGetValue(key, out var existing))
    {
      if (previousBytes is null)
      {
        if (existing.Committed is not null || existing.Legacy is not null)
        {
          throw new InvalidOperationException(
              "A committed execution run snapshot cannot be recreated after deletion.");
        }

        index.Runs.Remove(key);
      }
      else
      {
        var digest = SnapshotDigest(previousBytes);
        if (existing.Pending is { } pending && pending.MatchesDigest(digest))
        {
          committed = pending;
        }
        else if (existing.Committed is { } current && current.MatchesDigest(digest))
        {
          committed = current;
        }
        else if (existing.Legacy is { } migration && migration.MatchesDigest(digest))
        {
          committed = migration;
          legacy = migration;
        }
        else if (existing.Committed is not null || existing.Legacy is not null)
        {
          throw new InvalidOperationException(
              "The execution run snapshot does not match its authenticated revision.");
        }
        else
        {
          index.Runs.Remove(key);
        }
      }
    }
    else if (previousBytes is not null)
    {
      throw new InvalidOperationException(
          "The execution run snapshot has no authenticated revision.");
    }

    if (committed is { } previous && run.Revision != checked(previous.Revision + 1))
    {
      throw new InvalidOperationException(
          "The replacement snapshot revision must follow its authenticated revision.");
    }

    var next = new SnapshotRevisionCommitment(run.Revision, SnapshotDigest(snapshotBytes));
    index.Runs[key] = new SnapshotFormatIndexEntry(
        legacy is null ? committed : null,
        next,
        legacy);
    var pendingIndexWrite = PrepareSnapshotFormatIndex(index);
    var committedRuns = new Dictionary<string, SnapshotFormatIndexEntry>(
        pendingIndexWrite.Index.Runs,
        StringComparer.OrdinalIgnoreCase)
    {
      [key] = new SnapshotFormatIndexEntry(next, Pending: null, Legacy: null)
    };
    var committedIndexWrite = PrepareSnapshotFormatIndex(
        pendingIndexWrite.Index with { Runs = committedRuns });

    await PersistPreparedSnapshotFormatIndexAsync(pendingIndexWrite, cancellationToken)
        .ConfigureAwait(false);

    await WriteBytesAtomicallyAsync(path, snapshotBytes, cancellationToken)
        .ConfigureAwait(false);

    await PersistPreparedSnapshotFormatIndexAsync(committedIndexWrite, cancellationToken)
        .ConfigureAwait(false);
  }

  private SnapshotFormatReconciliation ReconcileSnapshotFormatCommitment(
      Guid runId,
      byte[] snapshotBytes,
      SnapshotFormatIndex index,
      out bool indexChanged)
  {
    indexChanged = false;
    var key = runId.ToString("D");
    if (!index.Runs.TryGetValue(key, out var entry))
    {
      if (File.Exists(SnapshotFormatCommitmentPath(runId)))
      {
        throw new InvalidOperationException(
            "The current-format execution run snapshot has no authenticated revision.");
      }

      return new SnapshotFormatReconciliation(
          IsCurrent: false,
          ExpectedRevision: null,
          IsLegacyMigrationCandidate: false);
    }

    var digest = SnapshotDigest(snapshotBytes);
    if (entry.Pending is { } pending && pending.MatchesDigest(digest))
    {
      index.Runs[key] = new SnapshotFormatIndexEntry(pending, Pending: null);
      indexChanged = true;
      return new SnapshotFormatReconciliation(
          IsCurrent: true,
          pending.Revision,
          IsLegacyMigrationCandidate: false);
    }

    if (entry.Committed is { } committed && committed.MatchesDigest(digest))
    {
      if (entry.Pending is not null)
      {
        index.Runs[key] = new SnapshotFormatIndexEntry(
            committed,
            Pending: null,
            Legacy: null);
        indexChanged = true;
      }

      return new SnapshotFormatReconciliation(
          IsCurrent: true,
          committed.Revision,
          IsLegacyMigrationCandidate: false);
    }

    if (entry.Legacy is { } legacy && legacy.MatchesDigest(digest))
    {
      if (entry.Pending is not null)
      {
        index.Runs[key] = new SnapshotFormatIndexEntry(
            Committed: null,
            Pending: null,
            Legacy: legacy);
        indexChanged = true;
      }

      return new SnapshotFormatReconciliation(
          IsCurrent: false,
          legacy.Revision,
          IsLegacyMigrationCandidate: true);
    }

    throw new InvalidOperationException(
        "The execution run snapshot does not match its authenticated revision.");
  }

  private async Task<SnapshotFormatIndex> ReadAuthenticatedSnapshotFormatIndexAsync(
      CancellationToken cancellationToken)
  {
    var indexPath = SnapshotFormatIndexPath();
    var anchorPath = SnapshotFormatAnchorPath();
    var indexExists = File.Exists(indexPath);
    var anchorExists = File.Exists(anchorPath);
    if (!indexExists && !anchorExists)
    {
      return await EnrollLegacyMigrationCandidatesAsync(cancellationToken)
          .ConfigureAwait(false);
    }

    if (indexExists && !anchorExists)
    {
      throw new InvalidOperationException(
          "The snapshot format index is missing its root freshness anchor.");
    }

    var anchor = await ReadSnapshotFormatAnchorAsync(cancellationToken).ConfigureAwait(false) ??
        throw new InvalidOperationException("The snapshot format freshness anchor is missing.");
    if (!indexExists)
    {
      if (anchor.Committed is null && anchor.Pending is not null)
      {
        File.Delete(anchorPath);
        return await EnrollLegacyMigrationCandidatesAsync(cancellationToken)
            .ConfigureAwait(false);
      }

      throw new InvalidOperationException(
          "The root freshness anchor refers to a missing snapshot format index.");
    }

    var read = await ReadSnapshotFormatIndexFileAsync(cancellationToken).ConfigureAwait(false);
    if (anchor.Pending is { } pending && pending.Matches(read.Index, read.Digest))
    {
      await WriteSnapshotFormatAnchorAsync(
          new SnapshotFormatAnchor(
              CurrentSnapshotFormatVersion,
              pending,
              Pending: null),
          cancellationToken).ConfigureAwait(false);
      return read.Index;
    }

    if (anchor.Committed is { } committed && committed.Matches(read.Index, read.Digest))
    {
      if (anchor.Pending is not null)
      {
        await WriteSnapshotFormatAnchorAsync(
            new SnapshotFormatAnchor(
                CurrentSnapshotFormatVersion,
                committed,
                Pending: null),
            cancellationToken).ConfigureAwait(false);
      }

      return read.Index;
    }

    throw new InvalidOperationException(
        "The snapshot format index does not match its root freshness anchor.");
  }

  private async Task<SnapshotFormatIndex> EnrollLegacyMigrationCandidatesAsync(
      CancellationToken cancellationToken)
  {
    var paths = Directory.EnumerateFiles(
            _paths.RunsDirectory,
            "*.json",
            SearchOption.TopDirectoryOnly)
        .Take(MaximumLegacyMigrationCandidateFiles + 1)
        .ToArray();
    if (paths.Length > MaximumLegacyMigrationCandidateFiles)
    {
      throw new InvalidOperationException(
          "The legacy snapshot migration scan exceeded its file limit.");
    }

    var index = SnapshotFormatIndex.Empty();
    foreach (var path in paths)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var name = Path.GetFileNameWithoutExtension(path);
      if (!Guid.TryParseExact(name, "D", out var runId) ||
          !string.Equals(name, runId.ToString("D"), StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      if (File.Exists(SnapshotFormatCommitmentPath(runId)))
      {
        var current = await TryReadCurrentMigrationCandidateAsync(
            path,
            runId,
            cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
          index.Runs.Add(
              runId.ToString("D"),
              new SnapshotFormatIndexEntry(
                  Committed: current,
                  Pending: null,
                  Legacy: null));
        }
      }
      else
      {
        var legacy = await TryReadLegacyMigrationCandidateAsync(
            path,
            runId,
            cancellationToken).ConfigureAwait(false);
        if (legacy is not null)
        {
          index.Runs.Add(
              runId.ToString("D"),
              new SnapshotFormatIndexEntry(
                  Committed: null,
                  Pending: null,
                  Legacy: legacy));
        }
      }
    }

    return await PersistSnapshotFormatIndexAsync(index, cancellationToken)
        .ConfigureAwait(false);
  }

  private async Task<SnapshotRevisionCommitment?> TryReadCurrentMigrationCandidateAsync(
      string path,
      Guid expectedRunId,
      CancellationToken cancellationToken)
  {
    try
    {
      byte[]? bytes = await TryReadMigrationCandidateBytesAsync(path, cancellationToken)
          .ConfigureAwait(false);
      if (bytes is null)
      {
        return null;
      }

      var commitment = await ReadLegacySnapshotFormatCommitmentAsync(
          expectedRunId,
          cancellationToken).ConfigureAwait(false);
      if (commitment.Revision == long.MaxValue ||
          !commitment.MatchesDigest(SnapshotDigest(bytes)))
      {
        return null;
      }

      var document = JsonNode.Parse(bytes) as JsonObject;
      var protectedPlanApproval = document?[ProtectedPlanApprovalPropertyName]?.DeepClone();
      document?.Remove(ProtectedPlanApprovalPropertyName);
      var run = document?.Deserialize<ExecutionRun>(_snapshotJsonOptions);
      if (run is null ||
          run.RunId != expectedRunId ||
          run.Revision != commitment.Revision ||
          run.Plan is null ||
          run.PlanApproval is null ||
          protectedPlanApproval is null)
      {
        return null;
      }

      ValidateRun(run);
      var envelope = protectedPlanApproval.Deserialize<ProtectedPlanApproval>(
          _snapshotJsonOptions) ??
          throw new JsonException("The protected plan approval is null.");
      run = RestoreProtectedPlanApproval(run, envelope);
      ValidateRun(run);
      return commitment;
    }
    catch (Exception exception) when (
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or CryptographicException
            or FormatException
            or InvalidOperationException
            or ArgumentException)
    {
      return null;
    }
  }

  private async Task<SnapshotRevisionCommitment?> TryReadLegacyMigrationCandidateAsync(
      string path,
      Guid expectedRunId,
      CancellationToken cancellationToken)
  {
    try
    {
      byte[]? bytes = await TryReadMigrationCandidateBytesAsync(path, cancellationToken)
          .ConfigureAwait(false);
      if (bytes is null)
      {
        return null;
      }

      var document = JsonNode.Parse(bytes) as JsonObject;
      if (document is null || document.ContainsKey(ProtectedPlanApprovalPropertyName))
      {
        return null;
      }

      var run = document.Deserialize<ExecutionRun>(_snapshotJsonOptions);
      if (run is null ||
          run.RunId != expectedRunId ||
          run.Plan is null ||
          run.PlanApproval is null)
      {
        return null;
      }

      ValidateRun(run);
      return new SnapshotRevisionCommitment(run.Revision, SnapshotDigest(bytes));
    }
    catch (Exception exception) when (
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or FormatException
            or InvalidOperationException
            or ArgumentException)
    {
      return null;
    }
  }

  private async Task<byte[]?> TryReadMigrationCandidateBytesAsync(
      string path,
      CancellationToken cancellationToken)
  {
    try
    {
      return await SecureBoundedFileReader.ReadAsync(
          path,
          _paths.RunsDirectory,
          checked((int)MaximumLegacyMigrationCandidateBytes),
          "snapshot migration candidate",
          cancellationToken).ConfigureAwait(false);
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException)
    {
      return null;
    }
  }

  private async Task<SnapshotFormatIndexRead> ReadSnapshotFormatIndexFileAsync(
      CancellationToken cancellationToken)
  {
    var protectedIndex = await SecureBoundedFileReader.ReadAsync(
        SnapshotFormatIndexPath(),
        _paths.RunsDirectory,
        MaximumSnapshotFormatIndexBytes,
        "index",
        cancellationToken).ConfigureAwait(false);
    var plaintext = _approvedResourceProtector.Unprotect(
        protectedIndex,
        SnapshotFormatIndexEntropy());
    var index = JsonSerializer.Deserialize<SnapshotFormatIndex>(
        plaintext,
        _snapshotJsonOptions) ??
        throw new JsonException("The snapshot format index is null.");
    if (index.FormatVersion != CurrentSnapshotFormatVersion ||
        index.Generation <= 0 ||
        index.Runs is null)
    {
      throw new InvalidOperationException("The snapshot format index version is unsupported.");
    }

    var validatedRuns = new Dictionary<string, SnapshotFormatIndexEntry>(
        StringComparer.OrdinalIgnoreCase);
    foreach (var (key, entry) in index.Runs)
    {
      if (!Guid.TryParseExact(key, "D", out _) ||
          entry is null ||
          (entry.Committed is null && entry.Pending is null && entry.Legacy is null) ||
          (entry.Committed is not null && entry.Legacy is not null))
      {
        throw new InvalidOperationException("The snapshot format index is malformed.");
      }

      ValidateSnapshotRevisionCommitment(entry.Committed);
      ValidateSnapshotRevisionCommitment(entry.Pending);
      ValidateSnapshotRevisionCommitment(entry.Legacy);
      var previous = entry.Committed ?? entry.Legacy;
      if (entry.Pending is { } pending &&
          previous is { } committed &&
          pending.Revision != checked(committed.Revision + 1))
      {
        throw new InvalidOperationException(
            "The pending snapshot revision does not follow its authenticated revision.");
      }

      validatedRuns.Add(key, entry);
    }

    return new SnapshotFormatIndexRead(
        new SnapshotFormatIndex(
            CurrentSnapshotFormatVersion,
            index.Generation,
            validatedRuns),
        SnapshotDigest(plaintext));
  }

  private async Task<SnapshotFormatIndex> PersistSnapshotFormatIndexAsync(
      SnapshotFormatIndex index,
      CancellationToken cancellationToken) =>
      await PersistPreparedSnapshotFormatIndexAsync(
          PrepareSnapshotFormatIndex(index),
          cancellationToken).ConfigureAwait(false);

  private PreparedSnapshotFormatIndex PrepareSnapshotFormatIndex(
      SnapshotFormatIndex index)
  {
    var nextIndex = index with { Generation = checked(index.Generation + 1) };
    var plaintext = JsonSerializer.SerializeToUtf8Bytes(nextIndex, _snapshotJsonOptions);
    var protectedIndex = _approvedResourceProtector.Protect(
        plaintext,
        SnapshotFormatIndexEntropy());
    SecureBoundedFileReader.EnsureLengthWithinMaximum(
        protectedIndex.LongLength,
        MaximumSnapshotFormatIndexBytes,
        "index");
    var next = new SnapshotIndexCommitment(nextIndex.Generation, SnapshotDigest(plaintext));
    return new PreparedSnapshotFormatIndex(nextIndex, next, protectedIndex);
  }

  private async Task<SnapshotFormatIndex> PersistPreparedSnapshotFormatIndexAsync(
      PreparedSnapshotFormatIndex prepared,
      CancellationToken cancellationToken)
  {
    var anchor = await ReadSnapshotFormatAnchorAsync(cancellationToken).ConfigureAwait(false);
    if (prepared.Index.Generation == 1 && anchor is not null)
    {
      throw new InvalidOperationException(
          "A new snapshot format index cannot replace an existing freshness anchor.");
    }

    var committed = anchor?.Committed;
    await WriteSnapshotFormatAnchorAsync(
        new SnapshotFormatAnchor(
            CurrentSnapshotFormatVersion,
            committed,
            prepared.Commitment),
        cancellationToken).ConfigureAwait(false);

    await WriteBytesAtomicallyAsync(
        SnapshotFormatIndexPath(),
        prepared.ProtectedBytes,
        cancellationToken).ConfigureAwait(false);

    await WriteSnapshotFormatAnchorAsync(
        new SnapshotFormatAnchor(
            CurrentSnapshotFormatVersion,
            prepared.Commitment,
            Pending: null),
        cancellationToken).ConfigureAwait(false);
    return prepared.Index;
  }

  private async Task<SnapshotFormatAnchor?> ReadSnapshotFormatAnchorAsync(
      CancellationToken cancellationToken)
  {
    var path = SnapshotFormatAnchorPath();
    if (!File.Exists(path))
    {
      return null;
    }

    var protectedAnchor = await SecureBoundedFileReader.ReadAsync(
        path,
        _paths.Root,
        MaximumSnapshotFormatAnchorBytes,
        "anchor",
        cancellationToken).ConfigureAwait(false);
    var plaintext = _approvedResourceProtector.Unprotect(
        protectedAnchor,
        SnapshotFormatAnchorEntropy());
    var anchor = JsonSerializer.Deserialize<SnapshotFormatAnchor>(
        plaintext,
        _snapshotJsonOptions) ??
        throw new JsonException("The snapshot format freshness anchor is null.");
    if (anchor.FormatVersion != CurrentSnapshotFormatVersion ||
        (anchor.Committed is null && anchor.Pending is null))
    {
      throw new InvalidOperationException("The snapshot format freshness anchor is malformed.");
    }

    ValidateSnapshotIndexCommitment(anchor.Committed);
    ValidateSnapshotIndexCommitment(anchor.Pending);
    if (anchor.Pending is { } pending &&
        pending.Generation != checked((anchor.Committed?.Generation ?? 0) + 1))
    {
      throw new InvalidOperationException(
          "The pending snapshot index generation does not follow its committed generation.");
    }

    return anchor;
  }

  private async Task WriteSnapshotFormatAnchorAsync(
      SnapshotFormatAnchor anchor,
      CancellationToken cancellationToken)
  {
    var plaintext = JsonSerializer.SerializeToUtf8Bytes(anchor, _snapshotJsonOptions);
    var protectedAnchor = _approvedResourceProtector.Protect(
        plaintext,
        SnapshotFormatAnchorEntropy());
    SecureBoundedFileReader.EnsureLengthWithinMaximum(
        protectedAnchor.LongLength,
        MaximumSnapshotFormatAnchorBytes,
        "anchor");
    await WriteBytesAtomicallyAsync(
        SnapshotFormatAnchorPath(),
        protectedAnchor,
        cancellationToken).ConfigureAwait(false);
  }

  private async Task<SnapshotRevisionCommitment> ReadLegacySnapshotFormatCommitmentAsync(
      Guid runId,
    CancellationToken cancellationToken)
  {
    var path = SnapshotFormatCommitmentPath(runId);
    var protectedCommitment = await SecureBoundedFileReader.ReadAsync(
        path,
        _paths.RunsDirectory,
        MaximumSnapshotFormatCommitmentBytes,
        "snapshot format commitment",
        cancellationToken).ConfigureAwait(false);

    var plaintext = _approvedResourceProtector.Unprotect(
        protectedCommitment,
        SnapshotFormatEntropy(runId));
    var commitment = JsonSerializer.Deserialize<LegacySnapshotFormatCommitment>(
        plaintext,
        _snapshotJsonOptions) ??
        throw new JsonException("The snapshot format commitment is null.");
    if (commitment.FormatVersion != CurrentSnapshotFormatVersion ||
        commitment.RunId != runId)
    {
      throw new InvalidOperationException(
          "The snapshot format commitment does not match its execution run.");
    }

    var revisionCommitment = new SnapshotRevisionCommitment(
        commitment.Revision,
        commitment.Digest);
    ValidateSnapshotRevisionCommitment(revisionCommitment);
    return revisionCommitment;
  }

  private static void ValidateSnapshotRevisionCommitment(
      SnapshotRevisionCommitment? commitment)
  {
    if (commitment is null)
    {
      return;
    }

    if (commitment.Revision < 0 || commitment.Revision == long.MaxValue)
    {
      throw new InvalidOperationException(
          "The snapshot format index contains an invalid revision.");
    }

    ValidateSha256(commitment.Digest, "snapshot digest");
  }

  private static void ValidateSnapshotIndexCommitment(SnapshotIndexCommitment? commitment)
  {
    if (commitment is null)
    {
      return;
    }

    if (commitment.Generation <= 0)
    {
      throw new InvalidOperationException(
          "The snapshot format anchor contains an invalid generation.");
    }

    ValidateSha256(commitment.Digest, "snapshot index digest");
  }

  private static string SnapshotDigest(byte[] snapshotBytes) =>
      Convert.ToHexString(SHA256.HashData(snapshotBytes));

  private byte[] SerializeSnapshot(ExecutionRun run)
  {
    var publicRun = Redact(run);
    var document = JsonSerializer.SerializeToNode(publicRun, _snapshotJsonOptions)?.AsObject() ??
        throw new JsonException("The execution run snapshot could not be serialized.");
    if (run.Plan is not null && run.PlanApproval is not null)
    {
      var binding = new PlanApprovalBinding(
          run.RunId,
          run.Revision,
          run.Plan.PlanId,
          run.Plan.Fingerprint,
          run.PlanApproval.InitialPlanFingerprint);
      var commitment = new TrustedPlanApproval(
          binding,
          run.Plan,
          run.PlanApproval,
          publicRun.Plan!,
          publicRun.PlanApproval!);
      var plaintext = JsonSerializer.SerializeToUtf8Bytes(commitment, _snapshotJsonOptions);
      var protectedData = _approvedResourceProtector.Protect(
          plaintext,
          binding.Entropy());
      document[ProtectedPlanApprovalPropertyName] = JsonSerializer.SerializeToNode(
          new ProtectedPlanApproval(
              binding,
              Convert.ToBase64String(protectedData)),
          _snapshotJsonOptions);
    }

    return JsonSerializer.SerializeToUtf8Bytes(document, _snapshotJsonOptions);
  }

  private ExecutionRun RestoreProtectedPlanApproval(
      ExecutionRun publicRun,
      ProtectedPlanApproval envelope)
  {
    var binding = envelope.Binding;
    if (binding.RunId != publicRun.RunId ||
        binding.Revision != publicRun.Revision ||
        binding.PlanId == Guid.Empty)
    {
      throw new InvalidOperationException(
          "The protected plan approval does not match its execution run snapshot.");
    }

    ValidateSha256(binding.PlanFingerprint, "protected plan fingerprint");
    ValidateSha256(binding.ApprovalFingerprint, "protected approval fingerprint");
    var protectedData = Convert.FromBase64String(envelope.ProtectedPayload);
    var plaintext = _approvedResourceProtector.Unprotect(
        protectedData,
        binding.Entropy());
    var commitment = JsonSerializer.Deserialize<TrustedPlanApproval>(
        plaintext,
        _snapshotJsonOptions) ??
        throw new JsonException("The protected plan approval payload is null.");
    if (!commitment.Binding.Matches(binding) ||
        commitment.Plan.PlanId != binding.PlanId ||
        !FixedEquals(commitment.Plan.Fingerprint, binding.PlanFingerprint) ||
        !FixedEquals(
            commitment.Approval.InitialPlanFingerprint,
            binding.ApprovalFingerprint))
    {
      throw new InvalidOperationException(
          "The protected plan approval payload does not match its snapshot envelope.");
    }

    if (publicRun.Plan is null || publicRun.PlanApproval is null ||
        !JsonEquivalent(publicRun.Plan, commitment.PublicPlan) ||
        !JsonEquivalent(publicRun.PlanApproval, commitment.PublicApproval))
    {
      throw new InvalidOperationException(
          "The public execution plan does not match its protected approval commitment.");
    }

    return publicRun with
    {
      Plan = commitment.Plan,
      PlanApproval = commitment.Approval
    };
  }

  private bool JsonEquivalent<T>(T first, T second) => JsonNode.DeepEquals(
      JsonSerializer.SerializeToNode(first, _snapshotJsonOptions),
      JsonSerializer.SerializeToNode(second, _snapshotJsonOptions));

  private static byte[] SnapshotFormatEntropy(Guid runId) => Encoding.UTF8.GetBytes(
      $"WDEM\0snapshot-format\0{runId:D}");

  private static byte[] SnapshotFormatIndexEntropy() => Encoding.UTF8.GetBytes(
      "WDEM\0snapshot-format-index\0v1");

  private static byte[] SnapshotFormatAnchorEntropy() => Encoding.UTF8.GetBytes(
      "WDEM\0snapshot-format-anchor\0v1");

  private ExecutionRun Redact(ExecutionRun run) => run with
  {
    ProfileSourcePath = _redactor.Redact(run.ProfileSourcePath),
    ProfileId = _redactor.Redact(run.ProfileId),
    ProfileVersion = _redactor.Redact(run.ProfileVersion),
    SelectedOptionalResourceIds = run.SelectedOptionalResourceIds
        .Select(_redactor.Redact)
        .ToHashSet(StringComparer.OrdinalIgnoreCase),
    AcknowledgedRestartResourceIds = run.AcknowledgedRestartResourceIds
        .Select(_redactor.Redact)
        .ToHashSet(StringComparer.OrdinalIgnoreCase),
    Machine = run.Machine with
    {
      OperatingSystem = _redactor.Redact(run.Machine.OperatingSystem),
      Architecture = _redactor.Redact(run.Machine.Architecture),
      ComputerName = _redactor.Redact(run.Machine.ComputerName),
      UserName = _redactor.Redact(run.Machine.UserName)
    },
    Graph = run.Graph is null ? null : Redact(run.Graph),
    Plan = run.Plan is null ? null : Redact(run.Plan),
    PlanApproval = run.PlanApproval is null ? null : Redact(run.PlanApproval),
    ResourceResults = run.ResourceResults.ToDictionary(
        pair => _redactor.Redact(pair.Key),
        pair => Redact(pair.Value),
        StringComparer.OrdinalIgnoreCase),
    RestartReasons = run.RestartReasons.Select(_redactor.Redact).ToArray()
  };

  private PlanApproval Redact(PlanApproval approval) => approval with
  {
    DeferredAuthorizations = approval.DeferredAuthorizations.Select(proof => proof with
    {
      ResourceId = _redactor.Redact(proof.ResourceId),
      ResourceType = _redactor.Redact(proof.ResourceType),
      ProviderName = _redactor.Redact(proof.ProviderName),
      Dependencies = proof.Dependencies.Select(_redactor.Redact).ToArray()
    }).ToArray()
  };

  private ResourceResult Redact(ResourceResult result) => result with
  {
    ResourceId = _redactor.Redact(result.ResourceId),
    DetectedBefore = result.DetectedBefore is null ? null : Redact(result.DetectedBefore),
    DetectedAfter = result.DetectedAfter is null ? null : Redact(result.DetectedAfter),
    Message = result.Message is null ? null : _redactor.Redact(result.Message),
    Error = result.Error is null ? null : _redactor.Redact(result.Error),
    StepResults = result.StepResults.Select(Redact).ToArray()
  };

  private StepResult Redact(StepResult result) => result with
  {
    StepId = _redactor.Redact(result.StepId),
    Name = _redactor.Redact(result.Name),
    Error = result.Error is null ? null : _redactor.Redact(result.Error)
  };

  private DetectedState Redact(DetectedState state) => state with
  {
    ResourceId = _redactor.Redact(state.ResourceId),
    Version = state.Version is null ? null : _redactor.Redact(state.Version),
    ConfigurationHash = state.ConfigurationHash is null
        ? null
        : _redactor.Redact(state.ConfigurationHash),
    Evidence = state.Evidence.ToDictionary(
        pair => _redactor.Redact(pair.Key),
        pair => _redactor.Redact(pair.Value),
        StringComparer.OrdinalIgnoreCase),
    Error = state.Error is null ? null : _redactor.Redact(state.Error),
    StructuredError = state.StructuredError is null ? null : _redactor.Redact(state.StructuredError)
  };

  private ResourceGraph Redact(ResourceGraph graph) => graph with
  {
    Nodes = graph.Nodes.ToDictionary(
        pair => _redactor.Redact(pair.Key),
        pair => pair.Value with
        {
          Definition = Redact(pair.Value.Definition),
          RequiredBy = pair.Value.RequiredBy.Select(_redactor.Redact)
              .ToHashSet(StringComparer.OrdinalIgnoreCase)
        },
        StringComparer.OrdinalIgnoreCase),
    TopologicalLayers = graph.TopologicalLayers.Select(layer => layer with
    {
      ResourceIds = layer.ResourceIds.Select(_redactor.Redact).ToArray()
    }).ToArray()
  };

  private ExecutionPlan Redact(ExecutionPlan plan) => plan with
  {
    Fingerprint = _redactor.Redact(plan.Fingerprint),
    ProfileId = _redactor.Redact(plan.ProfileId),
    ProfileVersion = _redactor.Redact(plan.ProfileVersion),
    Layers = plan.Layers.Select(layer => layer with
    {
      ResourceIds = layer.ResourceIds.Select(_redactor.Redact).ToArray()
    }).ToArray(),
    Resources = plan.Resources.Select(resource => resource with
    {
      Definition = Redact(resource.Definition),
      Dependencies = resource.Dependencies.Select(_redactor.Redact).ToArray(),
      ResourcePlan = Redact(resource.ResourcePlan),
      Reason = resource.Reason is null ? null : _redactor.Redact(resource.Reason),
      BlockedBy = resource.BlockedBy.Select(_redactor.Redact).ToArray(),
      Diagnostics = resource.Diagnostics.Select(_redactor.Redact).ToArray()
    }).ToArray(),
    Errors = plan.Errors.Select(_redactor.Redact).ToArray()
  };

  private ResourcePlan Redact(ResourcePlan plan) => plan with
  {
    ResourceId = _redactor.Redact(plan.ResourceId),
    ResourceType = _redactor.Redact(plan.ResourceType),
    ProviderName = _redactor.Redact(plan.ProviderName),
    DesiredStateFingerprint = _redactor.Redact(plan.DesiredStateFingerprint),
    Steps = plan.Steps.Select(step => step with
    {
      Id = _redactor.Redact(step.Id),
      Description = _redactor.Redact(step.Description),
      Reason = step.Reason is null ? null : _redactor.Redact(step.Reason)
    }).ToArray(),
    Error = plan.Error is null ? null : _redactor.Redact(plan.Error),
    StructuredErrors = plan.StructuredErrors.Select(_redactor.Redact).ToArray()
  };

  private ResourceDefinition Redact(ResourceDefinition definition)
  {
    var presentation = ResourceDefinitionPresentationRedactor.Redact(definition, _redactor);
    return presentation with
    {
      Id = _redactor.Redact(definition.Id),
      Type = _redactor.Redact(definition.Type),
      Provider = _redactor.Redact(definition.Provider),
      VersionConstraint = definition.VersionConstraint is null
          ? null
          : _redactor.Redact(definition.VersionConstraint),
      PreferredVersion = definition.PreferredVersion is null
          ? null
          : _redactor.Redact(definition.PreferredVersion),
      ProfileSourcePath = definition.ProfileSourcePath is null
          ? null
          : _redactor.Redact(definition.ProfileSourcePath),
      Dependencies = definition.Dependencies.Select(_redactor.Redact).ToArray(),
      Parameters = definition.Parameters.ToDictionary(
          pair => _redactor.Redact(pair.Key),
          pair => _redactor.RedactNamedValue(pair.Key, pair.Value),
          StringComparer.OrdinalIgnoreCase)
    };
  }

  private readonly record struct LogIndexState(
      long Count,
      long LastSequence,
      long IndexedLength);

  private readonly record struct LogIndexFingerprint(long Length, long LastWriteTicks);

  private readonly record struct ValidatedLogIndex(
      LogIndexFingerprint Fingerprint,
      LogIndexState State);

  private readonly record struct LogIndexRecord(
      long Sequence,
      long StartOffset,
      long EndOffset);

  private sealed record ApprovedResourceEnvelope(
      Guid RunId,
      IReadOnlyList<ProtectedApprovedResource> Resources);

  private sealed record ProtectedApprovedResource(
      string ResourceId,
      string Fingerprint,
      string ProtectedPayload,
      IReadOnlyList<string>? ClaimedPlanFingerprints = null);

  private sealed record ProtectedPlanApproval(
      PlanApprovalBinding Binding,
      string ProtectedPayload);

  private readonly record struct PlanApprovalBinding(
      Guid RunId,
      long Revision,
      Guid PlanId,
      string PlanFingerprint,
      string ApprovalFingerprint)
  {
    public byte[] Entropy() => Encoding.UTF8.GetBytes(
        $"WDEM\0plan-approval\0{RunId:D}\0{Revision}\0{PlanId:D}\0" +
        $"{PlanFingerprint}\0{ApprovalFingerprint}");

    public bool Matches(PlanApprovalBinding other) =>
        RunId == other.RunId &&
        Revision == other.Revision &&
        PlanId == other.PlanId &&
        FixedEquals(PlanFingerprint, other.PlanFingerprint) &&
        FixedEquals(ApprovalFingerprint, other.ApprovalFingerprint);
  }

  private sealed record TrustedPlanApproval(
      PlanApprovalBinding Binding,
      ExecutionPlan Plan,
      PlanApproval Approval,
      ExecutionPlan PublicPlan,
      PlanApproval PublicApproval);

  private sealed record LegacySnapshotFormatCommitment(
      int FormatVersion,
      Guid RunId,
      long Revision,
      string Digest);

  private sealed record SnapshotFormatIndex(
      int FormatVersion,
      long Generation,
      Dictionary<string, SnapshotFormatIndexEntry> Runs)
  {
    public static SnapshotFormatIndex Empty() => new(
        CurrentSnapshotFormatVersion,
        Generation: 0,
        new Dictionary<string, SnapshotFormatIndexEntry>(StringComparer.OrdinalIgnoreCase));
  }

  private sealed record SnapshotFormatAnchor(
      int FormatVersion,
      SnapshotIndexCommitment? Committed,
      SnapshotIndexCommitment? Pending);

  private sealed record SnapshotIndexCommitment(long Generation, string Digest)
  {
    public bool Matches(SnapshotFormatIndex index, string digest) =>
        Generation == index.Generation && FixedEquals(Digest, digest);
  }

  private readonly record struct SnapshotFormatIndexRead(
      SnapshotFormatIndex Index,
      string Digest);

  private readonly record struct PreparedSnapshotFormatIndex(
      SnapshotFormatIndex Index,
      SnapshotIndexCommitment Commitment,
      byte[] ProtectedBytes);

  private sealed record SnapshotFormatIndexEntry(
      SnapshotRevisionCommitment? Committed,
      SnapshotRevisionCommitment? Pending,
      SnapshotRevisionCommitment? Legacy = null);

  private sealed record SnapshotRevisionCommitment(long Revision, string Digest)
  {
    public bool MatchesDigest(string digest) => FixedEquals(Digest, digest);
  }

  private readonly record struct SnapshotFormatReconciliation(
      bool IsCurrent,
      long? ExpectedRevision,
      bool IsLegacyMigrationCandidate);

  private sealed class BatchSnapshotFormatState(SnapshotFormatIndex index)
  {
    public SnapshotFormatIndex Index { get; } = index;
    public bool IsChanged { get; set; }
  }

  private sealed class SecureDirectoryScope : IDisposable
  {
    private readonly List<SecureDirectoryLease> _leases = [];

    public void Add(SecureDirectoryLease lease) => _leases.Add(lease);

    public void Dispose()
    {
      for (int index = _leases.Count - 1; index >= 0; index--)
      {
        _leases[index].Dispose();
      }
    }
  }

  private sealed class OwnedAsyncLease(
      IAsyncDisposable inner,
      SecureDirectoryScope directoryScope) : IAsyncDisposable
  {
    public async ValueTask DisposeAsync()
    {
      try
      {
        await inner.DisposeAsync().ConfigureAwait(false);
      }
      finally
      {
        directoryScope.Dispose();
      }
    }
  }

  private sealed class ReadOnlyStringSetJsonConverter : JsonConverter<IReadOnlySet<string>>
  {
    public override IReadOnlySet<string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
      if (reader.TokenType != JsonTokenType.StartArray)
      {
        throw new JsonException("A string array was expected.");
      }

      var values = new List<string>();
      while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
      {
        if (reader.TokenType != JsonTokenType.String)
        {
          throw new JsonException("A string set cannot contain non-string values.");
        }

        values.Add(reader.GetString()!);
      }

      if (reader.TokenType != JsonTokenType.EndArray)
      {
        throw new JsonException("The string array was not terminated.");
      }

      return values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlySet<string> value,
        JsonSerializerOptions options)
    {
      writer.WriteStartArray();
      foreach (var item in value)
      {
        writer.WriteStringValue(item);
      }

      writer.WriteEndArray();
    }
  }
}
