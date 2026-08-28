using System.Text.Json;
using Wdem.Windows.Persistence;
using Xunit;

namespace Wdem.Windows.Tests.Persistence;

public sealed class LegacyStateMigrationAdapterTests : IDisposable
{
  private readonly string _root = Path.Combine(
      Path.GetTempPath(),
      $"wdem-migration-tests-{Guid.NewGuid():N}");

  [Fact]
  public async Task MigrateAsync_ImportsOnlyStepNamesIntoClearlyLabelledMarker()
  {
    WriteLegacy("state.json", """
        {
          "applied_items": ["Git.Git"],
          "system_setting_originals": { "secret": "must-not-copy" },
          "step_history": {
            "install-git": { "stepName": "Install Git", "status": "Succeeded" },
            "configure-shell": { "status": "Failed", "errorMessage": "password=must-not-copy" }
          }
        }
        """);
    var adapter = new LegacyStateMigrationAdapter(_root);

    var result = await adapter.MigrateAsync(CancellationToken.None);

    Assert.True(result.MigrationPerformed);
    Assert.Equal(["Git.Git", "Install Git", "configure-shell"], result.ImportedStepNames);
    using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(result.MarkerPath));
    Assert.Equal("legacy-step-name-reference", marker.RootElement.GetProperty("recordKind").GetString());
    Assert.Equal("WinHome", marker.RootElement.GetProperty("sourceProduct").GetString());
    Assert.False(marker.RootElement.TryGetProperty("compliance", out _));
    Assert.DoesNotContain("must-not-copy", marker.RootElement.GetRawText(), StringComparison.Ordinal);
    Assert.True(File.Exists(Path.Combine(_root, "WinHome", "state.json")));
  }

  [Fact]
  public async Task MigrateAsync_SupportsLegacyArrayAndStepDictionaryFormats()
  {
    WriteLegacy(".winhome-state.json", """
        {
          "step-one": { "step_id": "step-one", "step_name": "First step" },
          "step-two": { "StepId": "step-two" }
        }
        """);
    WriteLegacy("winhome.state.json", "[\"array-step\", \"token=super-secret\"]");
    var adapter = new LegacyStateMigrationAdapter(_root);

    var result = await adapter.MigrateAsync(CancellationToken.None);

    Assert.Equal(
        ["First step", "step-two", "array-step", "Legacy item 1 (redacted)"],
        result.ImportedStepNames);
  }

  [Fact]
  public async Task MigrateAsync_MissingOrMalformedLegacyStateStillCompletesOnce()
  {
    WriteLegacy("state.json", "{ malformed");
    var adapter = new LegacyStateMigrationAdapter(_root);

    var first = await adapter.MigrateAsync(CancellationToken.None);
    File.WriteAllText(Path.Combine(_root, "WinHome", "state.json"), "[\"late-step\"]");
    var second = await adapter.MigrateAsync(CancellationToken.None);

    Assert.True(first.MigrationPerformed);
    Assert.Empty(first.ImportedStepNames);
    Assert.False(second.MigrationPerformed);
    Assert.Empty(second.ImportedStepNames);
  }

  [Fact]
  public async Task MigrateAsync_ExistingMarkerNeverReadsLegacyDirectory()
  {
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    await File.WriteAllTextAsync(markerPath, """
        { "schemaVersion": 1, "recordKind": "legacy-step-name-reference", "sourceProduct": "WinHome", "importedAtUtc": "2026-08-29T00:00:00Z", "importedStepNames": [] }
        """);
    WriteLegacy("state.json", "[\"must-not-import\"]");
    var adapter = new LegacyStateMigrationAdapter(_root);

    var result = await adapter.MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Empty(result.ImportedStepNames);
  }

  [Fact]
  public async Task MigrateAsync_TransientInaccessibleMarkerStateFailsWithoutSideEffects()
  {
    WriteLegacy("state.json", "[\"late-step\"]");
    var legacyPath = Path.Combine(_root, "WinHome", "state.json");
    var resolver = new RecordingFinalPathResolver(legacyPath);
    var adapter = new LegacyStateMigrationAdapter(
        _root,
        resolver,
        SystemLegacyMigrationFileOperations.Instance,
        WindowsMigrationMarkerFinalPathResolver.Instance,
        new FixedMigrationPathEntryProbe(MigrationPathEntryState.Inaccessible));

    await Assert.ThrowsAsync<IOException>(() =>
        adapter.MigrateAsync(CancellationToken.None));

    Assert.False(resolver.ObservedOpenReadableStream);
    Assert.False(File.Exists(Path.Combine(_root, "WDEM", "migration-v1.json")));
    Assert.False(File.Exists(Path.Combine(_root, "WDEM", ".migration-v1.gate")));

    var recovered = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.True(recovered.MigrationPerformed);
    Assert.Equal(["late-step"], recovered.ImportedStepNames);
  }

  [Fact]
  public async Task MigrateAsync_PersistentInaccessibleMarkerStateNeverReportsSuccess()
  {
    WriteLegacy("state.json", "[\"must-not-import\"]");
    var resolver = new RecordingFinalPathResolver(
        Path.Combine(_root, "WinHome", "state.json"));
    var adapter = new LegacyStateMigrationAdapter(
        _root,
        resolver,
        SystemLegacyMigrationFileOperations.Instance,
        WindowsMigrationMarkerFinalPathResolver.Instance,
        new FixedMigrationPathEntryProbe(MigrationPathEntryState.Inaccessible));

    await Assert.ThrowsAsync<IOException>(() =>
        adapter.MigrateAsync(CancellationToken.None));
    await Assert.ThrowsAsync<IOException>(() =>
        adapter.MigrateAsync(CancellationToken.None));

    Assert.False(resolver.ObservedOpenReadableStream);
    Assert.False(File.Exists(Path.Combine(_root, "WDEM", "migration-v1.json")));
    Assert.False(File.Exists(Path.Combine(_root, "WDEM", ".migration-v1.gate")));
  }

  [Theory]
  [InlineData("")]
  [InlineData("{")]
  [InlineData("{ \"schemaVersion\": 2, \"recordKind\": \"legacy-step-name-reference\", \"sourceProduct\": \"WinHome\", \"importedAtUtc\": \"2026-08-29T00:00:00Z\", \"importedStepNames\": [] }")]
  [InlineData("{ \"schemaVersion\": 1, \"recordKind\": \"unrelated\", \"sourceProduct\": \"Other\", \"importedAtUtc\": \"2026-08-29T00:00:00Z\", \"importedStepNames\": [] }")]
  public async Task MigrateAsync_InvalidMarkerRecordsOneDiagnosticWithoutRereadingLegacy(
      string invalidMarker)
  {
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    await File.WriteAllTextAsync(markerPath, invalidMarker);
    WriteLegacy("state.json", "[\"recovered-step\"]");
    var legacyPath = Path.Combine(_root, "WinHome", "state.json");
    var firstResolver = new RecordingFinalPathResolver(legacyPath);

    var result = await new LegacyStateMigrationAdapter(_root, firstResolver)
        .MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Empty(result.ImportedStepNames);
    Assert.False(firstResolver.ObservedOpenReadableStream);
    var firstDiagnostics = Directory.EnumerateFiles(
        markerDirectory,
        "migration-v1.invalid-*.json").Order().ToArray();
    Assert.Single(firstDiagnostics);
    var firstDiagnosticBytes = firstDiagnostics.Sum(path => new FileInfo(path).Length);
    Assert.Equal(invalidMarker, await File.ReadAllTextAsync(markerPath));

    var secondResolver = new RecordingFinalPathResolver(legacyPath);
    var second = await new LegacyStateMigrationAdapter(_root, secondResolver)
        .MigrateAsync(CancellationToken.None);

    Assert.False(second.MigrationPerformed);
    Assert.Empty(second.ImportedStepNames);
    Assert.False(secondResolver.ObservedOpenReadableStream);
    Assert.Equal(invalidMarker, await File.ReadAllTextAsync(markerPath));
    var repeatedDiagnostics = Directory.EnumerateFiles(
        markerDirectory,
        "migration-v1.invalid-*.json").Order().ToArray();
    Assert.Equal(firstDiagnostics, repeatedDiagnostics);
    Assert.Equal(
        firstDiagnosticBytes,
        repeatedDiagnostics.Sum(path => new FileInfo(path).Length));
  }

  [Fact]
  public async Task MigrateAsync_ExistingInvalidDiagnosticSkipsFurtherWrites()
  {
    const string invalidMarker = "{";
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    await File.WriteAllTextAsync(markerPath, invalidMarker);
    WriteLegacy("state.json", "[\"must-not-import\"]");

    await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);
    var operations = new ThrowingDiagnosticMigrationFileOperations();
    var resolver = new RecordingFinalPathResolver(
        Path.Combine(_root, "WinHome", "state.json"));

    var repeatedAdapter = new LegacyStateMigrationAdapter(
        _root,
        resolver,
        operations);

    var repeated = await repeatedAdapter.MigrateAsync(CancellationToken.None);
    var repeatedAgain = await repeatedAdapter.MigrateAsync(CancellationToken.None);

    Assert.False(repeated.MigrationPerformed);
    Assert.Empty(repeated.ImportedStepNames);
    Assert.False(repeatedAgain.MigrationPerformed);
    Assert.Empty(repeatedAgain.ImportedStepNames);
    Assert.Equal(0, operations.DiagnosticCommitAttempts);
    Assert.False(resolver.ObservedOpenReadableStream);
    Assert.Equal(invalidMarker, await File.ReadAllTextAsync(markerPath));
    Assert.Single(Directory.EnumerateFiles(
        markerDirectory,
        "migration-v1.invalid-*.json"));
  }

  [Fact]
  public async Task MigrateAsync_PreexistingInvalidDiagnosticNeverAuthorizesLegacyRead()
  {
    const string invalidMarker = "{";
    const string untrustedDiagnostic = "attacker-controlled";
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    var diagnosticPath = Path.Combine(
        markerDirectory,
        "migration-v1.invalid-diagnostic.json");
    await File.WriteAllTextAsync(markerPath, invalidMarker);
    await File.WriteAllTextAsync(diagnosticPath, untrustedDiagnostic);
    WriteLegacy("state.json", "[\"must-not-import\"]");
    var operations = new ThrowingDiagnosticMigrationFileOperations();
    var resolver = new RecordingFinalPathResolver(
        Path.Combine(_root, "WinHome", "state.json"));

    var result = await new LegacyStateMigrationAdapter(
        _root,
        resolver,
        operations).MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Empty(result.ImportedStepNames);
    Assert.Equal(0, operations.DiagnosticCommitAttempts);
    Assert.False(resolver.ObservedOpenReadableStream);
    Assert.Equal(invalidMarker, await File.ReadAllTextAsync(markerPath));
    Assert.Equal(untrustedDiagnostic, await File.ReadAllTextAsync(diagnosticPath));
  }

  [Theory]
  [InlineData(MigrationFailureStage.DiagnosticCommit, false)]
  [InlineData(MigrationFailureStage.DiagnosticCommit, true)]
  public async Task MigrateAsync_InvalidMarkerFailureAlwaysLeavesPermanentGate(
      MigrationFailureStage failureStage,
      bool cancel)
  {
    const string invalidMarker = "{";
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    await File.WriteAllTextAsync(markerPath, invalidMarker);
    WriteLegacy("state.json", "[\"must-not-import\"]");
    var legacyPath = Path.Combine(_root, "WinHome", "state.json");
    var firstResolver = new RecordingFinalPathResolver(legacyPath);
    var adapter = new LegacyStateMigrationAdapter(
        _root,
        firstResolver,
        new FaultInjectingMigrationFileOperations(failureStage, cancel));

    if (cancel)
    {
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
          adapter.MigrateAsync(CancellationToken.None));
    }
    else
    {
      await Assert.ThrowsAnyAsync<IOException>(() =>
          adapter.MigrateAsync(CancellationToken.None));
    }

    Assert.Equal(invalidMarker, await File.ReadAllTextAsync(markerPath));
    Assert.False(firstResolver.ObservedOpenReadableStream);
    if (failureStage == MigrationFailureStage.DiagnosticCommit)
    {
      Assert.Empty(Directory.EnumerateFiles(
          markerDirectory,
          "migration-v1.invalid-*.json"));
    }

    var secondResolver = new RecordingFinalPathResolver(legacyPath);
    var second = await new LegacyStateMigrationAdapter(_root, secondResolver)
        .MigrateAsync(CancellationToken.None);

    Assert.False(second.MigrationPerformed);
    Assert.Empty(second.ImportedStepNames);
    Assert.False(secondResolver.ObservedOpenReadableStream);
    Assert.Equal(invalidMarker, await File.ReadAllTextAsync(markerPath));
  }

  [Fact]
  public async Task MigrateAsync_CommitConflictReturnsPersistedWinnerNames()
  {
    WriteLegacy("state.json", "[\"local-step-a\"]");
    var markerPath = Path.Combine(_root, "WDEM", "migration-v1.json");
    var winnerMarker = CreateMarkerJson(["winner-step-b"]);
    var firstResolver = new RecordingFinalPathResolver(
        Path.Combine(_root, "WinHome", "state.json"));
    var adapter = new LegacyStateMigrationAdapter(
        _root,
        firstResolver,
        new CompetingMigrationFileOperations(winnerMarker));

    var result = await adapter.MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Equal(["winner-step-b"], result.ImportedStepNames);
    Assert.Equal(1, firstResolver.OpenReadableStreamCount);
    var persistedMarker = await File.ReadAllTextAsync(markerPath);
    Assert.Contains("winner-step-b", persistedMarker, StringComparison.Ordinal);
    Assert.DoesNotContain("local-step-a", persistedMarker, StringComparison.Ordinal);

    var secondResolver = new RecordingFinalPathResolver(
        Path.Combine(_root, "WinHome", "state.json"));
    var second = await new LegacyStateMigrationAdapter(_root, secondResolver)
        .MigrateAsync(CancellationToken.None);

    Assert.False(second.MigrationPerformed);
    Assert.False(secondResolver.ObservedOpenReadableStream);
    Assert.Equal(persistedMarker, await File.ReadAllTextAsync(markerPath));
  }

  [Fact]
  public async Task MigrateAsync_ValidWinnerHandleRejectsMutationDuringSnapshot()
  {
    WriteLegacy("state.json", "[\"local-step-a\"]");
    var markerPath = Path.Combine(_root, "WDEM", "migration-v1.json");
    var markerResolver = new MutationAttemptingMarkerFinalPathResolver(
        CreateMarkerJson(["mutated-step-c"]));
    var adapter = new LegacyStateMigrationAdapter(
        _root,
        new RecordingFinalPathResolver(Path.Combine(_root, "WinHome", "state.json")),
        new CompetingMigrationFileOperations(CreateMarkerJson(["winner-step-b"])),
        markerResolver);

    var result = await adapter.MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Equal(["winner-step-b"], result.ImportedStepNames);
    Assert.True(markerResolver.DeleteRejected);
    Assert.True(markerResolver.ReplaceRejected);
    var persistedMarker = await File.ReadAllTextAsync(markerPath);
    Assert.Contains("winner-step-b", persistedMarker, StringComparison.Ordinal);
    Assert.DoesNotContain("mutated-step-c", persistedMarker, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MigrateAsync_InvalidCommitConflictPreservesGateWithoutRereadingLegacy()
  {
    WriteLegacy("state.json", "[\"local-step-a\"]");
    var markerPath = Path.Combine(_root, "WDEM", "migration-v1.json");
    var firstResolver = new RecordingFinalPathResolver(
        Path.Combine(_root, "WinHome", "state.json"));
    var adapter = new LegacyStateMigrationAdapter(
        _root,
        firstResolver,
        new CompetingMigrationFileOperations("{"));

    var result = await adapter.MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Empty(result.ImportedStepNames);
    Assert.Equal(1, firstResolver.OpenReadableStreamCount);
    Assert.Equal("{", await File.ReadAllTextAsync(markerPath));
    Assert.Single(Directory.EnumerateFiles(
        Path.GetDirectoryName(markerPath)!,
        "migration-v1.invalid-*.json"));

    var secondResolver = new RecordingFinalPathResolver(
        Path.Combine(_root, "WinHome", "state.json"));
    var second = await new LegacyStateMigrationAdapter(_root, secondResolver)
        .MigrateAsync(CancellationToken.None);

    Assert.False(second.MigrationPerformed);
    Assert.False(secondResolver.ObservedOpenReadableStream);
    Assert.Equal("{", await File.ReadAllTextAsync(markerPath));
  }

  [Fact]
  public async Task MigrateAsync_DisappearingCommitWinnerRetriesWithoutLosingGate()
  {
    WriteLegacy("state.json", "[\"local-step-a\"]");
    var markerPath = Path.Combine(_root, "WDEM", "migration-v1.json");
    var operations = new DisappearingWinnerMigrationFileOperations();

    var result = await new LegacyStateMigrationAdapter(
        _root,
        new RecordingFinalPathResolver(Path.Combine(_root, "WinHome", "state.json")),
        operations).MigrateAsync(CancellationToken.None);

    Assert.True(result.MigrationPerformed);
    Assert.Equal(["local-step-a"], result.ImportedStepNames);
    Assert.True(File.Exists(markerPath));
    Assert.Equal(2, operations.CommitAttempts);
    Assert.Contains(
        "local-step-a",
        await File.ReadAllTextAsync(markerPath),
        StringComparison.Ordinal);

    var secondResolver = new RecordingFinalPathResolver(
        Path.Combine(_root, "WinHome", "state.json"));
    var second = await new LegacyStateMigrationAdapter(_root, secondResolver)
        .MigrateAsync(CancellationToken.None);

    Assert.False(second.MigrationPerformed);
    Assert.False(secondResolver.ObservedOpenReadableStream);
  }

  [Fact]
  public async Task MigrateAsync_ValidWinnerDuringDiagnosticCommitIsNotOverwritten()
  {
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    await File.WriteAllTextAsync(markerPath, "{");
    WriteLegacy("state.json", "[\"must-not-import\"]");
    var operations = new WinnerDuringDiagnosticMigrationFileOperations(
        CreateMarkerJson(["winner-step-b"]));

    var result = await new LegacyStateMigrationAdapter(
        _root,
        new RecordingFinalPathResolver(Path.Combine(_root, "WinHome", "state.json")),
        operations).MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Empty(result.ImportedStepNames);
    var persistedMarker = await File.ReadAllTextAsync(markerPath);
    Assert.Contains("winner-step-b", persistedMarker, StringComparison.Ordinal);
    Assert.DoesNotContain("must-not-import", persistedMarker, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MigrateAsync_OversizedInvalidMarkerDoesNotCreateDiagnostic()
  {
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    var oversizedMarker = new string('x', (128 * 1024) + 1);
    await File.WriteAllTextAsync(markerPath, oversizedMarker);
    WriteLegacy("state.json", "[\"must-not-import\"]");
    var operations = new TrackingMigrationFileOperations();

    var result = await new LegacyStateMigrationAdapter(
        _root,
        new RecordingFinalPathResolver(Path.Combine(_root, "WinHome", "state.json")),
        operations).MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Empty(result.ImportedStepNames);
    Assert.Equal(0, operations.DiagnosticCommitAttempts);
    Assert.Equal(oversizedMarker, await File.ReadAllTextAsync(markerPath));
    Assert.Empty(Directory.EnumerateFiles(
        markerDirectory,
        "migration-v1.invalid-*.json"));
  }

  [Fact]
  public async Task MigrateAsync_RejectsMarkerWhoseOpenedHandleResolvesElsewhere()
  {
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    var markerContents = CreateMarkerJson(["must-not-trust"]);
    await File.WriteAllTextAsync(markerPath, markerContents);
    WriteLegacy("state.json", "[\"must-not-import\"]");
    var markerResolver = new RecordingMarkerFinalPathResolver(
        Path.Combine(Path.GetTempPath(), "outside", "migration-v1.json"));

    var result = await new LegacyStateMigrationAdapter(
        _root,
        new RecordingFinalPathResolver(Path.Combine(_root, "WinHome", "state.json")),
        SystemLegacyMigrationFileOperations.Instance,
        markerResolver).MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Empty(result.ImportedStepNames);
    Assert.True(markerResolver.ObservedOpenReadableStream);
    Assert.Equal(markerContents, await File.ReadAllTextAsync(markerPath));
  }

  [Fact]
  public async Task MigrateAsync_DanglingMarkerSymlinkRemainsAHardGateWhenSupported()
  {
    var outside = Path.Combine(Path.GetTempPath(), $"wdem-marker-{Guid.NewGuid():N}");
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(outside);
    Directory.CreateDirectory(markerDirectory);
    var outsideMarker = Path.Combine(outside, "winner.json");
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    await File.WriteAllTextAsync(outsideMarker, CreateMarkerJson(["outside-step"]));
    try
    {
      try
      {
        File.CreateSymbolicLink(markerPath, outsideMarker);
      }
      catch (Exception exception) when (
          exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
      {
        return;
      }

      WriteLegacy("state.json", "[\"must-not-import\"]");
      var first = await new LegacyStateMigrationAdapter(_root)
          .MigrateAsync(CancellationToken.None);
      File.Delete(outsideMarker);
      var secondResolver = new RecordingFinalPathResolver(
          Path.Combine(_root, "WinHome", "state.json"));

      var second = await new LegacyStateMigrationAdapter(_root, secondResolver)
          .MigrateAsync(CancellationToken.None);

      Assert.False(first.MigrationPerformed);
      Assert.False(second.MigrationPerformed);
      Assert.False(secondResolver.ObservedOpenReadableStream);
    }
    finally
    {
      Directory.Delete(outside, recursive: true);
    }
  }

  [Fact]
  public async Task MigrateAsync_ConcurrentInstancesPerformMigrationOnce()
  {
    WriteLegacy("state.json", "[\"one-step\"]");
    var adapters = Enumerable.Range(0, 8)
        .Select(_ => new LegacyStateMigrationAdapter(_root))
        .ToArray();

    var results = await Task.WhenAll(
        adapters.Select(adapter => adapter.MigrateAsync(CancellationToken.None)));

    Assert.Single(results, result => result.MigrationPerformed);
    Assert.True(File.Exists(results[0].MarkerPath));
    Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "WDEM"), "*.tmp"));
  }

  [Fact]
  public async Task MigrateAsync_AtomicReplaceFailureLeavesNoValidMarker()
  {
    WriteLegacy("state.json", "[\"one-step\"]");
    var markerPath = Path.Combine(_root, "WDEM", "migration-v1.json");
    var adapter = new LegacyStateMigrationAdapter(
        _root,
        new RecordingFinalPathResolver(Path.Combine(_root, "WinHome", "state.json")),
        new AlwaysFailingMarkerCommitOperations());

    await Assert.ThrowsAnyAsync<IOException>(() =>
        adapter.MigrateAsync(CancellationToken.None));

    Assert.False(File.Exists(markerPath));
    Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "WDEM"), "*.tmp"));

    var secondResolver = new RecordingFinalPathResolver(
        Path.Combine(_root, "WinHome", "state.json"));
    var second = await new LegacyStateMigrationAdapter(_root, secondResolver)
        .MigrateAsync(CancellationToken.None);

    Assert.False(second.MigrationPerformed);
    Assert.False(secondResolver.ObservedOpenReadableStream);
    Assert.True(File.Exists(markerPath));
  }

  [Fact(Skip = "Requires an environment that permits directory symlink creation; deterministic handle-path coverage is mandatory below.")]
  public async Task MigrateAsync_DoesNotFollowReparsePointOutsideLegacyRoot()
  {
    var outside = Path.Combine(Path.GetTempPath(), $"wdem-outside-{Guid.NewGuid():N}");
    Directory.CreateDirectory(outside);
    await File.WriteAllTextAsync(Path.Combine(outside, "state.json"), "[\"outside-step\"]");
    Directory.CreateDirectory(_root);
    Directory.CreateSymbolicLink(Path.Combine(_root, "WinHome"), outside);

    try
    {
      var result = await new LegacyStateMigrationAdapter(_root)
          .MigrateAsync(CancellationToken.None);

      Assert.Empty(result.ImportedStepNames);
    }
    finally
    {
      Directory.Delete(outside, recursive: true);
    }
  }

  [Fact]
  public async Task MigrateAsync_ValidatesFinalPathFromOpenedHandleBeforeReading()
  {
    WriteLegacy("state.json", "[\"must-not-import\"]");
    var outsidePath = Path.Combine(Path.GetTempPath(), "outside", "state.json");
    var resolver = new RecordingFinalPathResolver(outsidePath);
    var adapter = new LegacyStateMigrationAdapter(_root, resolver);

    var result = await adapter.MigrateAsync(CancellationToken.None);

    Assert.True(resolver.ObservedOpenReadableStream);
    Assert.Empty(result.ImportedStepNames);
    Assert.DoesNotContain(
        "must-not-import",
        await File.ReadAllTextAsync(result.MarkerPath),
        StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(@"C:\Data\WinHome", @"\\?\C:\DATA\WinHome\state.json", true)]
  [InlineData(@"C:\Data\WinHome", @"\\?\C:\Data\WinHomeOther\state.json", false)]
  [InlineData(@"\\server\share\WinHome", @"\\?\UNC\SERVER\share\WinHome\state.json", true)]
  [InlineData(@"\\server\share\WinHome", @"\\?\UNC\server\share\WinHome2\state.json", false)]
  public void IsFinalPathWithinRoot_NormalizesExtendedCaseAndRootBoundary(
      string root,
      string candidate,
      bool expected)
  {
    Assert.Equal(
        expected,
        LegacyStateMigrationAdapter.IsFinalPathWithinRoot(root, candidate));
  }

  [Fact]
  public async Task MigrateAsync_FileAtMaximumSizeIsAccepted()
  {
    var json = "[\"boundary-step\"]";
    WriteLegacy(
        "state.json",
        json + new string(' ', LegacyStateMigrationAdapter.MaximumLegacyFileBytes - json.Length));

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.Equal(["boundary-step"], result.ImportedStepNames);
  }

  [Fact]
  public async Task MigrateAsync_FileOverMaximumSizeIsRejected()
  {
    var json = "[\"oversized-step\"]";
    WriteLegacy(
        "state.json",
        json + new string(
            ' ',
            LegacyStateMigrationAdapter.MaximumLegacyFileBytes + 1 - json.Length));

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.Empty(result.ImportedStepNames);
  }

  [Fact]
  public async Task MigrateAsync_ImportedNamesAreCappedInDeterministicSourceOrder()
  {
    var candidates = Enumerable.Range(
            0,
            LegacyStateMigrationAdapter.MaximumImportedStepNames + 5)
        .Select(index => $"step-{index:D3}")
        .ToArray();
    WriteLegacy("state.json", JsonSerializer.Serialize(candidates));

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.Equal(
        candidates.Take(LegacyStateMigrationAdapter.MaximumImportedStepNames),
        result.ImportedStepNames);
  }

  [Fact]
  public async Task MigrateAsync_RedactsFilteredDiscoveredNamesAndDoesNotRereadLegacy()
  {
    const string unsafeCandidate = "banana-coconut-20240829";
    WriteLegacy("state.json", JsonSerializer.Serialize(new[] { unsafeCandidate }));

    var first = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.True(first.MigrationPerformed);
    Assert.Equal(["Legacy item 1 (redacted)"], first.ImportedStepNames);
    var marker = await File.ReadAllTextAsync(first.MarkerPath);
    Assert.Contains("Legacy item 1 (redacted)", marker, StringComparison.Ordinal);
    Assert.DoesNotContain(unsafeCandidate, marker, StringComparison.Ordinal);
    Assert.DoesNotContain(
        unsafeCandidate.Length.ToString(),
        Assert.Single(first.ImportedStepNames),
        StringComparison.Ordinal);

    var resolver = new RecordingFinalPathResolver(
        Path.Combine(_root, "WinHome", "state.json"));
    var second = await new LegacyStateMigrationAdapter(_root, resolver)
        .MigrateAsync(CancellationToken.None);

    Assert.False(second.MigrationPerformed);
    Assert.Empty(second.ImportedStepNames);
    Assert.False(resolver.ObservedOpenReadableStream);
    Assert.Equal(marker, await File.ReadAllTextAsync(first.MarkerPath));
  }

  [Fact]
  public async Task MigrateAsync_DeduplicatesAndCapsRedactedPlaceholders()
  {
    const string repeatedUnsafeCandidate = "banana-coconut-20240829";
    WriteLegacy("state.json", JsonSerializer.Serialize(new
    {
      applied_items = new[] { repeatedUnsafeCandidate },
      step_history = new Dictionary<string, object>
      {
        ["explicit-name"] = new { stepName = repeatedUnsafeCandidate },
        [repeatedUnsafeCandidate] = new { status = "Failed" }
      }
    }));
    var unsafeCandidates = new[] { repeatedUnsafeCandidate }
        .Concat(Enumerable.Range(0, LegacyStateMigrationAdapter.MaximumImportedStepNames + 5)
            .Select(index => $"opaque-candidate-{index:D3}-20240829"));
    WriteLegacy("winhome.state.json", JsonSerializer.Serialize(unsafeCandidates));

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.Equal(
        Enumerable.Range(1, LegacyStateMigrationAdapter.MaximumImportedStepNames)
            .Select(index => $"Legacy item {index} (redacted)"),
        result.ImportedStepNames);
    var marker = await File.ReadAllTextAsync(result.MarkerPath);
    Assert.DoesNotContain(repeatedUnsafeCandidate, marker, StringComparison.Ordinal);
    Assert.DoesNotContain("opaque-candidate", marker, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MigrateAsync_RejectsUnsafeLabelsWithoutPersistingSensitiveOrPathData()
  {
    var overlong = new string('x', 257);
    WriteLegacy("state.json", JsonSerializer.Serialize(new object[]
    {
      "Install Git",
      "configure-shell",
      "Git.Git",
      "apiKey: super-secret",
      "password=\"quoted-secret\"",
      "Bearer abc.def.ghi",
      @"C:\Users\Jane\secret.txt",
      @"\\server\share\secret.txt",
      "/home/jane/.ssh/id_rsa",
      "control\u0001name",
      overlong,
      "ghp_1234567890abcdefghijklmnopqrstuvwxyz",
      "github_pat_1234567890_abcdefghijklmnopqrstuvwxyz",
      "AKIAIOSFODNN7EXAMPLE",
      "myAccessToken",
      "a8F3kP9zQ2mN7vR4xT6cL1wY",
      "0123456789abcdef0123456789abcdef01234567",
      "k7m2q9v4x8n3p6r1t5w0y2z7c4b9d6f3h8j1s5u0"
    }));
    WriteLegacy(".winhome-state.json", """
        {
          "safe-step": { "stepName": "Configure Shell" },
          "unsafe-path": { "stepName": "C:\\private\\token.txt" },
          "malicious": { "stepName": { "nested": "super-secret" } }
        }
        """);
    var adapter = new LegacyStateMigrationAdapter(_root);

    var result = await adapter.MigrateAsync(CancellationToken.None);

    Assert.Equal(
        ["Configure Shell", "Install Git", "configure-shell", "Git.Git"],
        result.ImportedStepNames.Where(name => !name.StartsWith("Legacy item ")));
    Assert.Equal(
        Enumerable.Range(1, 16).Select(index => $"Legacy item {index} (redacted)"),
        result.ImportedStepNames.Where(name => name.StartsWith("Legacy item ")));
    var marker = await File.ReadAllTextAsync(result.MarkerPath);
    foreach (var forbidden in new[]
    {
      "super-secret", "quoted-secret", "abc.def.ghi", "Jane", "server", "jane",
      "control", "nested", overlong, "ghp_", "github_pat_", "AKIA",
      "myAccessToken", "a8F3kP9zQ2mN7vR4xT6cL1wY",
      "0123456789abcdef0123456789abcdef01234567",
      "k7m2q9v4x8n3p6r1t5w0y2z7c4b9d6f3h8j1s5u0"
    })
    {
      Assert.DoesNotContain(forbidden, marker, StringComparison.OrdinalIgnoreCase);
    }
  }

  [Theory]
  [InlineData("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0In0.abcdefghijklmnopqrstuvwxyz_01234")]
  [InlineData("k7m2q9v4x8n3p6r1t5w0-y2z7c4b9d6f3h8j1s5u0")]
  [InlineData("k7m2q9v4x8n3p6r1t5w0_y2z7c4b9d6f3h8j1s5u0")]
  [InlineData("k7m2q9v4x8n3p6r1-t5w0y2z7c4b9d6f3-h8j1s5u0a2c7e4g9")]
  [InlineData("k7m2q9v4x8n3p6r1.t5w0y2z7c4b9d6f3.h8j1s5u0a2c7e4g9")]
  [InlineData("k7m2q9v4x8n3p6r1_t5w0y2z7c4b9d6f3_h8j1s5u0a2c7e4g9")]
  [InlineData("550e8400-e29b-41d4-a716-446655440000")]
  [InlineData("qzxvbnmasdfghjkl-1234567890123456-plmoknijbuhvygct")]
  [InlineData("com.k7m2q9v4x8n3p6r1t5w0y2z7c4b9d6f3.pkg")]
  [InlineData("com.k7m2q9v4x8n3p6r1-t5w0y2z7c4b9d6f3.pkg")]
  [InlineData("banana-coconut-20240829")]
  [InlineData("zaneku-morapi-tuvexo-20240829")]
  public async Task MigrateAsync_RejectsDelimitedOpaqueCredentials(string credential)
  {
    WriteLegacy("state.json", JsonSerializer.Serialize(new[] { credential }));

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.Equal(["Legacy item 1 (redacted)"], result.ImportedStepNames);
    Assert.DoesNotContain(
        credential,
        await File.ReadAllTextAsync(result.MarkerPath),
        StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("banana-coconut-20240829")]
  [InlineData("zaneku-morapi-tuvexo-20240829")]
  public async Task MigrateAsync_RejectsOpaqueCredentialsFromEveryLegacyNameSource(
      string credential)
  {
    WriteLegacy("state.json", JsonSerializer.Serialize(new
    {
      applied_items = new[] { credential },
      step_history = new Dictionary<string, object>
      {
        ["explicit-name"] = new { stepName = credential },
        [credential] = new { status = "Failed" }
      }
    }));
    WriteLegacy("winhome.state.json", JsonSerializer.Serialize(new[] { credential }));

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.Equal(["Legacy item 1 (redacted)"], result.ImportedStepNames);
    Assert.DoesNotContain(
        credential,
        await File.ReadAllTextAsync(result.MarkerPath),
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task MigrateAsync_PreservesKnownManagerIdentifiersFromAppliedItems()
  {
    var managedIdentifiers = new[]
    {
      "winget:TestApp",
      "winget:Microsoft.VisualStudioCode",
      "choco:git",
      "chocolatey:nodejs",
      "scoop:neovim"
    };
    WriteLegacy(
        "state.json",
        JsonSerializer.Serialize(new { applied_items = managedIdentifiers }));

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.Equal(managedIdentifiers, result.ImportedStepNames);
  }

  [Fact]
  public async Task MigrateAsync_PreservesKnownManagerIdentifiersFromStepHistoryKeys()
  {
    var managedIdentifiers = new[]
    {
      "winget:TestApp",
      "winget:Microsoft.VisualStudioCode",
      "choco:git",
      "chocolatey:nodejs",
      "scoop:neovim"
    };
    WriteLegacy(
        "state.json",
        JsonSerializer.Serialize(new
        {
          step_history = managedIdentifiers.ToDictionary(
              identifier => identifier,
              _ => new { status = "Succeeded" })
        }));

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.Equal(managedIdentifiers, result.ImportedStepNames);
  }

  [Theory]
  [InlineData("winget:banana-coconut-20240829")]
  [InlineData("winget:k7m2q9v4x8n3p6r1t5w0y2z7c4b9d6f3h8j1s5u0")]
  [InlineData("scoop:eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.abcdefghijklmnopqrstuvwxyz_01234")]
  [InlineData("choco:ghp_1234567890abcdefghijklmnopqrstuvwxyz")]
  [InlineData("chocolatey:Bearer abc.def.ghi")]
  [InlineData("winget:password=super-secret")]
  [InlineData("winget:550e8400-e29b-41d4-a716-446655440000")]
  [InlineData("unknown:Microsoft.VisualStudioCode")]
  public async Task MigrateAsync_RejectsUnsafeOrUnknownManagerIdentifiers(
      string identifier)
  {
    WriteLegacy("state.json", JsonSerializer.Serialize(new
    {
      applied_items = new[] { identifier },
      step_history = new Dictionary<string, object>
      {
        [identifier] = new { status = "Failed" }
      }
    }));

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.Equal(["Legacy item 1 (redacted)"], result.ImportedStepNames);
    Assert.DoesNotContain(
        identifier,
        await File.ReadAllTextAsync(result.MarkerPath),
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task MigrateAsync_PreservesLegitimateProductAndPackageLabels()
  {
    var legitimateLabels = new[]
    {
      "1Password",
      "winget:Git.Git",
      "Microsoft.VisualStudio.2022.BuildTools",
      "Microsoft.VisualStudio.BuildTools-2022",
      "com.microsoft.visualstudiobuildtools",
      "ai.openai.chatgptdesktop",
      "visual-studio-build-tools-2022",
      "visual-studio-build-tools-v2022",
      "visual-studio-build-tools-rc1",
      "visual-studio-build-tools-x64",
      "visual-studio-build-tools-win11",
      "VisualStudioBuildTools2022",
      "VisualStudio17BuildTools2022x64"
    };
    WriteLegacy(
        "state.json",
        JsonSerializer.Serialize(new { applied_items = legitimateLabels }));

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.Equal(legitimateLabels, result.ImportedStepNames);
  }

  [Fact]
  public async Task MigrateAsync_ValidMarkerWithLegitimateLabelsIsNotQuarantined()
  {
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    await File.WriteAllTextAsync(markerPath, """
        {
          "schemaVersion": 1,
          "recordKind": "legacy-step-name-reference",
          "sourceProduct": "WinHome",
          "importedAtUtc": "2026-08-29T00:00:00Z",
          "importedStepNames": [
            "1Password",
            "winget:Git.Git",
            "Microsoft.VisualStudio.2022.BuildTools",
            "Microsoft.VisualStudio.BuildTools-2022",
            "com.microsoft.visualstudiobuildtools",
            "ai.openai.chatgptdesktop",
            "visual-studio-build-tools-2022",
            "visual-studio-build-tools-v2022",
            "visual-studio-build-tools-rc1",
            "visual-studio-build-tools-x64",
            "visual-studio-build-tools-win11",
            "VisualStudioBuildTools2022",
            "VisualStudio17BuildTools2022x64"
          ]
        }
        """);
    WriteLegacy("state.json", "[\"must-not-import\"]");

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Empty(Directory.EnumerateFiles(
        markerDirectory,
        "migration-v1.invalid-*.json"));
    Assert.Contains("1Password", await File.ReadAllTextAsync(markerPath));
    Assert.Contains("winget:Git.Git", await File.ReadAllTextAsync(markerPath));
    Assert.Contains(
        "VisualStudio17BuildTools2022x64",
        await File.ReadAllTextAsync(markerPath));
    Assert.Contains(
        "Microsoft.VisualStudio.BuildTools-2022",
        await File.ReadAllTextAsync(markerPath));
    Assert.Contains(
        "com.microsoft.visualstudiobuildtools",
        await File.ReadAllTextAsync(markerPath));
    Assert.Contains(
        "ai.openai.chatgptdesktop",
        await File.ReadAllTextAsync(markerPath));
    Assert.Contains(
        "visual-studio-build-tools-2022",
        await File.ReadAllTextAsync(markerPath));
    Assert.Contains(
        "visual-studio-build-tools-v2022",
        await File.ReadAllTextAsync(markerPath));
    Assert.Contains(
        "visual-studio-build-tools-rc1",
        await File.ReadAllTextAsync(markerPath));
    Assert.Contains(
        "visual-studio-build-tools-x64",
        await File.ReadAllTextAsync(markerPath));
    Assert.Contains(
        "visual-studio-build-tools-win11",
        await File.ReadAllTextAsync(markerPath));
  }

  [Theory]
  [InlineData("banana-coconut-20240829")]
  [InlineData("zaneku-morapi-tuvexo-20240829")]
  [InlineData("winget:banana-coconut-20240829")]
  [InlineData("scoop:eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.abcdefghijklmnopqrstuvwxyz_01234")]
  [InlineData("choco:ghp_1234567890abcdefghijklmnopqrstuvwxyz")]
  [InlineData("chocolatey:Bearer abc.def.ghi")]
  [InlineData("unknown:Microsoft.VisualStudioCode")]
  public async Task MigrateAsync_InvalidCredentialMarkerRecordsSafeDiagnostic(
      string credential)
  {
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    var invalidMarker = JsonSerializer.Serialize(new
    {
      schemaVersion = 1,
      recordKind = "legacy-step-name-reference",
      sourceProduct = "WinHome",
      importedAtUtc = "2026-08-29T00:00:00Z",
      importedStepNames = new[] { credential }
    });
    await File.WriteAllTextAsync(markerPath, invalidMarker);
    WriteLegacy("state.json", "[\"recovered-step\"]");
    var legacyPath = Path.Combine(_root, "WinHome", "state.json");
    var resolver = new RecordingFinalPathResolver(legacyPath);

    var result = await new LegacyStateMigrationAdapter(_root, resolver)
        .MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Empty(result.ImportedStepNames);
    Assert.False(resolver.ObservedOpenReadableStream);
    Assert.Single(Directory.EnumerateFiles(
        markerDirectory,
        "migration-v1.invalid-*.json"));
    Assert.Equal(invalidMarker, await File.ReadAllTextAsync(markerPath));
    Assert.Equal(
        "{\"recordKind\":\"invalid-migration-marker\",\"legacyImportDisabled\":true}\n",
        await File.ReadAllTextAsync(Directory.EnumerateFiles(
            markerDirectory,
            "migration-v1.invalid-*.json").Single()));
  }

  [Fact]
  public async Task MigrateAsync_ValidMarkerPreservesKnownManagerIdentifiers()
  {
    var managedIdentifiers = new[]
    {
      "winget:TestApp",
      "winget:Microsoft.VisualStudioCode",
      "choco:git",
      "chocolatey:nodejs",
      "scoop:neovim"
    };
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(new
    {
      schemaVersion = 1,
      recordKind = "legacy-step-name-reference",
      sourceProduct = "WinHome",
      importedAtUtc = "2026-08-29T00:00:00Z",
      importedStepNames = managedIdentifiers
    }));
    WriteLegacy("state.json", "[\"must-not-import\"]");

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Empty(Directory.EnumerateFiles(
        markerDirectory,
        "migration-v1.invalid-*.json"));
    Assert.Equal(
        managedIdentifiers,
        JsonDocument.Parse(await File.ReadAllTextAsync(markerPath))
            .RootElement.GetProperty("importedStepNames")
            .EnumerateArray()
            .Select(item => item.GetString()));
  }

  private sealed class RecordingFinalPathResolver(string finalPath) :
      ILegacyFileFinalPathResolver
  {
    public bool ObservedOpenReadableStream { get; private set; }
    public int OpenReadableStreamCount { get; private set; }

    public string ResolveFinalPath(FileStream stream, string requestedPath)
    {
      ObservedOpenReadableStream = stream.CanRead && !stream.SafeFileHandle.IsClosed;
      if (ObservedOpenReadableStream)
      {
        OpenReadableStreamCount++;
      }

      return finalPath;
    }
  }

  private sealed class FixedMigrationPathEntryProbe(MigrationPathEntryState state) :
      IMigrationPathEntryProbe
  {
    public MigrationPathEntryState Probe(string path) => state;
  }

  public enum MigrationFailureStage
  {
    DiagnosticCommit
  }

  private sealed class FaultInjectingMigrationFileOperations(
      MigrationFailureStage failureStage,
      bool cancel) : ILegacyMigrationFileOperations
  {
    public void CommitInvalidMarkerDiagnostic(
        string temporaryPath,
        string diagnosticPath)
    {
      ThrowIfRequested(MigrationFailureStage.DiagnosticCommit);
      File.Move(temporaryPath, diagnosticPath, overwrite: false);
    }

    public void CommitNewMarker(string temporaryPath, string markerPath) =>
        File.Move(temporaryPath, markerPath, overwrite: false);

    private void ThrowIfRequested(MigrationFailureStage currentStage)
    {
      if (failureStage != currentStage)
      {
        return;
      }

      if (cancel)
      {
        throw new OperationCanceledException(new CancellationToken(canceled: true));
      }

      throw new IOException($"Injected {currentStage} failure.");
    }
  }

  private sealed class ThrowingDiagnosticMigrationFileOperations :
      ILegacyMigrationFileOperations
  {
    public int DiagnosticCommitAttempts { get; private set; }

    public void CommitInvalidMarkerDiagnostic(
        string temporaryPath,
        string diagnosticPath)
    {
      DiagnosticCommitAttempts++;
      throw new IOException("Injected read-only diagnostic directory.");
    }

    public void CommitNewMarker(string temporaryPath, string markerPath) =>
        File.Move(temporaryPath, markerPath, overwrite: false);
  }

  private sealed class CompetingMigrationFileOperations(string winnerMarker) :
      ILegacyMigrationFileOperations
  {
    public void CommitInvalidMarkerDiagnostic(
        string temporaryPath,
        string diagnosticPath) =>
        File.Move(temporaryPath, diagnosticPath, overwrite: false);

    public void CommitNewMarker(string temporaryPath, string markerPath)
    {
      File.WriteAllText(markerPath, winnerMarker);
      throw new IOException("Injected marker commit conflict.");
    }
  }

  private sealed class DisappearingWinnerMigrationFileOperations :
      ILegacyMigrationFileOperations
  {
    public int CommitAttempts { get; private set; }

    public void CommitInvalidMarkerDiagnostic(
        string temporaryPath,
        string diagnosticPath) =>
        File.Move(temporaryPath, diagnosticPath, overwrite: false);

    public void CommitNewMarker(string temporaryPath, string markerPath)
    {
      CommitAttempts++;
      if (CommitAttempts == 1)
      {
        File.WriteAllText(markerPath, CreateMarkerJson(["vanished-winner"]));
        File.Delete(markerPath);
        throw new IOException("Injected disappearing marker commit conflict.");
      }

      File.Move(temporaryPath, markerPath, overwrite: false);
    }
  }

  private sealed class WinnerDuringDiagnosticMigrationFileOperations(
      string winnerMarker) : ILegacyMigrationFileOperations
  {
    public void CommitInvalidMarkerDiagnostic(
        string temporaryPath,
        string diagnosticPath)
    {
      File.Move(temporaryPath, diagnosticPath, overwrite: false);
      File.WriteAllText(
          Path.Combine(Path.GetDirectoryName(diagnosticPath)!, "migration-v1.json"),
          winnerMarker);
    }

    public void CommitNewMarker(string temporaryPath, string markerPath) =>
        File.Move(temporaryPath, markerPath, overwrite: false);
  }

  private sealed class TrackingMigrationFileOperations : ILegacyMigrationFileOperations
  {
    public int DiagnosticCommitAttempts { get; private set; }

    public void CommitInvalidMarkerDiagnostic(
        string temporaryPath,
        string diagnosticPath)
    {
      DiagnosticCommitAttempts++;
      File.Move(temporaryPath, diagnosticPath, overwrite: false);
    }

    public void CommitNewMarker(string temporaryPath, string markerPath) =>
        File.Move(temporaryPath, markerPath, overwrite: false);
  }

  private sealed class AlwaysFailingMarkerCommitOperations :
      ILegacyMigrationFileOperations
  {
    public void CommitInvalidMarkerDiagnostic(
        string temporaryPath,
        string diagnosticPath) =>
        File.Move(temporaryPath, diagnosticPath, overwrite: false);

    public void CommitNewMarker(string temporaryPath, string markerPath) =>
        throw new IOException("Injected persistent marker commit failure.");
  }

  private sealed class RecordingMarkerFinalPathResolver(string finalPath) :
      IMigrationMarkerFinalPathResolver
  {
    public bool ObservedOpenReadableStream { get; private set; }

    public string ResolveFinalPath(FileStream stream, string requestedPath)
    {
      ObservedOpenReadableStream = stream.CanRead && !stream.SafeFileHandle.IsClosed;
      return finalPath;
    }
  }

  private sealed class MutationAttemptingMarkerFinalPathResolver(
      string replacementMarker) : IMigrationMarkerFinalPathResolver
  {
    public bool DeleteRejected { get; private set; }
    public bool ReplaceRejected { get; private set; }

    public string ResolveFinalPath(FileStream stream, string requestedPath)
    {
      var replacementPath = $"{requestedPath}.{Guid.NewGuid():N}.replacement";
      File.WriteAllText(replacementPath, replacementMarker);
      try
      {
        File.Delete(requestedPath);
      }
      catch (Exception exception) when (
          exception is IOException or UnauthorizedAccessException)
      {
        DeleteRejected = true;
      }

      try
      {
        File.Move(replacementPath, requestedPath, overwrite: true);
      }
      catch (Exception exception) when (
          exception is IOException or UnauthorizedAccessException)
      {
        ReplaceRejected = true;
      }
      finally
      {
        File.Delete(replacementPath);
      }

      return Path.GetFullPath(requestedPath);
    }
  }

  private static string CreateMarkerJson(IReadOnlyList<string> importedStepNames) =>
      JsonSerializer.Serialize(new
      {
        schemaVersion = 1,
        recordKind = "legacy-step-name-reference",
        sourceProduct = "WinHome",
        importedAtUtc = "2026-08-29T00:00:00Z",
        importedStepNames
      });

  private void WriteLegacy(string fileName, string contents)
  {
    var directory = Path.Combine(_root, "WinHome");
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, fileName), contents);
  }

  public void Dispose()
  {
    if (Directory.Exists(_root))
    {
      Directory.Delete(_root, recursive: true);
    }
  }
}
