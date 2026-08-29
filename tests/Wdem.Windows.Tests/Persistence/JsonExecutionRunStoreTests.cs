using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Core.Versions;
using Wdem.Windows.Persistence;
using Wdem.Windows.Security;
using Xunit;

namespace Wdem.Windows.Tests.Persistence;

public sealed class JsonExecutionRunStoreTests : IDisposable
{
  private readonly string _directory = Path.Combine(
      Path.GetTempPath(), $"wdem-run-store-{Guid.NewGuid():N}");
  private readonly JsonExecutionRunStore _store;

  public JsonExecutionRunStoreTests()
  {
    _store = new JsonExecutionRunStore(new WdemDataPaths(_directory), new LogRedactor());
  }

  [Fact]
  public void Diagnostics_AreAvailableThroughStoreContract()
  {
    IExecutionRunStore store = _store;

    Assert.Empty(store.Diagnostics);
  }

  [Fact]
  public async Task CreateAsync_ProtectsOriginalApprovedResourceAndKeepsPublicSnapshotRedacted()
  {
    var protector = new DeterministicApprovedResourceProtector();
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        protector);
    var run = SampleRun();
    var planned = Assert.Single(run.Plan!.Resources);
    var original = planned.Definition with
    {
      PrivilegeRequirement = PrivilegeRequirement.Administrator,
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["password"] = "original-secret"
      }
    };
    var executablePlan = planned.ResourcePlan with
    {
      Steps =
      [
        planned.ResourcePlan.Steps.Single() with
        {
          PrivilegeRequirement = PrivilegeRequirement.Administrator
        }
      ]
    };
    run = run with
    {
      Plan = run.Plan with
      {
        Resources =
        [
          planned with
          {
            Definition = original,
            ResourcePlan = executablePlan,
            RequiresElevation = true,
            Risk = PlanRisk.Elevated
          }
        ]
      }
    };

    await store.CreateAsync(run, CancellationToken.None);

    var approved = await ((IApprovedResourceStore)store).GetApprovedResourceAsync(
        run.RunId,
        original.Id,
        CancellationToken.None);
    var publicSnapshot = await File.ReadAllTextAsync(store.SnapshotPath(run.RunId));
    var protectedSnapshot = await File.ReadAllTextAsync(store.ApprovedResourcesPath(run.RunId));
    Assert.NotNull(approved);
    Assert.Equal("original-secret", approved.Definition.Parameters["password"]);
    Assert.Equal(executablePlan.ResourceId, approved.Plan.ResourceId);
    Assert.Equal(executablePlan.ResourceType, approved.Plan.ResourceType);
    Assert.Equal(executablePlan.ProviderName, approved.Plan.ProviderName);
    Assert.Equal(executablePlan.DesiredStateFingerprint, approved.Plan.DesiredStateFingerprint);
    Assert.Equal(executablePlan.Steps.Single(), approved.Plan.Steps.Single());
    Assert.Equal(
        ApprovedResourceFingerprint.Create(original, executablePlan),
        approved.Fingerprint);
    Assert.DoesNotContain("original-secret", publicSnapshot, StringComparison.Ordinal);
    Assert.DoesNotContain("original-secret", protectedSnapshot, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GetApprovedResourceAsync_TamperedCiphertextIsRejected()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("tamper-secret");
    await store.CreateAsync(run, CancellationToken.None);
    var path = store.ApprovedResourcesPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    var payload = document["resources"]![0]!["protectedPayload"]!.GetValue<string>();
    document["resources"]![0]!["protectedPayload"] =
        $"{(payload[0] == 'A' ? 'B' : 'A')}{payload[1..]}";
    await File.WriteAllTextAsync(path, document.ToJsonString());

    var approved = await store.GetApprovedResourceAsync(
        run.RunId,
        "git",
        CancellationToken.None);

    Assert.Null(approved);
    Assert.Contains(store.Diagnostics, error => error.Code == WdemErrorCode.PermissionError);
  }

  [Fact]
  public async Task GetApprovedResourceAsync_DifferentUserProtectorIsRejected()
  {
    var paths = new WdemDataPaths(_directory);
    var run = ElevatedRunWithSecret("identity-secret");
    var creator = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector("first-user"));
    await creator.CreateAsync(run, CancellationToken.None);
    var otherUser = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector("second-user"));

    var approved = await otherUser.GetApprovedResourceAsync(
        run.RunId,
        "git",
        CancellationToken.None);

    Assert.Null(approved);
  }

  [Fact]
  public async Task SaveAsync_TerminalRunRemovesApprovedResourceSnapshot()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("terminal-secret");
    await store.CreateAsync(run, CancellationToken.None);

    await store.SaveAsync(
        run with
        {
          State = ExecutionState.Completed,
          Outcome = ExecutionOutcome.Succeeded,
          EndedAtUtc = DateTimeOffset.UtcNow
        },
        CancellationToken.None);

    Assert.False(File.Exists(store.ApprovedResourcesPath(run.RunId)));
  }

  [Fact]
  public async Task SaveAsync_TerminalCleanupFailurePersistsRunAndRetriesCleanup()
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var run = ElevatedRunWithSecret("cleanup-secret");
    await store.CreateAsync(run, CancellationToken.None);
    var approvedPath = store.ApprovedResourcesPath(run.RunId);
    ExecutionRun saved;

    await using (var sidecarLock = new FileStream(
                     approvedPath,
                     FileMode.Open,
                     FileAccess.Read,
                     FileShare.None))
    {
      saved = await store.SaveAsync(
          run with
          {
            State = ExecutionState.Completed,
            Outcome = ExecutionOutcome.Succeeded,
            EndedAtUtc = DateTimeOffset.UtcNow
          },
          CancellationToken.None);

      Assert.Equal(ExecutionState.Completed, saved.State);
      Assert.True(File.Exists(approvedPath));
      var diagnostic = Assert.Single(store.Diagnostics);
      Assert.Equal(WdemErrorCode.PermissionError, diagnostic.Code);
      Assert.True(diagnostic.IsRetryable);
      Assert.IsType<IOException>(diagnostic.UnderlyingException);
    }

    var restored = await store.GetAsync(run.RunId, CancellationToken.None);

    Assert.NotNull(restored);
    Assert.Equal(ExecutionState.Completed, restored.State);
    Assert.False(File.Exists(approvedPath));
  }

  [Fact]
  public async Task ElevatedWorker_NewStoreInstanceExecutesWithProtectedOriginalValues()
  {
    var paths = new WdemDataPaths(_directory);
    var run = ElevatedRunWithSecret("worker-secret");
    var creator = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    await creator.CreateAsync(run, CancellationToken.None);
    var hostStore = new JsonExecutionRunStore(
        paths,
        new LogRedactor(),
        new DeterministicApprovedResourceProtector());
    var provider = new OriginalValueRecordingProvider();
    var planned = run.Plan!.Resources.Single();
    var worker = new ElevatedResourceWorker(
        hostStore,
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            run.RunId,
            planned.Definition.Id,
            ApprovedResourceFingerprint.Create(
                planned.Definition,
                planned.ResourcePlan),
            "pipe"),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Equal("worker-secret", provider.AppliedResource!.Parameters["password"]);
  }

  [Fact]
  public void CurrentUserApprovedResourceProtector_RoundTripsWithBoundEntropy()
  {
    var protector = new CurrentUserApprovedResourceProtector();
    var plaintext = System.Text.Encoding.UTF8.GetBytes("dpapi-secret");
    var entropy = SHA256.HashData("run-resource-fingerprint"u8.ToArray());

    var protectedData = protector.Protect(plaintext, entropy);
    var restored = protector.Unprotect(protectedData, entropy);

    Assert.False(plaintext.SequenceEqual(protectedData));
    Assert.Equal(plaintext, restored);
    Assert.Throws<CryptographicException>(() => protector.Unprotect(
        protectedData,
        SHA256.HashData("different-binding"u8.ToArray())));
  }

  [Fact]
  public async Task TryAcquireRecoveryOperationAsync_PropagatesAndRecordsUnexpectedIoFailure()
  {
    var runId = Guid.NewGuid();
    var expected = new IOException("access denied", unchecked((int)0x80070005));
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        _ => throw expected);

    var actual = await Assert.ThrowsAsync<IOException>(
        () => store.TryAcquireRecoveryOperationAsync(runId, CancellationToken.None));

    Assert.Same(expected, actual);
    var diagnostic = Assert.Single(store.Diagnostics);
    Assert.Equal(WdemErrorCode.DetectionError, diagnostic.Code);
    Assert.Contains(runId.ToString("D"), diagnostic.Detail, StringComparison.Ordinal);
    Assert.Contains(expected.Message, diagnostic.Detail, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(unchecked((int)0x80040020))]
  [InlineData(unchecked((int)0x80040021))]
  public async Task TryAcquireRecoveryOperationAsync_PropagatesBusyLowWordFromOtherFacility(
      int hresult)
  {
    var runId = Guid.NewGuid();
    var expected = new IOException("interface failure", hresult);
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        _ => throw expected);

    var actual = await Assert.ThrowsAsync<IOException>(
        () => store.TryAcquireRecoveryOperationAsync(runId, CancellationToken.None));

    Assert.Same(expected, actual);
    var diagnostic = Assert.Single(store.Diagnostics);
    Assert.Contains(runId.ToString("D"), diagnostic.Detail, StringComparison.Ordinal);
    Assert.Contains(expected.Message, diagnostic.Detail, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(unchecked((int)0x80070020))]
  [InlineData(unchecked((int)0x80070021))]
  public async Task TryAcquireRecoveryOperationAsync_ReturnsBusyForSharingAndLockViolations(
      int hresult)
  {
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        _ => throw new IOException("busy", hresult));

    var operation = await store.TryAcquireRecoveryOperationAsync(
        Guid.NewGuid(),
        CancellationToken.None);

    Assert.Null(operation);
    Assert.Empty(store.Diagnostics);
  }

  [Fact]
  public async Task CreateAndAppendLog_RoundTripsRunAndNeverPersistsSecrets()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(
        run.RunId,
        new RunLogEntry(
            1,
            DateTimeOffset.UtcNow,
            ProviderLogLevel.Info,
            "git",
            "git:install",
            "Authorization: Bearer abc.def.ghi",
            new StructuredError(
                WdemErrorCode.ProviderError,
                "token=summary-secret",
                "password=detail-secret")),
        CancellationToken.None);

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    var log = await File.ReadAllTextAsync(_store.LogPath(run.RunId));

    Assert.True(
        restored is not null,
        string.Join(Environment.NewLine, _store.Diagnostics.Select(error => error.Detail)));
    var page = await _store.ReadLogPageAsync(run.RunId, 0, 10, CancellationToken.None);
    Assert.Equal(run.ProfileId, restored.ProfileId);
    Assert.True(restored.ResourceResults.ContainsKey("GIT"));
    Assert.Equal("git", Assert.Single(restored.Graph!.Nodes).Key);
    Assert.Equal("git", Assert.Single(restored.Plan!.Resources).Definition.Id);
    Assert.Equal("2.52.0", restored.ResourceResults["git"].DetectedBefore!.Version);
    Assert.DoesNotContain("abc.def.ghi", log, StringComparison.Ordinal);
    Assert.DoesNotContain("summary-secret", log, StringComparison.Ordinal);
    Assert.DoesNotContain("detail-secret", log, StringComparison.Ordinal);
    Assert.Equal("Authorization: Bearer ***", page[0].Message);
    Assert.Equal("token=[REDACTED]", page[0].Error!.Summary);
  }

  [Fact]
  public async Task CreateAsync_RoundTripsRecoveryClaimWithoutWeakeningRedaction()
  {
    var claimId = Guid.NewGuid();
    var claimedAt = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        new MutableTimeProvider(claimedAt));
    var run = SampleRun() with
    {
      Revision = 7,
      RecoveryClaimId = claimId,
      RecoveryClaimedAtUtc = claimedAt,
      AcknowledgedRestartResourceIds = new HashSet<string>(
          ["token=acknowledgement-secret"],
          StringComparer.OrdinalIgnoreCase),
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = SampleResourceResult() with
        {
          DetectedBefore = MissingEvidence("password=evidence-secret")
        }
      }
    };

    await store.CreateAsync(run, CancellationToken.None);

    var disk = await File.ReadAllTextAsync(store.SnapshotPath(run.RunId));
    var restored = await store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Equal(7, restored!.Revision);
    Assert.Equal(claimId, restored.RecoveryClaimId);
    Assert.Equal(claimedAt, restored.RecoveryClaimedAtUtc);
    Assert.Contains("token=***", restored.AcknowledgedRestartResourceIds);
    Assert.Equal(
        "password=***",
        restored.ResourceResults["git"].DetectedBefore!.Evidence["detail"]);
    Assert.DoesNotContain("acknowledgement-secret", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("evidence-secret", disk, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateAsync_RoundTripsRecoveryAssociation()
  {
    var priorRunId = Guid.NewGuid();
    var run = SampleRun() with
    {
      RetriedFromRunId = priorRunId,
      RecoveredFromRunId = priorRunId
    };

    await _store.CreateAsync(run, CancellationToken.None);

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Equal(priorRunId, restored!.RetriedFromRunId);
    Assert.Equal(priorRunId, restored.RecoveredFromRunId);
  }

  [Fact]
  public async Task GetAsync_OldSnapshotWithoutRecoveryAssociationDefaultsToNull()
  {
    var run = SampleRun() with { RetriedFromRunId = Guid.NewGuid() };
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    Assert.True(document.Remove("recoveredFromRunId"));
    await File.WriteAllTextAsync(snapshotPath, document.ToJsonString());

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.NotNull(restored);
    Assert.Equal(run.RetriedFromRunId, restored.RetriedFromRunId);
    Assert.Null(restored.RecoveredFromRunId);
  }

  [Theory]
  [InlineData(-1, false, false)]
  [InlineData(0, true, false)]
  [InlineData(0, false, true)]
  public async Task CreateAsync_RejectsInvalidRecoveryClaimMetadata(
      long revision,
      bool includeClaimId,
      bool includeClaimedAt)
  {
    var run = SampleRun() with
    {
      Revision = revision,
      RecoveryClaimId = includeClaimId ? Guid.NewGuid() : null,
      RecoveryClaimedAtUtc = includeClaimedAt ? DateTimeOffset.UtcNow : null
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
    Assert.False(File.Exists(_store.SnapshotPath(run.RunId)));
  }

  [Fact]
  public async Task CreateAsync_RejectsRecoveryClaimTimestampBeyondClockSkew()
  {
    var now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    var clock = new MutableTimeProvider(now);
    var store = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor(),
        clock);
    var valid = SampleRun() with
    {
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = now.AddMinutes(5)
    };
    await store.CreateAsync(valid, CancellationToken.None);
    var invalid = SampleRun() with
    {
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = DateTimeOffset.MaxValue
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        store.CreateAsync(invalid, CancellationToken.None));

    Assert.NotNull(await store.GetAsync(valid.RunId, CancellationToken.None));
    Assert.False(File.Exists(store.SnapshotPath(invalid.RunId)));
  }

  [Fact]
  public async Task AppendLogAsync_RedactsStandaloneBearerWithoutConsumingDiagnosticText()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(
        run.RunId,
        new RunLogEntry(
            1,
            DateTimeOffset.UtcNow,
            ProviderLogLevel.Info,
            "git",
            "git:install",
            "using bearer abc.def.ghi, provider retry scheduled"),
        CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.LogPath(run.RunId));
    var page = await _store.ReadLogPageAsync(run.RunId, 0, 10, CancellationToken.None);

    Assert.DoesNotContain("abc.def.ghi", disk, StringComparison.Ordinal);
    Assert.Equal("using bearer ***, provider retry scheduled", Assert.Single(page).Message);
  }

  [Theory]
  [InlineData("abcdefghijklmnop")]
  [InlineData("abcdefghijklmnopqrstuvw")]
  public async Task AppendLogAsync_RedactsOpaqueRfc6750BearerTokensOnDisk(string token)
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(
        run.RunId,
        SampleLog(1) with { Message = $"Bearer {token}" },
        CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.LogPath(run.RunId));
    var page = await _store.ReadLogPageAsync(run.RunId, 0, 10, CancellationToken.None);

    Assert.DoesNotContain(token, disk, StringComparison.Ordinal);
    Assert.Equal("Bearer ***", Assert.Single(page).Message);
  }

  [Fact]
  public async Task AppendLogAsync_RedactsShortBearerTokensFromMessageAndErrorOnDisk()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(
        run.RunId,
        SampleLog(1) with
        {
          Message = "Bearer abc123",
          Error = new StructuredError(
              WdemErrorCode.ProviderError,
              "Bearer hunter2",
              "Bearer detail1")
        },
        CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.LogPath(run.RunId));
    var entry = Assert.Single(await _store.ReadLogPageAsync(
        run.RunId, 0, 10, CancellationToken.None));

    Assert.DoesNotContain("abc123", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("hunter2", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("detail1", disk, StringComparison.Ordinal);
    Assert.Equal("Bearer ***", entry.Message);
    Assert.DoesNotContain("hunter2", entry.Error!.Summary, StringComparison.Ordinal);
    Assert.DoesNotContain("detail1", entry.Error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AppendLogAsync_PreservesBearerProtocolDiagnosticOnDisk()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(
        run.RunId,
        SampleLog(1) with { Message = "Bearer RFC6750 support enabled" },
        CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.LogPath(run.RunId));

    Assert.Contains("Bearer RFC6750 support enabled", disk, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AppendLogAsync_RedactsJwtAndPreservesSentencePeriodOnDisk()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(
        run.RunId,
        SampleLog(1) with { Message = "Bearer abc.def.ghi." },
        CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.LogPath(run.RunId));
    var page = await _store.ReadLogPageAsync(run.RunId, 0, 10, CancellationToken.None);

    Assert.DoesNotContain("abc.def.ghi", disk, StringComparison.Ordinal);
    Assert.Equal("Bearer ***.", Assert.Single(page).Message);
  }

  [Fact]
  public async Task CreateAsync_WritesCamelCaseSnapshotAndNormalizesProgress()
  {
    var run = SampleRun() with
    {
      ResourceResults = new Dictionary<string, ResourceResult>
      {
        ["git"] = SampleResourceResult() with
        {
          Progress = double.PositiveInfinity,
          StepResults = [SampleStepResult() with { Progress = -1 }]
        }
      }
    };

    await _store.CreateAsync(run, CancellationToken.None);

    var json = await File.ReadAllTextAsync(_store.SnapshotPath(run.RunId));
    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Contains("\"profileId\"", json, StringComparison.Ordinal);
    Assert.Contains("\"state\": \"Running\"", json, StringComparison.Ordinal);
    Assert.DoesNotContain("\"ProfileId\"", json, StringComparison.Ordinal);
    Assert.Equal(1, restored!.ResourceResults["git"].Progress);
    Assert.Equal(0, restored.ResourceResults["git"].StepResults[0].Progress);
  }

  [Fact]
  public async Task LegacySnapshotWithoutRecoveryMetadataStartsAtRevisionZero()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    document.Remove("revision");
    document.Remove("recoveryClaimId");
    document.Remove("recoveryClaimedAtUtc");
    await File.WriteAllTextAsync(snapshotPath, document.ToJsonString());

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    var saved = await _store.SaveAsync(
        restored! with { RestartReasons = ["legacy upgraded"] },
        CancellationToken.None);

    Assert.Equal(0, restored.Revision);
    Assert.Equal(1, saved.Revision);
    Assert.Equal(1, (await _store.GetAsync(run.RunId, CancellationToken.None))!.Revision);
  }

  [Fact]
  public async Task GetAsync_RestoresGraphAndParameterCaseInsensitivityAndImmutability()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    var nodes = restored!.Graph!.Nodes;
    var parameters = nodes["GIT"].Definition.Parameters;
    Assert.Equal("user", parameters["SCOPE"]);
    Assert.True(((ICollection<KeyValuePair<string, ResolvedResource>>)nodes).IsReadOnly);
    Assert.True(((ICollection<KeyValuePair<string, string?>>)parameters).IsReadOnly);
  }

  [Fact]
  public async Task GetAsync_RestoresAllNestedPlanAndGraphCollectionsAsReadOnly()
  {
    var diagnostic = new StructuredError(
        WdemErrorCode.ProviderError,
        "Provider unavailable",
        "Provider unavailable");
    var definition = SampleDefinition() with { Dependencies = ["dependency"] };
    var plan = SamplePlan();
    var plannedResource = Assert.Single(plan.Resources) with
    {
      Definition = definition,
      Dependencies = ["dependency"],
      BlockedBy = ["blocker"],
      Diagnostics = [diagnostic],
      ResourcePlan = Assert.Single(plan.Resources).ResourcePlan with
      {
        StructuredErrors = [diagnostic]
      }
    };
    var run = SampleRun() with
    {
      Graph = new ResourceGraph(
          new Dictionary<string, ResolvedResource>(StringComparer.OrdinalIgnoreCase)
          {
            ["git"] = new(
                definition,
                ResourceOrigin.Required,
                new HashSet<string>(["consumer"], StringComparer.OrdinalIgnoreCase))
          },
          [new ResourceGraphLayer(0, ["git"])]),
      Plan = plan with
      {
        Layers = [new ResourceGraphLayer(0, ["git"])],
        Resources = [plannedResource],
        Errors = [diagnostic]
      }
    };
    await _store.CreateAsync(run, CancellationToken.None);

    var restored = (await _store.GetAsync(run.RunId, CancellationToken.None))!;
    var restoredResource = Assert.Single(restored.Plan!.Resources);

    AssertReadOnly(restored.Graph!.TopologicalLayers);
    AssertReadOnly(Assert.Single(restored.Graph.TopologicalLayers).ResourceIds);
    AssertReadOnly(restored.Plan.Layers);
    AssertReadOnly(Assert.Single(restored.Plan.Layers).ResourceIds);
    AssertReadOnly(restored.Plan.Resources);
    AssertReadOnly(restored.Plan.Errors);
    AssertReadOnly(restoredResource.Dependencies);
    AssertReadOnly(restoredResource.BlockedBy);
    AssertReadOnly(restoredResource.Diagnostics);
    AssertReadOnly(restoredResource.Definition.Dependencies);
    AssertReadOnly(restoredResource.ResourcePlan.Steps);
    AssertReadOnly(restoredResource.ResourcePlan.StructuredErrors);
  }

  [Fact]
  public async Task SaveAsync_AtomicallyReplacesSnapshotAndListsOnlyIncompleteRuns()
  {
    var incomplete = SampleRun();
    var complete = SampleRun() with
    {
      RunId = Guid.NewGuid(),
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow
    };
    await _store.CreateAsync(incomplete, CancellationToken.None);
    await _store.CreateAsync(complete, CancellationToken.None);

    var current = incomplete;
    foreach (var index in Enumerable.Range(1, 8))
    {
      current = await _store.SaveAsync(
          current with { RestartReasons = [$"save-{index}"] },
          CancellationToken.None);
    }

    var discovered = await _store.ListIncompleteAsync(CancellationToken.None);

    Assert.Single(discovered);
    Assert.Equal(incomplete.RunId, discovered[0].RunId);
    Assert.Equal(8, discovered[0].Revision);
    Assert.False(File.Exists(_store.SnapshotPath(incomplete.RunId) + ".tmp"));
    Assert.NotNull(await _store.GetAsync(incomplete.RunId, CancellationToken.None));
  }

  [Fact]
  public async Task ListAsync_ReturnsCompletedAndIncompleteRunsAcrossStoreInstances()
  {
    var incomplete = SampleRun();
    var complete = SampleRun() with
    {
      RunId = Guid.NewGuid(),
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow
    };
    await _store.CreateAsync(incomplete, CancellationToken.None);
    await _store.CreateAsync(complete, CancellationToken.None);
    var otherStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());

    var allRuns = await otherStore.ListAsync(CancellationToken.None);
    var incompleteRuns = await otherStore.ListIncompleteAsync(CancellationToken.None);

    Assert.Equal(
        new[] { complete.RunId, incomplete.RunId }.Order().ToArray(),
        allRuns.Select(run => run.RunId).Order().ToArray());
    Assert.Equal(incomplete.RunId, Assert.Single(incompleteRuns).RunId);
  }

  [Fact]
  public async Task SaveAsync_PersistsRestartAcknowledgementAcrossStoreInstances()
  {
    var resource = SampleResourceResult() with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow,
      RestartRequirement = RestartPolicy.RestartRequired
    };
    var run = SampleRun() with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow,
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = resource
      },
      RestartRequirements = [RestartPolicy.RestartRequired]
    };
    await _store.CreateAsync(run, CancellationToken.None);
    var secondStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());
    var restored = (await secondStore.GetAsync(run.RunId, CancellationToken.None))!;

    await secondStore.SaveAsync(restored with
    {
      AcknowledgedRestartResourceIds = new HashSet<string>(
          ["git"],
          StringComparer.OrdinalIgnoreCase)
    }, CancellationToken.None);

    var thirdStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());
    var acknowledged = (await thirdStore.GetAsync(run.RunId, CancellationToken.None))!;
    Assert.Contains("git", acknowledged.AcknowledgedRestartResourceIds);
    Assert.Equal([RestartPolicy.RestartRequired], acknowledged.RestartRequirements);
    Assert.Equal(
        RestartPolicy.RestartRequired,
        acknowledged.ResourceResults["git"].RestartRequirement);
  }

  [Fact]
  public async Task CreateAsync_RedactsAcknowledgedRestartResourceIds()
  {
    var run = SampleRun() with
    {
      AcknowledgedRestartResourceIds = new HashSet<string>(
          ["token=restart-ack-secret"],
          StringComparer.OrdinalIgnoreCase)
    };

    await _store.CreateAsync(run, CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.SnapshotPath(run.RunId));
    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.DoesNotContain("restart-ack-secret", disk, StringComparison.Ordinal);
    Assert.Contains("token=***", restored!.AcknowledgedRestartResourceIds);
  }

  [Fact]
  public async Task SaveAsync_CoordinatesAcrossStoreInstances()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var otherStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());
    var payload = new string('x', 256 * 1024);
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var saves = Enumerable.Range(1, 16).Select(index =>
    {
      var store = index % 2 == 0 ? _store : otherStore;
      return AttemptSaveAsync(
          store,
          run with { RestartReasons = [$"{index}:{payload}"] },
          start.Task);
    }).ToArray();

    start.SetResult();
    var errors = await Task.WhenAll(saves);

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Single(errors, error => error is null);
    Assert.Equal(15, errors.Count(error => error is InvalidOperationException));
    Assert.NotNull(restored);
    Assert.Equal(1, restored.Revision);
    Assert.Single(restored.RestartReasons);
  }

  [Fact]
  public async Task SaveAsync_ConcurrentSameRevisionHasSingleWinner()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var otherStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var saves = new[]
    {
      AttemptSaveAsync(
          _store,
          run with { RestartReasons = ["first"] },
          start.Task),
      AttemptSaveAsync(
          otherStore,
          run with { RestartReasons = ["second"] },
          start.Task)
    };

    start.SetResult();
    var errors = await Task.WhenAll(saves);

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Single(errors, error => error is null);
    Assert.Single(errors, error => error is InvalidOperationException);
    Assert.Equal(1, restored!.Revision);
    Assert.Single(restored.RestartReasons);
  }

  [Fact]
  public async Task SaveAsync_WithoutClaimOwnerCannotClearActiveClaim()
  {
    var claimId = Guid.NewGuid();
    var claimed = SampleRun() with
    {
      Revision = 3,
      RecoveryClaimId = claimId,
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow
    };
    await _store.CreateAsync(claimed, CancellationToken.None);

    await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveAsync(
        claimed with
        {
          RecoveryClaimId = null,
          RecoveryClaimedAtUtc = null
        },
        CancellationToken.None));

    var restored = await _store.GetAsync(claimed.RunId, CancellationToken.None);
    Assert.Equal(3, restored!.Revision);
    Assert.Equal(claimId, restored.RecoveryClaimId);
  }

  [Fact]
  public async Task TrySaveAsync_RejectsTerminalizationByWrongClaimOwner()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var claimId = Guid.NewGuid();
    var claimed = run with
    {
      Revision = 1,
      RecoveryClaimId = claimId,
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow
    };
    Assert.True(await _store.TrySaveAsync(
        claimed,
        expectedRevision: 0,
        expectedRecoveryClaimId: null,
        CancellationToken.None));
    var terminal = claimed with
    {
      Revision = 2,
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Cancelled,
      EndedAtUtc = DateTimeOffset.UtcNow,
      RecoveryClaimId = null,
      RecoveryClaimedAtUtc = null
    };

    var saved = await _store.TrySaveAsync(
        terminal,
        expectedRevision: 1,
        expectedRecoveryClaimId: Guid.NewGuid(),
        CancellationToken.None);

    var persisted = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.False(saved);
    Assert.Equal(claimId, persisted!.RecoveryClaimId);
    Assert.Equal(1, persisted.Revision);
  }

  [Fact]
  public async Task TrySaveAsync_StaleClaimOwnerCannotOverwriteNewOwner()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var firstClaimId = Guid.NewGuid();
    var firstClaim = run with
    {
      Revision = 1,
      RecoveryClaimId = firstClaimId,
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
    };
    Assert.True(await _store.TrySaveAsync(
        firstClaim,
        expectedRevision: 0,
        expectedRecoveryClaimId: null,
        CancellationToken.None));
    var secondClaimId = Guid.NewGuid();
    var secondClaim = firstClaim with
    {
      Revision = 2,
      RecoveryClaimId = secondClaimId,
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow
    };
    Assert.True(await _store.TrySaveAsync(
        secondClaim,
        expectedRevision: 1,
        expectedRecoveryClaimId: firstClaimId,
        CancellationToken.None));

    var staleTerminal = firstClaim with
    {
      Revision = 2,
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = DateTimeOffset.UtcNow,
      RecoveryClaimId = null,
      RecoveryClaimedAtUtc = null
    };
    var saved = await _store.TrySaveAsync(
        staleTerminal,
        expectedRevision: 1,
        expectedRecoveryClaimId: firstClaimId,
        CancellationToken.None);

    var persisted = await _store.GetAsync(run.RunId, CancellationToken.None);
    Assert.False(saved);
    Assert.Equal(2, persisted!.Revision);
    Assert.Equal(secondClaimId, persisted.RecoveryClaimId);
    Assert.Equal(ExecutionState.Running, persisted.State);
  }

  [Fact]
  public async Task ReadLogPageAsync_OrdersAndPagesByExclusiveSequence()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    foreach (var sequence in Enumerable.Range(1, 5))
    {
      await _store.AppendLogAsync(
          run.RunId,
          SampleLog(sequence),
          CancellationToken.None);
    }

    var page = await _store.ReadLogPageAsync(run.RunId, 2, 2, CancellationToken.None);

    Assert.Equal([3L, 4L], page.Select(entry => entry.Sequence));
  }

  [Fact]
  public async Task ReadLogPageAsync_SeeksFromCursorWithoutParsingEarlierPages()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    foreach (var sequence in Enumerable.Range(1, 3))
    {
      await _store.AppendLogAsync(run.RunId, SampleLog(sequence), CancellationToken.None);
    }

    await using (var stream = new FileStream(
        _store.LogPath(run.RunId), FileMode.Open, FileAccess.Write, FileShare.Read))
    {
      stream.WriteByte((byte)'!');
    }

    var page = await _store.ReadLogPageAsync(run.RunId, 2, 10, CancellationToken.None);

    Assert.Equal(3, Assert.Single(page).Sequence);
  }

  [Fact]
  public async Task AppendLogAsync_RejectsDuplicateOrOutOfOrderSequence()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(run.RunId, SampleLog(2), CancellationToken.None);

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _store.AppendLogAsync(run.RunId, SampleLog(2), CancellationToken.None));
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _store.AppendLogAsync(run.RunId, SampleLog(1), CancellationToken.None));
  }

  [Theory]
  [InlineData("level")]
  [InlineData("error")]
  [InlineData("kind")]
  public async Task AppendLogAsync_RejectsUndefinedEnumsBeforeWriting(string invalidField)
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var entry = invalidField switch
    {
      "error" => SampleLog(1) with
      {
        Error = new StructuredError(
              (WdemErrorCode)999,
              "Invalid code",
              "Invalid code")
      },
      "kind" => SampleLog(1) with { Kind = (RunEventKind)999 },
      _ => SampleLog(1) with { Level = (ProviderLogLevel)999 }
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.AppendLogAsync(run.RunId, entry, CancellationToken.None));
    Assert.False(File.Exists(_store.LogPath(run.RunId)));
  }

  [Fact]
  public async Task AppendLogAsync_CoordinatesLogAndIndexAcrossStoreInstances()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var otherStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());
    var payload = new string('x', 256 * 1024);

    foreach (var sequence in Enumerable.Range(1, 16))
    {
      var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      async Task AppendAfterStartAsync(JsonExecutionRunStore store)
      {
        await start.Task;
        await store.AppendLogAsync(
            run.RunId,
            SampleLog(sequence) with { Message = payload },
            CancellationToken.None);
      }

      var attempts = new[]
      {
        Record.ExceptionAsync(() => AppendAfterStartAsync(_store)),
        Record.ExceptionAsync(() => AppendAfterStartAsync(otherStore))
      };
      start.SetResult();
      var errors = await Task.WhenAll(attempts);

      Assert.Single(errors, error => error is null);
      Assert.Single(errors, error => error is InvalidOperationException);
    }

    var page = await _store.ReadLogPageAsync(run.RunId, 0, 100, CancellationToken.None);
    Assert.Equal(Enumerable.Range(1, 16).Select(value => (long)value),
        page.Select(entry => entry.Sequence));
  }

  [Fact]
  public async Task AppendLogAsync_TrimsCrashPartialTailBeforeAppending()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(run.RunId, SampleLog(1), CancellationToken.None);
    await File.AppendAllTextAsync(_store.LogPath(run.RunId), "{\"sequence\":");

    await _store.AppendLogAsync(run.RunId, SampleLog(2), CancellationToken.None);

    var page = await _store.ReadLogPageAsync(run.RunId, 0, 10, CancellationToken.None);
    Assert.Equal([1L, 2L], page.Select(entry => entry.Sequence));
  }

  [Fact]
  public async Task AppendLogAsync_PreservesCompleteTailMissingFinalNewline()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(run.RunId, SampleLog(1), CancellationToken.None);
    var logPath = _store.LogPath(run.RunId);
    var existing = (await File.ReadAllTextAsync(logPath)).TrimEnd('\r', '\n');
    await File.WriteAllTextAsync(logPath, existing);

    await _store.AppendLogAsync(run.RunId, SampleLog(2), CancellationToken.None);

    var page = await _store.ReadLogPageAsync(run.RunId, 0, 10, CancellationToken.None);
    Assert.Equal([1L, 2L], page.Select(entry => entry.Sequence));
  }

  [Fact]
  public async Task AppendLogAsync_UsesPersistedTailMetadataInsteadOfRescanningHistory()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(run.RunId, SampleLog(1), CancellationToken.None);
    await _store.AppendLogAsync(run.RunId, SampleLog(2), CancellationToken.None);
    await using (var stream = new FileStream(
        _store.LogPath(run.RunId), FileMode.Open, FileAccess.Write, FileShare.Read))
    {
      stream.WriteByte((byte)'!');
    }

    await _store.AppendLogAsync(run.RunId, SampleLog(3), CancellationToken.None);

    var tail = File.ReadLines(_store.LogPath(run.RunId)).Last();
    Assert.Contains("\"sequence\":3", tail, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("sequence")]
  [InlineData("start")]
  [InlineData("end")]
  public async Task ReadLogPageAsync_AtomicallyRebuildsAnyCorruptCompleteIndexRecord(
      string corruptField)
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    foreach (var sequence in Enumerable.Range(1, 3))
    {
      await _store.AppendLogAsync(run.RunId, SampleLog(sequence), CancellationToken.None);
    }

    const int recordSize = sizeof(long) * 3;
    var indexPath = _store.LogIndexPath(run.RunId);
    var indexBytes = await File.ReadAllBytesAsync(indexPath);
    var secondRecord = recordSize;
    var corruptOffset = corruptField switch
    {
      "sequence" => secondRecord,
      "start" => secondRecord + sizeof(long),
      _ => secondRecord + (sizeof(long) * 2)
    };
    var corruptValue = corruptField switch
    {
      "sequence" => 99L,
      "start" => 0L,
      _ => BinaryPrimitives.ReadInt64LittleEndian(
          indexBytes.AsSpan((recordSize * 2) + (sizeof(long) * 2), sizeof(long)))
    };
    BinaryPrimitives.WriteInt64LittleEndian(
        indexBytes.AsSpan(corruptOffset, sizeof(long)),
        corruptValue);
    await File.WriteAllBytesAsync(indexPath, indexBytes);
    var reopenedStore = new JsonExecutionRunStore(
        new WdemDataPaths(_directory),
        new LogRedactor());

    var page = await reopenedStore.ReadLogPageAsync(
        run.RunId, 0, 10, CancellationToken.None);

    Assert.Equal([1L, 2L, 3L], page.Select(entry => entry.Sequence));
    var repairedIndex = await File.ReadAllBytesAsync(indexPath);
    Assert.Equal(2L, BinaryPrimitives.ReadInt64LittleEndian(
        repairedIndex.AsSpan(secondRecord, sizeof(long))));
    Assert.Empty(Directory.GetFiles(
        Path.GetDirectoryName(indexPath)!,
        Path.GetFileName(indexPath) + ".*.tmp"));
  }

  [Theory]
  [InlineData(-1, 1)]
  [InlineData(0, 0)]
  [InlineData(0, 1001)]
  public async Task ReadLogPageAsync_RejectsInvalidPagination(long afterSequence, int take)
  {
    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
        _store.ReadLogPageAsync(Guid.NewGuid(), afterSequence, take, CancellationToken.None));
  }

  [Fact]
  public async Task Operations_HaveDeterministicMissingAndDuplicateRunBehavior()
  {
    var run = SampleRun();

    Assert.Null(await _store.GetAsync(run.RunId, CancellationToken.None));
    await _store.CreateAsync(run, CancellationToken.None);
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        _store.SaveAsync(run with { RunId = Guid.NewGuid() }, CancellationToken.None));
    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        _store.AppendLogAsync(Guid.NewGuid(), SampleLog(1), CancellationToken.None));
  }

  [Fact]
  public async Task GetAsync_MissingRunDoesNotLeaveLockFile()
  {
    var runId = Guid.NewGuid();
    Directory.CreateDirectory(new WdemDataPaths(_directory).RunsDirectory);

    Assert.Null(await _store.GetAsync(runId, CancellationToken.None));

    var lockPath = Path.Combine(
        new WdemDataPaths(_directory).RunsDirectory,
        $"{runId:D}.lock");
    Assert.False(File.Exists(lockPath));
  }

  [Fact]
  public async Task SaveAsync_MissingRunDoesNotLeaveLockFile()
  {
    var run = SampleRun();

    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        _store.SaveAsync(run, CancellationToken.None));

    var lockPath = Path.Combine(
        new WdemDataPaths(_directory).RunsDirectory,
        $"{run.RunId:D}.lock");
    Assert.False(File.Exists(lockPath));
  }

  [Fact]
  public async Task AppendLogAsync_MissingRunDoesNotLeaveLockFile()
  {
    var runId = Guid.NewGuid();

    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        _store.AppendLogAsync(runId, SampleLog(1), CancellationToken.None));

    var lockPath = Path.Combine(
        new WdemDataPaths(_directory).RunsDirectory,
        $"{runId:D}.lock");
    Assert.False(File.Exists(lockPath));
  }

  [Fact]
  public async Task ReadLogPageAsync_MissingRunDoesNotLeaveLockFile()
  {
    var runId = Guid.NewGuid();

    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        _store.ReadLogPageAsync(runId, 0, 10, CancellationToken.None));

    var lockPath = Path.Combine(
        new WdemDataPaths(_directory).RunsDirectory,
        $"{runId:D}.lock");
    Assert.False(File.Exists(lockPath));
  }

  [Theory]
  [InlineData(null, true)]
  [InlineData(ExecutionOutcome.Succeeded, false)]
  public async Task CreateAsync_RejectsCompletedRunMissingTerminalFields(
      ExecutionOutcome? outcome,
      bool includeEndedAt)
  {
    var run = SampleRun() with
    {
      State = ExecutionState.Completed,
      Outcome = outcome,
      EndedAtUtc = includeEndedAt ? DateTimeOffset.UtcNow : null
    };

    await Assert.ThrowsAsync<ArgumentException>(() =>
        _store.CreateAsync(run, CancellationToken.None));
    Assert.False(File.Exists(_store.SnapshotPath(run.RunId)));
  }

  [Fact]
  public async Task SaveAsync_RejectsTerminalFieldsOnIncompleteRun()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);

    await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(
        run with
        {
          Outcome = ExecutionOutcome.Succeeded,
          EndedAtUtc = DateTimeOffset.UtcNow
        },
        CancellationToken.None));
  }

  [Fact]
  public async Task Operation_ObservesPreCancelledTokenBeforeIo()
  {
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        _store.CreateAsync(SampleRun(), cancellation.Token));
    Assert.False(Directory.Exists(new WdemDataPaths(_directory).RunsDirectory));
  }

  [Fact]
  public async Task ListIncompleteAsync_PreservesMalformedSnapshotAndExposesDiagnostic()
  {
    Directory.CreateDirectory(new WdemDataPaths(_directory).RunsDirectory);
    var runId = Guid.NewGuid();
    var snapshotPath = _store.SnapshotPath(runId);
    await File.WriteAllTextAsync(snapshotPath, "{ malformed json");

    var runs = await _store.ListIncompleteAsync(CancellationToken.None);

    Assert.Empty(runs);
    Assert.False(File.Exists(snapshotPath));
    Assert.Single(Directory.GetFiles(
        Path.GetDirectoryName(snapshotPath)!, $"{runId:D}.json.corrupted.*"));
    var diagnostic = Assert.Single(_store.Diagnostics);
    Assert.Equal(WdemErrorCode.DetectionError, diagnostic.Code);
    Assert.Contains(runId.ToString("D"), diagnostic.Detail, StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData("selectedOptionalResourceIds")]
  [InlineData("profileId")]
  public async Task GetAsync_PreservesSnapshotWithNullRequiredMember(string propertyName)
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    document[propertyName] = null;
    await File.WriteAllTextAsync(
        snapshotPath,
        document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Null(restored);
    Assert.False(File.Exists(snapshotPath));
    Assert.Single(Directory.GetFiles(
        Path.GetDirectoryName(snapshotPath)!, $"{run.RunId:D}.json.corrupted.*"));
    Assert.Single(_store.Diagnostics);
  }

  [Fact]
  public async Task GetAsync_PreservesSnapshotWithInvalidStateCombination()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    document["state"] = "Completed";
    await File.WriteAllTextAsync(
        snapshotPath,
        document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Null(restored);
    Assert.False(File.Exists(snapshotPath));
    Assert.Single(Directory.GetFiles(
        Path.GetDirectoryName(snapshotPath)!, $"{run.RunId:D}.json.corrupted.*"));
    Assert.Single(_store.Diagnostics);
  }

  [Fact]
  public async Task GetAsync_PreservesSnapshotWithNullPlanResource()
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    document["plan"]!["resources"]!.AsArray()[0] = null;
    await File.WriteAllTextAsync(snapshotPath, document.ToJsonString());

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Null(restored);
    Assert.False(File.Exists(snapshotPath));
    Assert.Single(Directory.GetFiles(
        Path.GetDirectoryName(snapshotPath)!, $"{run.RunId:D}.json.corrupted.*"));
    Assert.Single(_store.Diagnostics);
  }

  [Theory]
  [InlineData("number")]
  [InlineData("numericString")]
  [InlineData("unknownString")]
  public async Task GetAsync_PreservesSnapshotWithInvalidEnum(string invalidKind)
  {
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    var snapshotPath = _store.SnapshotPath(run.RunId);
    var document = JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!.AsObject();
    document["state"] = invalidKind switch
    {
      "number" => JsonValue.Create(999),
      "numericString" => JsonValue.Create("999"),
      _ => JsonValue.Create("UnknownState")
    };
    await File.WriteAllTextAsync(snapshotPath, document.ToJsonString());

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Null(restored);
    Assert.False(File.Exists(snapshotPath));
    Assert.Single(Directory.GetFiles(
        Path.GetDirectoryName(snapshotPath)!, $"{run.RunId:D}.json.corrupted.*"));
    Assert.Single(_store.Diagnostics);
  }

  [Fact]
  public async Task CreateAndSave_RedactSnapshotMessagesAndErrors()
  {
    var run = SampleRun() with
    {
      ResourceResults = new Dictionary<string, ResourceResult>
      {
        ["git"] = SampleResourceResult() with
        {
          Message = "password=snapshot-secret",
          Error = new StructuredError(
              WdemErrorCode.ProviderError,
              "token=snapshot-summary",
              "Authorization: Bearer snapshot.detail.token")
        }
      }
    };

    await _store.CreateAsync(run, CancellationToken.None);
    var disk = await File.ReadAllTextAsync(_store.SnapshotPath(run.RunId));
    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);

    Assert.DoesNotContain("snapshot-secret", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("snapshot-summary", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("snapshot.detail.token", disk, StringComparison.Ordinal);
    Assert.Equal("password=***", restored!.ResourceResults["git"].Message);
  }

  [Fact]
  public async Task CreateAsync_RedactsSensitiveProfileParametersByKey()
  {
    var definition = SampleDefinition() with
    {
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["clientSecret"] = "hunter2",
        ["access_token"] = "abc123",
        ["githubToken"] = "github-credential",
        ["databasePassword"] = "database-credential",
        ["serviceSecret"] = "service-credential",
        ["api-key"] = "api-credential",
        ["thumb_print"] = "thumbprint-credential",
        ["scope"] = "user",
        ["passwordPolicy"] = "standard"
      }
    };
    var run = SampleRun() with
    {
      Graph = new ResourceGraph(
          new Dictionary<string, ResolvedResource>(StringComparer.OrdinalIgnoreCase)
          {
            ["git"] = new(
                definition,
                ResourceOrigin.Required,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
          },
          [new ResourceGraphLayer(0, ["git"])])
    };

    await _store.CreateAsync(run, CancellationToken.None);

    var disk = await File.ReadAllTextAsync(_store.SnapshotPath(run.RunId));
    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    var parameters = restored!.Graph!.Nodes["git"].Definition.Parameters;

    Assert.DoesNotContain("hunter2", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("abc123", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("github-credential", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("database-credential", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("service-credential", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("api-credential", disk, StringComparison.Ordinal);
    Assert.DoesNotContain("thumbprint-credential", disk, StringComparison.Ordinal);
    Assert.Equal("***", parameters["clientSecret"]);
    Assert.Equal("***", parameters["access_token"]);
    Assert.Equal("***", parameters["githubToken"]);
    Assert.Equal("***", parameters["databasePassword"]);
    Assert.Equal("***", parameters["serviceSecret"]);
    Assert.Equal("***", parameters["api-key"]);
    Assert.Equal("***", parameters["thumb_print"]);
    Assert.Equal("user", parameters["scope"]);
    Assert.Equal("standard", parameters["passwordPolicy"]);
  }

  [Fact]
  public void WindowsMachineInformationProvider_CapturesFactsFromItsEnvironmentAbstraction()
  {
    var machine = new WindowsMachineInformationProvider(new StubMachineInformationSource())
        .GetMachineInformation();

    Assert.Equal("Windows test version", machine.OperatingSystem);
    Assert.Equal("Arm64", machine.Architecture);
    Assert.Equal("TESTBOX", machine.ComputerName);
    Assert.Equal("test-user", machine.UserName);
  }

  public void Dispose()
  {
    if (Directory.Exists(_directory))
    {
      Directory.Delete(_directory, recursive: true);
    }
  }

  private static ExecutionRun SampleRun() => new()
  {
    RunId = Guid.NewGuid(),
    Mode = RunMode.Apply,
    ProfileSourcePath = @"C:\profiles\developer.json",
    ProfileId = "developer",
    ProfileVersion = "1.0.0",
    SelectedOptionalResourceIds = new HashSet<string>(["git"], StringComparer.OrdinalIgnoreCase),
    StartedAtUtc = DateTimeOffset.UtcNow,
    State = ExecutionState.Running,
    Machine = new MachineInformation("Windows", "X64", "DEVBOX", "developer"),
    Graph = SampleGraph(),
    Plan = SamplePlan(),
    ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = SampleResourceResult()
    },
    RestartRequirements = [RestartPolicy.RestartRecommended],
    RestartReasons = ["PATH changed"]
  };

  private static ExecutionRun ElevatedRunWithSecret(string secret)
  {
    var run = SampleRun();
    var planned = run.Plan!.Resources.Single();
    var definition = planned.Definition with
    {
      PrivilegeRequirement = PrivilegeRequirement.Administrator,
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["password"] = secret
      }
    };
    var plan = planned.ResourcePlan with
    {
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(definition),
      Steps =
      [
        planned.ResourcePlan.Steps.Single() with
        {
          PrivilegeRequirement = PrivilegeRequirement.Administrator
        }
      ]
    };
    return run with
    {
      Plan = run.Plan with
      {
        Resources =
        [
          planned with
          {
            Definition = definition,
            ResourcePlan = plan,
            RequiresElevation = true,
            Risk = PlanRisk.Elevated
          }
        ]
      }
    };
  }

  private static ResourceResult SampleResourceResult() => new()
  {
    ResourceId = "git",
    State = ExecutionState.Running,
    FinalCompliance = ComplianceStatus.VersionMismatch,
    DetectedBefore = new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Succeeded,
      Exists = true,
      Version = "2.52.0",
      InstalledVersions = [new SemanticVersion(2, 52, 0)],
      Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
        ["provider"] = "winget"
      }
    },
    Progress = 0.5,
    Message = "Installing",
    StartedAtUtc = DateTimeOffset.UtcNow,
    RestartRequirement = RestartPolicy.NoRestart,
    StepResults = [SampleStepResult()]
  };

  private static DetectedState MissingEvidence(string detail) => new()
  {
    ResourceId = "git",
    Outcome = DetectionOutcome.Succeeded,
    Exists = false,
    Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
      ["detail"] = detail
    }
  };

  private static StepResult SampleStepResult() => new()
  {
    StepId = "git:install",
    Name = "Install Git",
    State = ExecutionState.Running,
    Progress = 0.5,
    FirstLogSequence = 1,
    LastLogSequence = 1
  };

  private static ResourceGraph SampleGraph()
  {
    var definition = SampleDefinition();
    return new ResourceGraph(
        new Dictionary<string, ResolvedResource>(StringComparer.OrdinalIgnoreCase)
        {
          ["git"] = new(
              definition,
              ResourceOrigin.Required,
              new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        },
        [new ResourceGraphLayer(0, ["git"])]);
  }

  private static ExecutionPlan SamplePlan()
  {
    var definition = SampleDefinition();
    var resourcePlan = new ResourcePlan
    {
      ResourceId = "git",
      ResourceType = "package",
      ProviderName = "winget",
      DesiredStateFingerprint = "fingerprint",
      Compliance = ComplianceStatus.VersionMismatch,
      IsExecutable = true,
      Steps =
      [
        new PlanStep
        {
          Id = "git:install",
          Description = "Install Git",
          Action = PlanAction.Upgrade,
          PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
          RestartPolicy = RestartPolicy.NoRestart
        }
      ]
    };
    return new ExecutionPlan
    {
      PlanId = Guid.NewGuid(),
      Fingerprint = "plan-fingerprint",
      ProfileId = "developer",
      ProfileVersion = "1.0.0",
      Layers = [new ResourceGraphLayer(0, ["git"])],
      Resources =
      [
        new PlannedResource
        {
          Definition = definition,
          Origin = ResourceOrigin.Required,
          Dependencies = [],
          ResourcePlan = resourcePlan,
          Status = PlannedResourceStatus.Ready,
          Risk = PlanRisk.Standard,
          RequiresElevation = false,
          IsDestructive = false,
          RestartPolicy = RestartPolicy.NoRestart
        }
      ],
      IsExecutable = true
    };
  }

  private static ResourceDefinition SampleDefinition() => new()
  {
    Id = "git",
    Type = "package",
    Provider = "winget",
    VersionConstraint = ">=2.52.1",
    Dependencies = [],
    Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
      ["scope"] = "user"
    },
    PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
    RestartPolicy = RestartPolicy.NoRestart
  };

  private static RunLogEntry SampleLog(int sequence) => new(
      sequence,
      DateTimeOffset.UtcNow.AddSeconds(sequence),
      ProviderLogLevel.Info,
      "git",
      "git:install",
      $"message-{sequence}");

  private static async Task<Exception?> AttemptSaveAsync(
      IExecutionRunStore store,
      ExecutionRun run,
      Task start)
  {
    await start;
    try
    {
      await store.SaveAsync(run, CancellationToken.None);
      return null;
    }
    catch (Exception exception)
    {
      return exception;
    }
  }

  private static void AssertReadOnly<T>(IReadOnlyList<T> values)
  {
    var collection = Assert.IsAssignableFrom<ICollection<T>>(values);
    Assert.True(collection.IsReadOnly);
    Assert.Throws<NotSupportedException>(() => collection.Add(values[0]));
  }

  private sealed class StubMachineInformationSource : IWindowsMachineInformationSource
  {
    public string OperatingSystem => "Windows test version";
    public string Architecture => "Arm64";
    public string ComputerName => "TESTBOX";
    public string UserName => "test-user";
  }

  private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => utcNow;
  }

  private sealed class DeterministicApprovedResourceProtector : IApprovedResourceProtector
  {
    private readonly byte[] _key;

    public DeterministicApprovedResourceProtector(string identity = "test-user")
    {
      _key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
    }

    public byte[] Protect(byte[] plaintext, byte[] entropy)
    {
      var nonce = SHA256.HashData(entropy)[..12];
      var ciphertext = new byte[plaintext.Length];
      var tag = new byte[16];
      using var aes = new AesGcm(_key, tag.Length);
      aes.Encrypt(nonce, plaintext, ciphertext, tag, entropy);
      return [.. nonce, .. tag, .. ciphertext];
    }

    public byte[] Unprotect(byte[] protectedData, byte[] entropy)
    {
      var plaintext = new byte[protectedData.Length - 28];
      using var aes = new AesGcm(_key, 16);
      aes.Decrypt(
          protectedData.AsSpan(0, 12),
          protectedData.AsSpan(28),
          protectedData.AsSpan(12, 16),
          plaintext,
          entropy);
      return plaintext;
    }
  }

  private sealed class OriginalValueRecordingProvider : IResourceProvider
  {
    public string ResourceType => "package";
    public string ProviderName => "winget";
    public ProviderCapabilities Capabilities { get; } = new();
    public ResourceDefinition? AppliedResource { get; private set; }

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      AppliedResource = resource;
      return ValueTask.FromResult(new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Succeeded
      });
    }

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
  }
}
