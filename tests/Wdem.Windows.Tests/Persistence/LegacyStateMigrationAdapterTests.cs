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
        ["First step", "step-two", "array-step"],
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

  [Theory]
  [InlineData("")]
  [InlineData("{")]
  [InlineData("{ \"schemaVersion\": 2, \"recordKind\": \"legacy-step-name-reference\", \"sourceProduct\": \"WinHome\", \"importedAtUtc\": \"2026-08-29T00:00:00Z\", \"importedStepNames\": [] }")]
  [InlineData("{ \"schemaVersion\": 1, \"recordKind\": \"unrelated\", \"sourceProduct\": \"Other\", \"importedAtUtc\": \"2026-08-29T00:00:00Z\", \"importedStepNames\": [] }")]
  public async Task MigrateAsync_InvalidMarkerIsQuarantinedAndMigrationIsRetried(
      string invalidMarker)
  {
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    await File.WriteAllTextAsync(markerPath, invalidMarker);
    WriteLegacy("state.json", "[\"recovered-step\"]");

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.True(result.MigrationPerformed);
    Assert.Equal(["recovered-step"], result.ImportedStepNames);
    Assert.Single(Directory.EnumerateFiles(
        markerDirectory,
        "migration-v1.invalid-*.json"));
    using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath));
    Assert.Equal(1, marker.RootElement.GetProperty("schemaVersion").GetInt32());
    Assert.Equal(
        "legacy-step-name-reference",
        marker.RootElement.GetProperty("recordKind").GetString());
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
    Directory.CreateDirectory(markerPath);
    var adapter = new LegacyStateMigrationAdapter(_root);

    await Assert.ThrowsAnyAsync<IOException>(() =>
        adapter.MigrateAsync(CancellationToken.None));

    Assert.False(File.Exists(markerPath));
    Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "WDEM"), "*.tmp"));
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
        result.ImportedStepNames);
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
  public async Task MigrateAsync_RejectsDelimitedOpaqueCredentials(string credential)
  {
    WriteLegacy("state.json", JsonSerializer.Serialize(new[] { credential }));

    var result = await new LegacyStateMigrationAdapter(_root)
        .MigrateAsync(CancellationToken.None);

    Assert.Empty(result.ImportedStepNames);
    Assert.DoesNotContain(
        credential,
        await File.ReadAllTextAsync(result.MarkerPath),
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task MigrateAsync_PreservesLegitimateProductAndPackageLabels()
  {
    var legitimateLabels = new[]
    {
      "1Password",
      "Microsoft.VisualStudio.2022.BuildTools",
      "Microsoft.VisualStudio.BuildTools-2022",
      "com.microsoft.visualstudiobuildtools",
      "VisualStudioBuildTools2022",
      "VisualStudio17BuildTools2022x64"
    };
    WriteLegacy("state.json", JsonSerializer.Serialize(legitimateLabels));

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
            "Microsoft.VisualStudio.2022.BuildTools",
            "Microsoft.VisualStudio.BuildTools-2022",
            "com.microsoft.visualstudiobuildtools",
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
    Assert.Contains(
        "VisualStudio17BuildTools2022x64",
        await File.ReadAllTextAsync(markerPath));
    Assert.Contains(
        "Microsoft.VisualStudio.BuildTools-2022",
        await File.ReadAllTextAsync(markerPath));
    Assert.Contains(
        "com.microsoft.visualstudiobuildtools",
        await File.ReadAllTextAsync(markerPath));
  }

  private sealed class RecordingFinalPathResolver(string finalPath) :
      ILegacyFileFinalPathResolver
  {
    public bool ObservedOpenReadableStream { get; private set; }

    public string ResolveFinalPath(FileStream stream, string requestedPath)
    {
      ObservedOpenReadableStream = stream.CanRead && !stream.SafeFileHandle.IsClosed;
      return finalPath;
    }
  }

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
