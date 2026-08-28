using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Core.Versions;
using Wdem.Windows.Persistence;
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

    var saves = Enumerable.Range(1, 8).Select(index =>
        _store.SaveAsync(
            incomplete with { RestartReasons = [$"save-{index}"] },
            CancellationToken.None));
    await Task.WhenAll(saves);

    var discovered = await _store.ListIncompleteAsync(CancellationToken.None);

    Assert.Single(discovered);
    Assert.Equal(incomplete.RunId, discovered[0].RunId);
    Assert.False(File.Exists(_store.SnapshotPath(incomplete.RunId) + ".tmp"));
    Assert.NotNull(await _store.GetAsync(incomplete.RunId, CancellationToken.None));
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

  private sealed class StubMachineInformationSource : IWindowsMachineInformationSource
  {
    public string OperatingSystem => "Windows test version";
    public string Architecture => "Arm64";
    public string ComputerName => "TESTBOX";
    public string UserName => "test-user";
  }
}
