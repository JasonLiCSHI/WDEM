using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Composition;
using Wdem.Windows.Persistence;
using Wdem.Windows.Providers;
using Wdem.Windows.Security;
using Wdem.Windows.Tests.Security;
using Wdem.Windows.VisualStudio;
using Xunit;

namespace Wdem.Windows.Tests.Providers;

public sealed partial class VisualStudioExtensionProviderTests
{
  [Fact]
  public async Task DetectAsync_OmittedInstanceIdSelectsOneCompleteConstrainedInstance()
  {
    var selected = Instance("17.real");
    var incompatible = Instance("17.other") with { Edition = "Professional" };
    var provider = Provider(
        new FakeVsixManifestReader(),
        new ThrowingProcessExecutor(),
        discovery: new FakeVisualStudioDiscovery(selected, incompatible));
    var resource = WithOptionalInstanceSelector(
        ExtensionResource("Contoso.DeveloperTools", "3.2.x", "placeholder"));

    var state = await provider.DetectAsync(resource, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.False(state.Exists);
    Assert.Equal("17.real", state.Evidence["visualStudioInstanceId"]);
  }

  [Fact]
  public async Task DetectAsync_AmbiguousOptionalInstanceSelectorReportsCandidateIds()
  {
    var provider = Provider(
        new FakeVsixManifestReader(),
        new ThrowingProcessExecutor(),
        discovery: new FakeVisualStudioDiscovery(Instance("17.a"), Instance("17.b")));
    var resource = WithOptionalInstanceSelector(
        ExtensionResource("Contoso.DeveloperTools", "3.2.x", "placeholder"));

    var state = await provider.DetectAsync(resource, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.Contains("17.a", state.StructuredError!.Detail, StringComparison.Ordinal);
    Assert.Contains("17.b", state.StructuredError.Detail, StringComparison.Ordinal);
  }
  [Fact]
  public async Task DetectAsync_UsesManifestIdentityAndTargetVisualStudioInstance()
  {
    var manifests = new FakeVsixManifestReader();
    manifests.Add(
        @"C:\Extensions\company\extension.vsixmanifest",
        "Contoso.DeveloperTools",
        "3.2.0",
        "17.0_a");
    manifests.Add(
        @"D:\Different\extension.vsixmanifest",
        "Contoso.DeveloperTools",
        "9.0.0",
        "17.0_b");
    var provider = Provider(manifests, new ThrowingProcessExecutor());

    var state = await provider.DetectAsync(
        ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a"),
        CancellationToken.None);

    Assert.True(state.Exists);
    Assert.Equal("3.2.0", state.Version);
    Assert.Equal("17.0_a", state.Evidence["visualStudioInstanceId"]);
    Assert.Equal("Contoso.DeveloperTools", state.Evidence["extensionId"]);
    Assert.Equal(
        @"C:\Extensions\company\extension.vsixmanifest",
        state.Evidence["manifestPath"]);
  }

  [Fact]
  public async Task DetectAsync_IdentityIsStableAcrossInstallPaths()
  {
    var first = new FakeVsixManifestReader();
    first.Add(@"C:\One\extension.vsixmanifest", "Contoso.DeveloperTools", "3.2.0", "17.0_a");
    var second = new FakeVsixManifestReader();
    second.Add(@"D:\Two\renamed.vsixmanifest", "Contoso.DeveloperTools", "3.2.0", "17.0_a");
    var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a");

    var firstState = await Provider(first, new ThrowingProcessExecutor())
        .DetectAsync(resource, CancellationToken.None);
    var secondState = await Provider(second, new ThrowingProcessExecutor())
        .DetectAsync(resource, CancellationToken.None);

    Assert.Equal(firstState.Version, secondState.Version);
    Assert.Equal(firstState.Evidence["extensionId"], secondState.Evidence["extensionId"]);
  }

  [Fact]
  public async Task DetectAsync_RealReaderKeepsIdentityStableAndExcludesOtherInstanceProfile()
  {
    var firstRoot = Path.Combine(Path.GetTempPath(), $"wdem-provider-reader-a-{Guid.NewGuid():N}");
    var secondRoot = Path.Combine(Path.GetTempPath(), $"wdem-provider-reader-b-{Guid.NewGuid():N}");
    try
    {
      var firstPath = ProfileManifestPath(firstRoot, "17.0_a", "random", "extension.vsixmanifest");
      var secondPath = ProfileManifestPath(secondRoot, "17.0_a", "different", "renamed.vsixmanifest");
      var wrongPath = ProfileManifestPath(firstRoot, "17.0_b", "same-id", "extension.vsixmanifest");
      var unrelatedInvalidPath = ProfileManifestPath(
          firstRoot,
          "17.0_a",
          "unrelated-invalid",
          "extension.vsixmanifest");
      Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
      Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
      Directory.CreateDirectory(Path.GetDirectoryName(wrongPath)!);
      Directory.CreateDirectory(Path.GetDirectoryName(unrelatedInvalidPath)!);
      await File.WriteAllTextAsync(firstPath, InstalledManifest("3.2.0"));
      await File.WriteAllTextAsync(secondPath, InstalledManifest("3.2.0"));
      await File.WriteAllTextAsync(wrongPath, InstalledManifest("9.0.0"));
      await File.WriteAllTextAsync(
          unrelatedInvalidPath,
          InstalledManifest("invalid").Replace(
              "Contoso.DeveloperTools",
              "Unrelated.Extension",
              StringComparison.Ordinal));
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "a");

      var firstState = await RealReaderProvider(firstRoot).DetectAsync(resource, CancellationToken.None);
      var secondState = await RealReaderProvider(secondRoot).DetectAsync(resource, CancellationToken.None);

      Assert.Equal(DetectionOutcome.Succeeded, firstState.Outcome);
      Assert.Equal("3.2.0", firstState.Version);
      Assert.Equal(firstState.Version, secondState.Version);
      Assert.Equal(firstState.Evidence["extensionId"], secondState.Evidence["extensionId"]);
    }
    finally
    {
      if (Directory.Exists(firstRoot))
      {
        Directory.Delete(firstRoot, recursive: true);
      }

      if (Directory.Exists(secondRoot))
      {
        Directory.Delete(secondRoot, recursive: true);
      }
    }
  }

  [Fact]
  public async Task DetectAsync_RealReaderFailsForInvalidManifestClaimingRequestedIdentity()
  {
    var root = Path.Combine(Path.GetTempPath(), $"wdem-provider-invalid-{Guid.NewGuid():N}");
    var path = ProfileManifestPath(root, "17.0_a", "candidate", "extension.vsixmanifest");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, InstalledManifest("invalid"));
    try
    {
      var state = await RealReaderProvider(root).DetectAsync(
          ExtensionResource("Contoso.DeveloperTools", "3.2.x", "a"),
          CancellationToken.None);

      Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }
  [Fact]
  public async Task DetectAsync_RealReaderFailsWhenAmbiguousIdentityIncludesRequestedId()
  {
    var root = Path.Combine(Path.GetTempPath(), $"wdem-provider-ambiguous-{Guid.NewGuid():N}");
    var path = ProfileManifestPath(root, "17.0_a", "candidate", "extension.vsixmanifest");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var manifest = InstalledManifest("3.2.0")
        .Replace("<PackageManifest ", "<PackageManifest xmlns:other=\"urn:other\" ", StringComparison.Ordinal)
        .Replace(
            "Identity Id=\"Contoso.DeveloperTools\"",
            "Identity Id=\"Contoso.DeveloperTools\" other:Id=\"Other.Extension\"",
            StringComparison.Ordinal);
    await File.WriteAllTextAsync(path, manifest);
    try
    {
      var state = await RealReaderProvider(root).DetectAsync(
          ExtensionResource("Contoso.DeveloperTools", "3.2.x", "a"),
          CancellationToken.None);

      Assert.Equal(DetectionOutcome.Failed, state.Outcome);
      Assert.Equal(WdemErrorCode.DetectionError, state.StructuredError!.Code);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task DetectAndVerify_RejectInstalledManifestIncompatibleWithSelectedInstance()
  {
    var manifests = new FakeVsixManifestReader();
    manifests.Add(
        @"C:\VS\17.0_a\Common7\IDE\Extensions\Contoso\extension.vsixmanifest",
        "Contoso.DeveloperTools",
        "3.2.0",
        "17.0_a",
        [new VsixInstallationTarget("Microsoft.VisualStudio.Enterprise", "[17.0,18.0)")]);
    var provider = Provider(manifests, new ThrowingProcessExecutor());
    var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a");

    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var verification = await provider.VerifyAsync(resource, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, detected.Outcome);
    Assert.Equal(ComplianceStatus.DetectionFailed, verification.Compliance);
  }
  [Fact]
  public async Task PlanArtifactStore_ActivationProofIsOnlyPublishedInSuccessfulLocator()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    try
    {
      var staged = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: new TestPlanArtifactRevocationStore(revocationPath))
          .StageAsync(
              "extension",
              stager.StagedPath,
              new string('A', 64),
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      var locator = Assert.IsType<string>(staged.StepEvidence);
      var activationProof = locator.Split(':')[3];
      var directory = Path.GetDirectoryName(stager.VerifiedVsixPath)!;

      Assert.Equal(43, activationProof.Length);
      Assert.DoesNotContain(
          activationProof,
          await File.ReadAllTextAsync(Path.Combine(directory, ".wdem-vsix-owner")),
          StringComparison.Ordinal);
      Assert.False(File.Exists(revocationPath));
    }
    finally
    {
      File.Delete(revocationPath);
    }
  }
  [Fact]
  public async Task PlanArtifactStore_NegativeUptimeCannotReplayAfterWallClockRollback()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var bootIdentifier = Guid.Parse("00112233-4455-6677-8899-AABBCCDDEEFF");
      var staged = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              handoffLifetime: TimeSpan.FromHours(1),
              deleteDirectory: static _ => { },
              revocationStore: revocationStore,
              getBootIdentifier: () => bootIdentifier,
              getUptimeMilliseconds: () => 10_000)
          .StageAsync(
              "extension",
              stager.StagedPath,
              new string('A', 64),
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);

      var replay = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              getUtcNow: () => DateTimeOffset.UtcNow.AddDays(-1),
              deleteDirectory: static _ => { },
              revocationStore: revocationStore,
              getBootIdentifier: () => bootIdentifier,
              getUptimeMilliseconds: () => -1)
          .ClaimAsync(
              "extension",
              staged.StepEvidence!,
              new string('A', 64),
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      replayedArtifact = replay.Artifact;

      Assert.Null(replay.Artifact);
      Assert.NotNull(replay.Error);
    }
    finally
    {
      if (replayedArtifact is not null)
      {
        await replayedArtifact.DisposeAsync();
      }

      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_DeadlineOverflowFailsClosedDuringStage()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var store = PlanArtifactStore(
        stager,
        verifier,
        manifests,
        handoffLifetime: TimeSpan.FromMilliseconds(1),
        getUptimeMilliseconds: () => long.MaxValue);

    var staged = await store.StageAsync(
        "extension",
        stager.StagedPath,
        new string('A', 64),
        VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
        CancellationToken.None);

    Assert.Null(staged.StepEvidence);
    Assert.NotNull(staged.Error);
  }

  [Fact]
  public async Task PlanArtifactStore_TimerRevokesWhenUptimeBecomesNegative()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var cleanupAttempted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var uptimeReadCount = 0;
    try
    {
      var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          handoffLifetime: TimeSpan.FromHours(1),
          deleteDirectory: _ => cleanupAttempted.TrySetResult(),
          revocationStore: revocationStore,
          getUptimeMilliseconds: () =>
              Interlocked.Increment(ref uptimeReadCount) == 1 ? 10_000 : -1);

      var staged = await store.StageAsync(
          "extension",
          stager.StagedPath,
          new string('A', 64),
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);

      Assert.NotNull(staged.StepEvidence);
      await cleanupAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));
      Assert.True(File.Exists(TerminalStatePath(
          Path.GetDirectoryName(stager.VerifiedVsixPath)!,
          staged.StepEvidence!)));
    }
    finally
    {
      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_ActiveLocatorCannotReplayAfterSystemBootChanges()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var issuedBootIdentifier = Guid.Parse("00112233-4455-6677-8899-AABBCCDDEEFF");
      var staged = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              handoffLifetime: TimeSpan.FromHours(1),
              deleteDirectory: static _ => { },
              revocationStore: revocationStore,
              getBootIdentifier: () => issuedBootIdentifier,
              getUptimeMilliseconds: () => 10_000)
          .StageAsync(
              "extension",
              stager.StagedPath,
              new string('A', 64),
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);

      var replay = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              deleteDirectory: static _ => { },
              revocationStore: revocationStore,
              getBootIdentifier: () => Guid.Parse("11223344-5566-7788-99AA-BBCCDDEEFF00"),
              getUptimeMilliseconds: () => 1)
          .ClaimAsync(
              "extension",
              staged.StepEvidence!,
              new string('A', 64),
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      replayedArtifact = replay.Artifact;

      Assert.Null(replay.Artifact);
      Assert.NotNull(replay.Error);
    }
    finally
    {
      if (replayedArtifact is not null)
      {
        await replayedArtifact.DisposeAsync();
      }

      File.Delete(revocationPath);
    }
  }
  [Theory]
  [InlineData("vsix-v2:")]
  [InlineData("vsix-v2:00000000000000000000000000000000:not-a-guid")]
  [InlineData("extension:install:vsix-v2:00000000000000000000000000000000:00000000000000000000000000000000")]
  public void HasValidStepEvidence_MalformedLocatorReturnsFalse(string locator)
  {
    var valid = VsixPlanArtifactStore.HasValidStepEvidence(
        "extension",
        locator);

    Assert.False(valid);
  }

  [Fact]
  public async Task PlanArtifactStore_SealsCreatorSidAndReusesItDuringClaim()
  {
    const string creatorSid = "S-1-5-21-111111111-222222222-333333333-1001";
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    var validatedCreators = new List<string>();
    await using var stager = new ScriptedStager();
    var planArtifactRoot = Directory.GetParent(
        Path.GetDirectoryName(stager.VerifiedVsixPath)!)!.FullName;
    var stagingStore = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        (_, recordedCreator) => validatedCreators.Add(recordedCreator),
        () => creatorSid,
        identityNeutralPlanArtifactRoot: planArtifactRoot);
    var claimingStore = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        (_, recordedCreator) => validatedCreators.Add(recordedCreator),
        () => "S-1-5-18",
        identityNeutralPlanArtifactRoot: planArtifactRoot);
    var expectedHash = new string('A', 64);

    var staged = await stagingStore.StageAsync(
        "extension",
        stager.StagedPath,
        expectedHash,
        VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
        CancellationToken.None);
    var marker = await File.ReadAllTextAsync(Path.Combine(
        Path.GetDirectoryName(stager.VerifiedVsixPath)!,
        ".wdem-vsix-owner"));
    var claimed = await claimingStore.ClaimAsync(
        "extension",
        staged.StepEvidence!,
        expectedHash,
        VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
        CancellationToken.None);

    await using var artifact = Assert.IsType<ClaimedVsixPlanArtifact>(claimed.Artifact);
    Assert.Contains($"\"creatorSid\":\"{creatorSid}\"", marker, StringComparison.Ordinal);
    Assert.Contains("\"resourceId\":\"extension\"", marker, StringComparison.Ordinal);
    Assert.Equal([creatorSid, creatorSid], validatedCreators);
  }

  [Fact]
  public async Task PlanArtifactStore_ClaimCopiesValidatedArtifactAwayFromCreatorMapping()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var store = PlanArtifactStore(stager, verifier, manifests);
    var expectedHash = new string('A', 64);
    var staged = await store.StageAsync(
        "extension",
        stager.StagedPath,
        expectedHash,
        VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
        CancellationToken.None);
    var claimed = await store.ClaimAsync(
        "extension",
        staged.StepEvidence!,
        expectedHash,
        VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
        CancellationToken.None);
    var artifact = Assert.IsType<ClaimedVsixPlanArtifact>(claimed.Artifact);
    var installPath = artifact.Path;
    try
    {
      Assert.NotEqual(stager.VerifiedVsixPath, installPath);
      await File.WriteAllTextAsync(stager.VerifiedVsixPath, "changed");

      Assert.Equal("staged", await File.ReadAllTextAsync(installPath));
    }
    finally
    {
      await artifact.DisposeAsync();
    }

    Assert.False(File.Exists(installPath));
  }

  [Fact]
  public async Task PlanArtifactStore_FreshStoreRejectsReplayAfterConsumedArtifactCleanupFails()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var stagingStore = PlanArtifactStore(stager, verifier, manifests);
    var claimingStore = PlanArtifactStore(
        stager,
        verifier,
        manifests,
        deleteDirectory: static _ => { });
    var replayStore = PlanArtifactStore(stager, verifier, manifests);
    var expectedHash = new string('A', 64);
    var directory = Path.GetDirectoryName(stager.VerifiedVsixPath)!;
    string? terminalStatePath = null;
    ClaimedVsixPlanArtifact? firstArtifact = null;
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var staged = await stagingStore.StageAsync(
          "extension",
          stager.StagedPath,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      terminalStatePath = TerminalStatePath(directory, staged.StepEvidence!);
      var firstClaim = await claimingStore.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      firstArtifact = Assert.IsType<ClaimedVsixPlanArtifact>(firstClaim.Artifact);
      var consumedMarker = await File.ReadAllTextAsync(
          Path.Combine(directory, ".wdem-vsix-owner"));
      Assert.Contains("\"consumed\":true", consumedMarker, StringComparison.Ordinal);
      ReplaceMarkerEvidence(directory, "\"consumed\":true", "\"consumed\":false");
      await firstArtifact.DisposeAsync();
      firstArtifact = null;
      Assert.True(Directory.Exists(directory));
      var replay = await replayStore.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      replayedArtifact = replay.Artifact;

      Assert.Null(replay.Artifact);
      Assert.NotNull(replay.Error);
    }
    finally
    {
      if (firstArtifact is not null)
      {
        await firstArtifact.DisposeAsync();
      }

      if (replayedArtifact is not null)
      {
        await replayedArtifact.DisposeAsync();
      }

      ArtifactCleanupQueue.Shared.RetryPending();
      DeleteTerminalState(terminalStatePath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_FailedHashClaimCannotReplayAfterArtifactIsRestored()
  {
    var manifests = SourceManifestReader();
    var verifier = new TrustedFileVerifier();
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    var stagingStore = PlanArtifactStore(
        stager,
        verifier,
        manifests,
        revocationStore: revocationStore);
    var claimingStore = PlanArtifactStore(
        stager,
        verifier,
        manifests,
        deleteDirectory: static _ => { },
        revocationStore: revocationStore);
    var replayStore = PlanArtifactStore(
        stager,
        verifier,
        manifests,
        revocationStore: revocationStore);
    var original = await File.ReadAllBytesAsync(stager.StagedPath);
    var expectedHash = Convert.ToHexString(SHA256.HashData(original));
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var staged = await stagingStore.StageAsync(
          "extension",
          stager.StagedPath,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      await File.WriteAllTextAsync(stager.VerifiedVsixPath, "tampered");

      var failedClaim = await claimingStore.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      await File.WriteAllBytesAsync(stager.VerifiedVsixPath, original);
      var replay = await replayStore.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      replayedArtifact = replay.Artifact;

      Assert.Null(failedClaim.Artifact);
      Assert.NotNull(failedClaim.Error);
      Assert.Null(replay.Artifact);
      Assert.NotNull(replay.Error);
    }
    finally
    {
      if (replayedArtifact is not null)
      {
        await replayedArtifact.DisposeAsync();
      }

      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_ClaimStartAppendFailureFailsClosedAgainstRetry()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath)
    {
      ClaimStartedFailure = new IOException("claim-start append failed")
    };
    var expectedHash = new string('A', 64);
    ClaimedVsixPlanArtifact? retriedArtifact = null;
    try
    {
      var staged = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: revocationStore)
          .StageAsync(
              "extension",
              stager.StagedPath,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      var failed = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              deleteDirectory: static _ => { },
              revocationStore: revocationStore)
          .ClaimAsync(
              "extension",
              staged.StepEvidence!,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);

      Assert.Null(failed.Artifact);
      Assert.NotNull(failed.Error);
      Assert.True(Directory.Exists(Path.GetDirectoryName(stager.VerifiedVsixPath)));

      revocationStore.ClaimStartedFailure = null;
      var retried = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              deleteDirectory: static _ => { },
              revocationStore: revocationStore)
          .ClaimAsync(
              "extension",
              staged.StepEvidence!,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      retriedArtifact = retried.Artifact;

      Assert.Null(retriedArtifact);
      Assert.NotNull(retried.Error);
    }
    finally
    {
      if (retriedArtifact is not null)
      {
        await retriedArtifact.DisposeAsync();
      }

      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_MalformedActivationCommitmentFailsClosed()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    string? terminalStatePath = null;
    try
    {
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          revocationStore: revocationStore);
      var staged = await store.StageAsync(
          "extension",
          stager.StagedPath,
          new string('A', 64),
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      terminalStatePath = TerminalStatePath(
          Path.GetDirectoryName(stager.VerifiedVsixPath)!,
          staged.StepEvidence!);
      revocationStore.StateOverride = new VsixPlanArtifactLedgerState(
          DateTimeOffset.UtcNow.AddHours(1),
          "not-a-hex-commitment",
          WindowsVsixPlanArtifactClock.GetBootIdentifier(),
          Environment.TickCount64 + 3_600_000,
          VsixPlanArtifactLedgerStatus.Active);

      var result = await store.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          new string('A', 64),
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);

      Assert.Null(result.Artifact);
      Assert.NotNull(result.Error);
    }
    finally
    {
      DeleteTerminalState(terminalStatePath);
      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_AbandonPersistsTerminalFileWhenLedgerStateReadIsUnavailable()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    var deleted = false;
    try
    {
      var staged = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: revocationStore)
          .StageAsync(
              "extension",
              stager.StagedPath,
              new string('A', 64),
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      var directory = Path.GetDirectoryName(stager.VerifiedVsixPath)!;
      revocationStore.GetStateFailure = new UnauthorizedAccessException("Users lack ReadData");

      await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              deleteDirectory: _ => deleted = true,
              revocationStore: revocationStore)
          .AbandonAsync("extension", staged.StepEvidence!, CancellationToken.None);

      Assert.True(File.Exists(TerminalStatePath(directory, staged.StepEvidence!)));
      Assert.True(deleted);
    }
    finally
    {
      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_PreoccupiedTerminalCannotBeDeletedToReplayAbandonedLocator()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    var expectedHash = new string('A', 64);
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    string? terminalStatePath = null;
    try
    {
      var staged = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: revocationStore)
          .StageAsync(
              "extension",
              stager.StagedPath,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      var directory = Path.GetDirectoryName(stager.VerifiedVsixPath)!;
      terminalStatePath = TerminalStatePath(directory, staged.StepEvidence!);
      Directory.CreateDirectory(terminalStatePath);

      await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              deleteDirectory: static _ => { },
              revocationStore: revocationStore)
          .AbandonAsync("extension", staged.StepEvidence!, CancellationToken.None);

      Directory.Delete(terminalStatePath);
      ReplaceMarkerEvidence(directory, "\"revoked\":true", "\"revoked\":false");
      var replay = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: revocationStore)
          .ClaimAsync(
              "extension",
              staged.StepEvidence!,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      replayedArtifact = replay.Artifact;

      Assert.Null(replay.Artifact);
      Assert.NotNull(replay.Error);
      Assert.True(revocationStore.IsRevoked(
          staged.StepEvidence!.Split(':')[1],
          Path.GetFileName(directory)));
    }
    finally
    {
      if (replayedArtifact is not null)
      {
        await replayedArtifact.DisposeAsync();
      }

      DeleteTerminalState(terminalStatePath);
      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_PreoccupiedTerminalFailedClaimCannotReplayAfterDeletion()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var expectedHash = new string('A', 64);
    string? terminalStatePath = null;
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var staged = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: new TestPlanArtifactRevocationStore(revocationPath))
          .StageAsync(
              "extension",
              stager.StagedPath,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      var directory = Path.GetDirectoryName(stager.VerifiedVsixPath)!;
      terminalStatePath = TerminalStatePath(directory, staged.StepEvidence!);
      Directory.CreateDirectory(terminalStatePath);

      var first = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: new TestPlanArtifactRevocationStore(revocationPath))
          .ClaimAsync(
              "extension",
              staged.StepEvidence!,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      Directory.Delete(terminalStatePath);
      var replay = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: new TestPlanArtifactRevocationStore(revocationPath))
          .ClaimAsync(
              "extension",
              staged.StepEvidence!,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      replayedArtifact = replay.Artifact;

      Assert.Null(first.Artifact);
      Assert.NotNull(first.Error);
      Assert.Null(replay.Artifact);
      Assert.NotNull(replay.Error);
    }
    finally
    {
      if (replayedArtifact is not null)
      {
        await replayedArtifact.DisposeAsync();
      }

      DeleteTerminalState(terminalStatePath);
      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_ApprovedAbandonDoesNotReportSuccessWhenLedgerAppendIsDenied()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    try
    {
      var staged = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: revocationStore)
          .StageAsync(
              "extension",
              stager.StagedPath,
              new string('A', 64),
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      revocationStore.RevokeFailure = new UnauthorizedAccessException("append denied");

      await Assert.ThrowsAsync<System.Security.SecurityException>(() =>
          PlanArtifactStore(
                  stager,
                  verifier,
                  manifests,
                  revocationStore: revocationStore)
              .AbandonAsync("extension", staged.StepEvidence!, CancellationToken.None));
    }
    finally
    {
      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_SupersessionPersistsTerminalFileWhenLedgerStateReadIsUnavailable()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    var source = TempFile("vsix");
    try
    {
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore);
      var first = await store.StageAsync(
          "extension",
          source,
          new string('A', 64),
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      var firstDirectory = Assert.Single(stager.Directories);
      revocationStore.GetStateFailure = new UnauthorizedAccessException("Users lack ReadData");

      var second = await store.StageAsync(
          "extension",
          source,
          new string('A', 64),
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);

      Assert.NotNull(second.StepEvidence);
      Assert.True(File.Exists(TerminalStatePath(firstDirectory, first.StepEvidence!)));
    }
    finally
    {
      File.Delete(revocationPath);
      File.Delete(source);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_ConcurrentClaimsProduceExactlyOneDurableConsumer()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    var expectedHash = new string('A', 64);
    var claimedArtifacts = new List<ClaimedVsixPlanArtifact>();
    try
    {
      var staged = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: revocationStore)
          .StageAsync(
              "extension",
              stager.StagedPath,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      var firstStore = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore);
      var secondStore = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore);

      var claims = await Task.WhenAll(
          firstStore.ClaimAsync(
              "extension",
              staged.StepEvidence!,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None),
          secondStore.ClaimAsync(
              "extension",
              staged.StepEvidence!,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None));
      claimedArtifacts.AddRange(claims.Select(result => result.Artifact).OfType<ClaimedVsixPlanArtifact>());

      Assert.Single(claimedArtifacts);
      Assert.Single(claims, result => result.Error is not null);
      var state = revocationStore.GetState(
          staged.StepEvidence!["vsix-v2:".Length..("vsix-v2:".Length + 32)],
          Path.GetFileName(Path.GetDirectoryName(stager.VerifiedVsixPath)!));
      Assert.Equal(VsixPlanArtifactLedgerStatus.Consumed, state.Status);
    }
    finally
    {
      foreach (var artifact in claimedArtifacts)
      {
        await artifact.DisposeAsync();
      }

      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_InterleavedClaimNoncesPermitOnlyLedgerWinner()
  {
    const string winningNonce =
        "1111111111111111111111111111111111111111111111111111111111111111";
    const string losingNonce =
        "2222222222222222222222222222222222222222222222222222222222222222";
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationStore = new InterleavingClaimRevocationStore(winningNonce, losingNonce);
    var expectedHash = new string('A', 64);
    ClaimedVsixPlanArtifact? claimedArtifact = null;
    try
    {
      var staged = await PlanArtifactStore(stager, verifier, manifests)
          .StageAsync(
              "extension",
              stager.StagedPath,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      var winningStore = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore,
          createClaimNonce: static () => winningNonce,
          acquireClaimLease: static _ => NoopDisposable.Instance);
      var losingStore = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore,
          createClaimNonce: static () => losingNonce,
          acquireClaimLease: static _ => NoopDisposable.Instance);

      var losingBegin = Task.Run(() => CaptureExceptionAsync(() =>
          losingStore.BeginClaimAsync(
              "extension",
              staged.StepEvidence!,
              CancellationToken.None)));
      revocationStore.WaitUntilLosingClaimIsBlocked();
      var winningBegin = Task.Run(() => CaptureExceptionAsync(() =>
          winningStore.BeginClaimAsync(
              "extension",
              staged.StepEvidence!,
              CancellationToken.None)));
      var beginResults = await Task.WhenAll(winningBegin, losingBegin);

      Assert.Null(beginResults[0]);
      Assert.IsType<System.Security.SecurityException>(beginResults[1]);
      Assert.Equal(winningNonce, revocationStore.GetState(
          staged.StepEvidence!.Split(':')[1],
          Path.GetFileName(Path.GetDirectoryName(stager.VerifiedVsixPath)!)).ClaimNonce);

      var winningClaim = await winningStore.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      claimedArtifact = winningClaim.Artifact;
      var losingClaim = await losingStore.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);

      Assert.NotNull(claimedArtifact);
      Assert.Null(winningClaim.Error);
      Assert.Null(losingClaim.Artifact);
      Assert.NotNull(losingClaim.Error);
    }
    finally
    {
      if (claimedArtifact is not null)
      {
        await claimedArtifact.DisposeAsync();
      }
    }

    static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
    {
      try
      {
        await action();
        return null;
      }
      catch (Exception exception)
      {
        return exception;
      }
    }
  }
  [Fact]
  public async Task PlanArtifactStore_RevokeAfterValidationPreventsConsumeAcrossStores()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    using var consumeEntered = new ManualResetEventSlim();
    using var releaseConsume = new ManualResetEventSlim();
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath)
    {
      BeforeConsume = () =>
      {
        consumeEntered.Set();
        Assert.True(releaseConsume.Wait(TimeSpan.FromSeconds(5)));
      }
    };
    var expectedHash = new string('A', 64);
    ClaimedVsixPlanArtifact? claimedArtifact = null;
    try
    {
      var staged = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              deleteDirectory: static _ => { },
              revocationStore: revocationStore)
          .StageAsync(
              "extension",
              stager.StagedPath,
              expectedHash,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      var claimingStore = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore);
      var abandoningStore = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore);

      var claimTask = Task.Run(() => claimingStore.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None));
      Assert.True(consumeEntered.Wait(TimeSpan.FromSeconds(5)));
      await abandoningStore.AbandonAsync(
          "extension",
          staged.StepEvidence!,
          CancellationToken.None);
      releaseConsume.Set();
      var claim = await claimTask;
      claimedArtifact = claim.Artifact;

      Assert.True(revocationStore.IsRevoked(
          staged.StepEvidence!.Split(':')[1],
          Path.GetFileName(Path.GetDirectoryName(stager.VerifiedVsixPath)!)));
      Assert.Null(claim.Artifact);
      Assert.NotNull(claim.Error);
    }
    finally
    {
      releaseConsume.Set();
      if (claimedArtifact is not null)
      {
        await claimedArtifact.DisposeAsync();
      }

      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_LedgerLockWaitPastExpiryPreventsConsume()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    using var consumeEntered = new ManualResetEventSlim();
    using var releaseConsume = new ManualResetEventSlim();
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath)
    {
      BeforeConsume = () =>
      {
        consumeEntered.Set();
        Assert.True(releaseConsume.Wait(TimeSpan.FromSeconds(5)));
      }
    };
    var issuedAtUtc = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
    var utcNow = issuedAtUtc;
    var bootIdentifier = Guid.Parse("00112233-4455-6677-8899-AABBCCDDEEFF");
    var uptimeMilliseconds = 10_000L;
    var expectedHash = new string('A', 64);
    ClaimedVsixPlanArtifact? claimedArtifact = null;
    try
    {
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          handoffLifetime: TimeSpan.FromMinutes(1),
          getUtcNow: () => utcNow,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore,
          getBootIdentifier: () => bootIdentifier,
          getUptimeMilliseconds: () => uptimeMilliseconds);
      var staged = await store.StageAsync(
          "extension",
          stager.StagedPath,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);

      var claimTask = Task.Run(() => store.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None));
      Assert.True(consumeEntered.Wait(TimeSpan.FromSeconds(5)));
      utcNow = issuedAtUtc.AddMinutes(2);
      uptimeMilliseconds += 120_000;
      releaseConsume.Set();
      var claim = await claimTask;
      claimedArtifact = claim.Artifact;

      Assert.Null(claim.Artifact);
      Assert.NotNull(claim.Error);
    }
    finally
    {
      releaseConsume.Set();
      if (claimedArtifact is not null)
      {
        await claimedArtifact.DisposeAsync();
      }

      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task NoopRevocationStore_ConsumePausePastExpiryFailsClosed()
  {
    using var consumeReadyToCommit = new ManualResetEventSlim();
    using var releaseConsume = new ManualResetEventSlim();
    var store = new VsixPlanArtifactStore.NoopRevocationStore
    {
      ConsumeTransitionBarrier = () =>
      {
        consumeReadyToCommit.Set();
        Assert.True(releaseConsume.Wait(TimeSpan.FromSeconds(5)));
      }
    };
    var issuedAtUtc = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
    var utcNow = issuedAtUtc;
    var bootIdentifier = Guid.Parse("00112233-4455-6677-8899-AABBCCDDEEFF");
    var uptimeMilliseconds = 10_000L;
    var ownershipToken = Guid.NewGuid().ToString("N");
    var directoryName = Guid.NewGuid().ToString("N");
    var activationCommitment = new string('A', 64);
    var claimNonce = new string('B', 64);
    store.RecordIssued(
        ownershipToken,
        directoryName,
        issuedAtUtc.AddMinutes(1),
        activationCommitment,
        bootIdentifier,
        uptimeMilliseconds + 60_000);
    store.Activate(ownershipToken, directoryName);
    store.ClaimStarted(ownershipToken, directoryName, claimNonce);

    try
    {
      var consumeTask = Task.Run(() => Record.Exception(() => store.Consume(
          ownershipToken,
          directoryName,
          claimNonce,
          activationCommitment,
          () => utcNow,
          () => bootIdentifier,
          () => uptimeMilliseconds)));
      Assert.True(consumeReadyToCommit.Wait(TimeSpan.FromSeconds(5)));
      utcNow = issuedAtUtc.AddMinutes(2);
      uptimeMilliseconds += 120_000;
      releaseConsume.Set();
      var exception = await consumeTask;

      Assert.IsType<System.Security.SecurityException>(exception);
      Assert.Equal(
          VsixPlanArtifactLedgerStatus.ClaimStarted,
          store.GetState(ownershipToken, directoryName).Status);
    }
    finally
    {
      releaseConsume.Set();
    }
  }

  [Theory]
  [InlineData("consumed")]
  [InlineData("claimNonce")]
  [InlineData("activationCommitment")]
  [InlineData("expired")]
  public async Task PlanArtifactStore_AuthoritativeStateChangeBeforeConsumeFailsClosed(
      string changedField)
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    var expectedHash = new string('A', 64);
    string? ownershipToken = null;
    string? directoryName = null;
    try
    {
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore);
      var staged = await store.StageAsync(
          "extension",
          stager.StagedPath,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      ownershipToken = staged.StepEvidence!.Split(':')[1];
      directoryName = Path.GetFileName(Path.GetDirectoryName(stager.VerifiedVsixPath)!);
      revocationStore.BeforeConsume = () =>
      {
        var state = revocationStore.GetState(ownershipToken, directoryName);
        revocationStore.StateOverride = changedField switch
        {
          "consumed" => state with { Status = VsixPlanArtifactLedgerStatus.Consumed },
          "claimNonce" => state with { ClaimNonce = new string('B', 64) },
          "activationCommitment" => state with { ActivationCommitment = new string('B', 64) },
          "expired" => state with { ExpiresAtUtc = DateTimeOffset.MinValue },
          _ => throw new InvalidOperationException($"Unknown field: {changedField}")
        };
      };

      var claim = await store.ClaimAsync(
          "extension",
          staged.StepEvidence,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);

      Assert.Null(claim.Artifact);
      Assert.NotNull(claim.Error);
    }
    finally
    {
      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_LedgerPoisoningCannotHideRevocationFromFreshStore()
  {
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new ScriptedStager();
    var deleteBlockers = new List<FileStream>();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var cleanupQueue = new ArtifactCleanupQueue(
        maxAttempts: 1,
        retryDelay: TimeSpan.FromSeconds(30),
        maxDelayedRetryRounds: 1,
        knownStagingRoots: []);
    var stagingStore = PlanArtifactStore(
        stager,
        verifier,
        manifests,
        revocationStore: new TestPlanArtifactRevocationStore(revocationPath));
    var abandoningStore = PlanArtifactStore(
        stager,
        verifier,
        manifests,
        deleteDirectory: path =>
        {
          deleteBlockers.AddRange(Directory.EnumerateFiles(path).Select(file => new FileStream(
              file,
              FileMode.Open,
              FileAccess.Read,
              FileShare.ReadWrite)));
          cleanupQueue.DeleteDirectory(path);
        },
        revocationStore: new TestPlanArtifactRevocationStore(revocationPath));
    var replayStore = PlanArtifactStore(
        stager,
        verifier,
        manifests,
        revocationStore: new TestPlanArtifactRevocationStore(revocationPath));
    var expectedHash = new string('A', 64);
    var directory = Path.GetDirectoryName(stager.VerifiedVsixPath)!;
    string? terminalStatePath = null;
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      await File.WriteAllTextAsync(revocationPath, "attacker-controlled-garbage");
      var staged = await stagingStore.StageAsync(
          "extension",
          stager.StagedPath,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      terminalStatePath = TerminalStatePath(directory, staged.StepEvidence!);
      await abandoningStore.AbandonAsync(
          "extension",
          staged.StepEvidence!,
          CancellationToken.None);
      foreach (var deleteBlocker in deleteBlockers)
      {
        deleteBlocker.Dispose();
      }

      deleteBlockers.Clear();
      var revokedMarker = await File.ReadAllTextAsync(
          Path.Combine(directory, ".wdem-vsix-owner"));
      Assert.Contains("\"revoked\":true", revokedMarker, StringComparison.Ordinal);
      ReplaceMarkerEvidence(directory, "\"revoked\":true", "\"revoked\":false");

      var replay = await replayStore.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      replayedArtifact = replay.Artifact;

      Assert.Null(replay.Artifact);
      Assert.NotNull(replay.Error);
    }
    finally
    {
      if (replayedArtifact is not null)
      {
        await replayedArtifact.DisposeAsync();
      }
      foreach (var deleteBlocker in deleteBlockers)
      {
        deleteBlocker.Dispose();
      }

      cleanupQueue.RetryPending();
      DeleteTerminalState(terminalStatePath);
      File.Delete(revocationPath);
    }
  }

  [WindowsFact]
  public async Task PlanArtifactStore_FreshElevatedStoreResolvesIdentityNeutralRoot()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-cross-identity-{Guid.NewGuid():N}");
    var sharedRoot = Path.Combine(basePath, "shared", "Wdem", "PlanArtifacts");
    var source = Path.Combine(basePath, "source.vsix");
    Directory.CreateDirectory(basePath);
    await File.WriteAllTextAsync(source, "vsix");
    var expectedHash = new string('A', 64);
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    var stager = new SecureArtifactStager(
        new TestPlanArtifactDirectoryPolicy(sharedRoot),
        verifier);
    var stagingStore = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory,
        WindowsPlanArtifactDirectoryPolicy.GetCurrentUserSid,
        identityNeutralPlanArtifactRoot: sharedRoot);
    var claimingStore = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory,
        WindowsPlanArtifactDirectoryPolicy.GetCurrentUserSid,
        identityNeutralPlanArtifactRoot: sharedRoot);

    try
    {
      var staged = await stagingStore.StageAsync(
          "extension",
          source,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      Assert.NotNull(staged.StepEvidence);
      Assert.InRange(staged.StepEvidence.Length, 1, 128);

      var claimed = await claimingStore.ClaimAsync(
          "extension",
          staged.StepEvidence,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);

      await using var artifact = Assert.IsType<ClaimedVsixPlanArtifact>(claimed.Artifact);
      Assert.StartsWith(sharedRoot, artifact.Path, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }

  [WindowsFact]
  public async Task PlanArtifactStore_FreshStoreAbandonsArtifactInIdentityNeutralRoot()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-cross-abandon-{Guid.NewGuid():N}");
    var sharedRoot = Path.Combine(basePath, "shared", "Wdem", "PlanArtifacts");
    var source = Path.Combine(basePath, "source.vsix");
    Directory.CreateDirectory(basePath);
    await File.WriteAllTextAsync(source, "vsix");
    var expectedHash = new string('A', 64);
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    var stager = new SecureArtifactStager(
        new TestPlanArtifactDirectoryPolicy(sharedRoot),
        verifier);
    var stagingStore = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory,
        WindowsPlanArtifactDirectoryPolicy.GetCurrentUserSid,
        identityNeutralPlanArtifactRoot: sharedRoot);
    var abandoningStore = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory,
        WindowsPlanArtifactDirectoryPolicy.GetCurrentUserSid,
        identityNeutralPlanArtifactRoot: sharedRoot);

    try
    {
      var staged = await stagingStore.StageAsync(
          "extension",
          source,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      var locator = Assert.IsType<string>(staged.StepEvidence);
      var directoryName = locator.Split(':')[2];
      var artifactDirectory = Path.Combine(sharedRoot, directoryName);

      await abandoningStore.AbandonAsync(
          "extension",
          locator,
          CancellationToken.None);

      Assert.False(Directory.Exists(artifactDirectory));
    }
    finally
    {
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }

  [WindowsFact]
  public async Task PlanArtifactStore_ClaimDoesNotFallBackToClaimantProfileForgery()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-cross-identity-forgery-{Guid.NewGuid():N}");
    var claimantRoot = Path.Combine(basePath, "claimant", "Wdem", "PlanArtifacts");
    var sharedRoot = Path.Combine(basePath, "shared", "Wdem", "PlanArtifacts");
    var source = Path.Combine(basePath, "source.vsix");
    Directory.CreateDirectory(basePath);
    await File.WriteAllTextAsync(source, "vsix");
    var expectedHash = new string('A', 64);
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    var stager = new SecureArtifactStager(
        new TestPlanArtifactDirectoryPolicy(sharedRoot),
        verifier);
    var stagingStore = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory,
        WindowsPlanArtifactDirectoryPolicy.GetCurrentUserSid,
        identityNeutralPlanArtifactRoot: sharedRoot);
    var claimingStore = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory,
        WindowsPlanArtifactDirectoryPolicy.GetCurrentUserSid,
        identityNeutralPlanArtifactRoot: sharedRoot);

    try
    {
      var staged = await stagingStore.StageAsync(
          "extension",
          source,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      var locator = Assert.IsType<string>(staged.StepEvidence);
      var directoryName = locator.Split(':')[2];
      var actualDirectory = Path.Combine(sharedRoot, directoryName);
      var forgedDirectory = Path.Combine(claimantRoot, directoryName);
      Directory.CreateDirectory(forgedDirectory);
      foreach (var path in Directory.EnumerateFiles(actualDirectory))
      {
        File.Copy(path, Path.Combine(forgedDirectory, Path.GetFileName(path)));
      }

      using var identity = WindowsIdentity.GetCurrent();
      new DirectoryInfo(forgedDirectory).SetAccessControl(
          WindowsPlanArtifactDirectoryPolicy.CreateSecurity(
              identity.User!,
              new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
              new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
      var forgedMarkerPath = Path.Combine(forgedDirectory, ".wdem-vsix-owner");
      var forgedMarker = JsonNode.Parse(await File.ReadAllTextAsync(forgedMarkerPath))!;
      forgedMarker["ownershipDirectory"] = forgedDirectory;
      forgedMarker["artifactPath"] = Path.Combine(forgedDirectory, "extension.vsix");
      await File.WriteAllTextAsync(forgedMarkerPath, forgedMarker.ToJsonString());
      File.Delete(Path.Combine(actualDirectory, ".wdem-vsix-owner"));

      var claimed = await claimingStore.ClaimAsync(
          "extension",
          locator,
          expectedHash,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);

      try
      {
        Assert.Null(claimed.Artifact);
        Assert.NotNull(claimed.Error);
        Assert.True(Directory.Exists(forgedDirectory));
      }
      finally
      {
        if (claimed.Artifact is not null)
        {
          await claimed.Artifact.DisposeAsync();
        }
      }
    }
    finally
    {
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }
  [Fact]
  public async Task VsixManifestReader_ReadsStableIdentityFromArchiveAndRejectsMissingIdentity()
  {
    var validPath = TempVsix(
        "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\">" +
        "<Metadata><Identity Id=\"Contoso.DeveloperTools\" Version=\"3.2.0\" /></Metadata>" +
        "<Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" Version=\"[17.0,18.0)\" />" +
        "</Installation>" +
        "</PackageManifest>");
    var invalidPath = TempVsix("<PackageManifest><Metadata /></PackageManifest>");
    try
    {
      var reader = new VsixManifestReader();

      var valid = await reader.ReadSourceAsync(validPath, "17.0_a", CancellationToken.None);
      var invalid = await reader.ReadSourceAsync(invalidPath, "17.0_a", CancellationToken.None);

      Assert.Equal("Contoso.DeveloperTools", valid.Manifest!.Id);
      Assert.Equal("3.2.0", valid.Manifest.Version);
      Assert.Equal("17.0_a", valid.Manifest.VisualStudioInstanceId);
      Assert.Equal("Microsoft.VisualStudio.Community", Assert.Single(valid.Manifest.Targets).Id);
      Assert.Null(valid.Error);
      Assert.Null(invalid.Manifest);
      Assert.Equal(WdemErrorCode.ConfigurationError, invalid.Error!.Code);
    }
    finally
    {
      File.Delete(validPath);
      File.Delete(invalidPath);
    }
  }

  [Fact]
  public async Task Factory_RegistersVsixAndReSharperProviders()
  {
    var root = Path.Combine(Path.GetTempPath(), $"wdem-vsix-factory-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(root, "profiles"));
    try
    {
      var composition = await WdemWindowsFactory.CreateAsync(
          Path.Combine(root, "profiles"),
          new WdemDataPaths(Path.Combine(root, "data")),
          CancellationToken.None);

      Assert.IsType<VisualStudioExtensionProvider>(
          composition.Providers.GetRequired("visual-studio-extension", "vsix"));
      Assert.IsType<ReSharperProvider>(
          composition.Providers.GetRequired("resharper", "winget"));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private static VisualStudioExtensionProvider Provider(
      FakeVsixManifestReader manifests,
      IProcessExecutor process,
      IVisualStudioDiscovery? discovery = null)
  {
    return new VisualStudioExtensionProvider(
        discovery ?? new FakeVisualStudioDiscovery(Instance("17.0_a"), Instance("17.0_b")),
        manifests,
        process,
        new ComplianceEvaluator());
  }

  private static VsixPlanArtifactStore PlanArtifactStore(
      ISecureArtifactStager stager,
      ITrustedFileVerifier verifier,
      IVsixManifestReader manifests,
      Action<string, string>? validateRestrictedDirectory = null,
      TimeSpan? handoffLifetime = null,
      Func<DateTimeOffset>? getUtcNow = null,
      Action<string>? deleteDirectory = null,
      IVsixPlanArtifactRevocationStore? revocationStore = null,
      Func<Guid>? getBootIdentifier = null,
      Func<long>? getUptimeMilliseconds = null,
      Func<TimeSpan, CancellationToken, Task>? delay = null,
      Func<string>? createClaimNonce = null,
      Func<string, IDisposable>? acquireClaimLease = null) => new(
          stager,
          verifier,
          manifests,
          validateRestrictedDirectory ?? ((_, _) => { }),
          static () => "S-1-0-0",
          handoffLifetime,
          identityNeutralPlanArtifactRoot: Path.Combine(
              Path.GetTempPath(),
              "Wdem",
              "PlanArtifacts"),
          getUtcNow,
          deleteDirectory: deleteDirectory,
          revocationStore: revocationStore,
          getBootIdentifier: getBootIdentifier,
          getUptimeMilliseconds: getUptimeMilliseconds,
          delay: delay,
          createClaimNonce: createClaimNonce,
          acquireClaimLease: acquireClaimLease);
  private static VisualStudioExtensionProvider RealReaderProvider(string localApplicationData)
  {
    var reader = new VsixManifestReader(localApplicationData);
    return new VisualStudioExtensionProvider(
        new FakeVisualStudioDiscovery(Instance("a"), Instance("b")),
        reader,
        new ThrowingProcessExecutor(),
        new ComplianceEvaluator());
  }

  private static string ProfileManifestPath(
      string root,
      string profile,
      string directory,
      string fileName) => Path.Combine(
          root,
          "Microsoft",
          "VisualStudio",
          profile,
          "Extensions",
          directory,
          fileName);

  private static string TerminalStatePath(string artifactDirectory, string locator)
  {
    var parts = locator.Split(':');
    Assert.Equal(4, parts.Length);
    return Path.Combine(
        Path.GetDirectoryName(artifactDirectory)!,
        $".{parts[2]}.{parts[1]}.wdem-vsix-terminal");
  }

  private static void DeleteTerminalState(string? path)
  {
    if (path is null)
    {
      return;
    }

    if (File.Exists(path))
    {
      File.Delete(path);
    }
    if (Directory.Exists(path))
    {
      Directory.Delete(path);
    }
  }

  private static void ReplaceMarkerEvidence(
      string directory,
      string expected,
      string replacement)
  {
    var markerPath = Path.Combine(directory, ".wdem-vsix-owner");
    var json = File.ReadAllText(markerPath);
    Assert.Contains(expected, json, StringComparison.Ordinal);
    json = json.Replace(expected, replacement, StringComparison.Ordinal);
    File.WriteAllText(markerPath, json);
  }

  private static string InstalledManifest(string version) =>
      "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\">" +
      $"<Metadata><Identity Id=\"Contoso.DeveloperTools\" Version=\"{version}\" /></Metadata>" +
      "<Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" " +
      "Version=\"[17.0,18.0)\" /></Installation></PackageManifest>";

  private static FakeVsixManifestReader SourceManifestReader() => new()
  {
    SourceManifest = new VsixManifest(
        "Contoso.DeveloperTools",
        "3.2.0",
        "source!/extension.vsixmanifest",
        "17.0_a")
  };

  private static ResourceDefinition ExtensionResource(
      string extensionId,
      string version,
      string instanceId,
      string source = @"C:\Artifacts\contoso.vsix",
      string? expectedSha256 = null) => new()
      {
        Id = "contoso-extension",
        Type = "visual-studio-extension",
        Provider = "vsix",
        VersionConstraint = version,
        Dependencies = ["visual-studio"],
        PrivilegeRequirement = PrivilegeRequirement.Administrator,
        Parameters = new Dictionary<string, string?>
        {
          ["extensionId"] = extensionId,
          ["sourcePath"] = source,
          ["expectedSha256"] = expectedSha256 ?? new string('A', 64),
          ["visualStudioResourceId"] = "visual-studio",
          ["instanceId"] = instanceId
        }
      };

  private static ResourceDefinition WithOptionalInstanceSelector(ResourceDefinition resource)
  {
    var parameters = resource.Parameters
        .Where(pair => !string.Equals(pair.Key, "instanceId", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    parameters["productId"] = "Microsoft.VisualStudio.Product.Community";
    parameters["edition"] = "Community";
    parameters["channelId"] = "VisualStudio.17.Release";
    return resource with { Parameters = parameters };
  }

  private static VisualStudioInstance Instance(string instanceId) => new()
  {
    InstanceId = instanceId,
    InstallationPath = $@"C:\VS\{instanceId}",
    ProductId = "Microsoft.VisualStudio.Product.Community",
    ProductPath = $@"C:\VS\{instanceId}\Common7\IDE\devenv.exe",
    ProductDisplayVersion = "17.0",
    InstallationVersion = "17.0.0",
    ChannelId = "VisualStudio.17.Release",
    Edition = "Community",
    IsComplete = true,
    IsLaunchable = true
  };

  private static DetectedState Missing(ResourceDefinition resource) => new()
  {
    ResourceId = resource.Id,
    Outcome = DetectionOutcome.Succeeded,
    Exists = false
  };

  private static string TempFile(string content)
  {
    var path = Path.Combine(Path.GetTempPath(), $"wdem-vsix-{Guid.NewGuid():N}.vsix");
    File.WriteAllText(path, content);
    return path;
  }

  private static string TempVsix(string manifest)
  {
    var path = Path.Combine(Path.GetTempPath(), $"wdem-manifest-{Guid.NewGuid():N}.vsix");
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    var entry = archive.CreateEntry("extension.vsixmanifest");
    using var writer = new StreamWriter(entry.Open());
    writer.Write(manifest);
    return path;
  }

  private sealed class FakeVisualStudioDiscovery(params VisualStudioInstance[] instances)
      : IVisualStudioDiscovery
  {
    public Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(
        IReadOnlyList<string> requestedWorkloads,
        IReadOnlyList<string> requestedComponents,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VisualStudioInstance>>(
            instances);
  }

  private sealed class MutableVisualStudioDiscovery(params VisualStudioInstance[] instances)
      : IVisualStudioDiscovery
  {
    public IReadOnlyList<VisualStudioInstance> Instances { get; set; } = instances;

    public Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(
        IReadOnlyList<string> requestedWorkloads,
        IReadOnlyList<string> requestedComponents,
        CancellationToken cancellationToken) => Task.FromResult(Instances);
  }

  private sealed class FakeVsixManifestReader : IVsixManifestReader
  {
    private readonly List<VsixManifest> _manifests = [];
    public VsixManifest? SourceManifest { get; init; }

    public void Add(
        string path,
        string id,
        string version,
        string instanceId,
        IReadOnlyList<VsixInstallationTarget>? targets = null) =>
        _manifests.Add(new VsixManifest(id, version, path, instanceId, targets));

    public Task<IReadOnlyList<VsixManifest>> ReadInstalledAsync(
        VisualStudioInstance instance,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VsixManifest>>(
            _manifests.Where(manifest => string.Equals(
                manifest.VisualStudioInstanceId,
                instance.InstanceId,
                StringComparison.OrdinalIgnoreCase)).ToArray());

    public Task<VsixManifestReadResult> ReadSourceAsync(
        string path,
        string visualStudioInstanceId,
        CancellationToken cancellationToken) => Task.FromResult(new VsixManifestReadResult(
            SourceManifest,
            SourceManifest is null
                ? new StructuredError(WdemErrorCode.ConfigurationError, "Invalid.", "Invalid.")
                : null));
  }

  private sealed class TestPlanArtifactDirectoryPolicy(string rootPath)
      : ISecureArtifactDirectoryPolicy
  {
    public string CreateRestrictedStagingDirectory()
    {
      var path = Path.Combine(rootPath, Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(path);
      using var identity = WindowsIdentity.GetCurrent();
      new DirectoryInfo(path).SetAccessControl(
          WindowsPlanArtifactDirectoryPolicy.CreateSecurity(
              identity.User!,
              new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
              new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
      return path;
    }
  }

  private sealed class TestPlanArtifactRevocationStore(string path)
      : IVsixPlanArtifactRevocationStore
  {
    private readonly object _transitionSync = new();
    public Action? BeforeRevoke { get; init; }
    public Action? BeforeConsume { get; set; }
    public Exception? RevokeFailure { get; set; }
    public Exception? ClaimStartedFailure { get; set; }
    public Exception? GetStateFailure { get; set; }
    public VsixPlanArtifactLedgerState? StateOverride { get; set; }

    public void RecordIssued(
        string ownershipToken,
        string directoryName,
        DateTimeOffset expiresAtUtc,
        string activationCommitment,
        Guid bootIdentifier,
        long expiresAtUptimeMilliseconds) =>
        Append(handle => WindowsPlanArtifactDirectoryPolicy.WriteIssuanceRecord(
            handle,
            ownershipToken,
            directoryName,
            expiresAtUtc,
            activationCommitment,
            bootIdentifier,
            expiresAtUptimeMilliseconds));

    public DateTimeOffset GetIssuedExpiry(string ownershipToken, string directoryName)
    {
      using var stream = File.OpenRead(path);
      return VsixPlanArtifactLedger.GetIssuedExpiry(stream, ownershipToken, directoryName);
    }

    public void Activate(string ownershipToken, string directoryName) =>
        Append(handle => WindowsPlanArtifactDirectoryPolicy.WriteActivationRecord(
            handle,
            ownershipToken,
            directoryName));

    public void ClaimStarted(string ownershipToken, string directoryName, string claimNonce)
    {
      if (ClaimStartedFailure is not null)
      {
        throw ClaimStartedFailure;
      }

      Append(handle => WindowsPlanArtifactDirectoryPolicy.WriteClaimStartedRecord(
          handle,
          ownershipToken,
          directoryName,
          claimNonce));
    }

    public void Consume(
        string ownershipToken,
        string directoryName,
        string claimNonce,
        string activationCommitment,
        Func<DateTimeOffset> getUtcNow,
        Func<Guid> getBootIdentifier,
        Func<long> getUptimeMilliseconds)
    {
      BeforeConsume?.Invoke();
      lock (_transitionSync)
      {
        if (!VsixPlanArtifactLedger.IsAuthorizedClaimForConsumption(
                GetState(ownershipToken, directoryName),
                claimNonce,
                activationCommitment,
                getUtcNow(),
                getBootIdentifier(),
                getUptimeMilliseconds()))
        {
          throw new System.Security.SecurityException(
              "The durable VSIX claim is no longer authorized for consumption.");
        }

        Append(handle => WindowsPlanArtifactDirectoryPolicy.WriteConsumedRecord(
            handle,
            ownershipToken,
            directoryName));
      }
    }

    public VsixPlanArtifactLedgerState GetState(string ownershipToken, string directoryName)
    {
      if (GetStateFailure is not null)
      {
        throw GetStateFailure;
      }

      if (StateOverride is not null)
      {
        return StateOverride.Value;
      }

      using var stream = File.OpenRead(path);
      return VsixPlanArtifactLedger.ReadState(stream, ownershipToken, directoryName);
    }

    public void Revoke(string ownershipToken, string directoryName)
    {
      BeforeRevoke?.Invoke();
      if (RevokeFailure is not null)
      {
        throw RevokeFailure;
      }

      lock (_transitionSync)
      {
        if (File.Exists(path))
        {
          using var stream = File.OpenRead(path);
          if (VsixPlanArtifactLedger.ReadFirstTerminalStatus(
                  stream,
                  ownershipToken,
                  directoryName) is not null)
          {
            return;
          }
        }

        Append(handle => WindowsPlanArtifactDirectoryPolicy.WriteRevocationRecord(
            handle,
            ownershipToken,
            directoryName));
      }
    }

    public bool IsRevoked(string ownershipToken, string directoryName)
    {
      if (!File.Exists(path))
      {
        return false;
      }

      using var stream = File.OpenRead(path);
      return VsixPlanArtifactLedger.ReadFirstTerminalStatus(
          stream,
          ownershipToken,
          directoryName) == VsixPlanArtifactLedgerStatus.Revoked;
    }
    private void Append(Action<Microsoft.Win32.SafeHandles.SafeFileHandle> write)
    {
      using var stream = new FileStream(
          path,
          FileMode.OpenOrCreate,
          FileAccess.Write,
          FileShare.ReadWrite,
          bufferSize: 1,
          FileOptions.WriteThrough);
      _ = stream.Seek(0, SeekOrigin.End);
      write(stream.SafeFileHandle);
    }
  }

  private sealed class InterleavingClaimRevocationStore(
      string winningNonce,
      string losingNonce) : IVsixPlanArtifactRevocationStore
  {
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _loserBlocked = new();
    private readonly ManualResetEventSlim _winnerWritten = new();
    private readonly Barrier _claimsWritten = new(2);
    private VsixPlanArtifactLedgerState? _state;

    public void RecordIssued(
        string ownershipToken,
        string directoryName,
        DateTimeOffset expiresAtUtc,
        string activationCommitment,
        Guid bootIdentifier,
        long expiresAtUptimeMilliseconds)
    {
      lock (_sync)
      {
        _state ??= new VsixPlanArtifactLedgerState(
            expiresAtUtc,
            activationCommitment,
            bootIdentifier,
            expiresAtUptimeMilliseconds,
            VsixPlanArtifactLedgerStatus.Pending);
      }
    }

    public DateTimeOffset GetIssuedExpiry(string ownershipToken, string directoryName) =>
        GetState(ownershipToken, directoryName).ExpiresAtUtc;

    public void Activate(string ownershipToken, string directoryName)
    {
      lock (_sync)
      {
        if (_state is { Status: VsixPlanArtifactLedgerStatus.Pending } state)
        {
          _state = state with { Status = VsixPlanArtifactLedgerStatus.Active };
        }
      }
    }

    public void ClaimStarted(string ownershipToken, string directoryName, string claimNonce)
    {
      if (string.Equals(claimNonce, losingNonce, StringComparison.Ordinal))
      {
        _loserBlocked.Set();
        Assert.True(_winnerWritten.Wait(TimeSpan.FromSeconds(5)));
      }

      lock (_sync)
      {
        if (_state is { Status: VsixPlanArtifactLedgerStatus.Active } state)
        {
          _state = state with
          {
            Status = VsixPlanArtifactLedgerStatus.ClaimStarted,
            ClaimNonce = claimNonce
          };
        }
      }

      if (string.Equals(claimNonce, winningNonce, StringComparison.Ordinal))
      {
        _winnerWritten.Set();
      }

      Assert.True(_claimsWritten.SignalAndWait(TimeSpan.FromSeconds(5)));
    }

    public void Consume(
        string ownershipToken,
        string directoryName,
        string claimNonce,
        string activationCommitment,
        Func<DateTimeOffset> getUtcNow,
        Func<Guid> getBootIdentifier,
        Func<long> getUptimeMilliseconds)
    {
      lock (_sync)
      {
        var state = _state ?? throw new System.Security.SecurityException(
            "The VSIX issuance record is missing.");
        _state = VsixPlanArtifactLedger.IsAuthorizedClaimForConsumption(
            state,
            claimNonce,
            activationCommitment,
            getUtcNow(),
            getBootIdentifier(),
            getUptimeMilliseconds())
                ? state with { Status = VsixPlanArtifactLedgerStatus.Consumed }
                : throw new System.Security.SecurityException(
                    "The durable VSIX claim is no longer authorized for consumption.");
      }
    }

    public VsixPlanArtifactLedgerState GetState(string ownershipToken, string directoryName)
    {
      lock (_sync)
      {
        return _state ?? throw new System.Security.SecurityException(
            "The VSIX issuance record is missing.");
      }
    }
    public void Revoke(string ownershipToken, string directoryName)
    {
      lock (_sync)
      {
        if (_state is { Status: not VsixPlanArtifactLedgerStatus.Consumed } state)
        {
          _state = state with { Status = VsixPlanArtifactLedgerStatus.Revoked };
        }
      }
    }

    public bool IsRevoked(string ownershipToken, string directoryName) =>
        GetState(ownershipToken, directoryName).Status == VsixPlanArtifactLedgerStatus.Revoked;

    public void WaitUntilLosingClaimIsBlocked() =>
        Assert.True(_loserBlocked.Wait(TimeSpan.FromSeconds(5)));
  }

  private sealed class NoopDisposable : IDisposable
  {
    public static NoopDisposable Instance { get; } = new();

    public void Dispose()
    {
    }
  }

  private sealed class ThrowingIssuanceRevocationStore : IVsixPlanArtifactRevocationStore
  {
    private bool _revoked;

    public void RecordIssued(
        string ownershipToken,
        string directoryName,
        DateTimeOffset expiresAtUtc,
        string activationCommitment,
        Guid bootIdentifier,
        long expiresAtUptimeMilliseconds) => throw new IOException("issuance append failed");

    public DateTimeOffset GetIssuedExpiry(string ownershipToken, string directoryName) =>
        throw new System.Security.SecurityException("No issuance was recorded.");

    public void Activate(string ownershipToken, string directoryName)
    {
    }

    public void ClaimStarted(string ownershipToken, string directoryName, string claimNonce)
    {
    }

    public void Consume(
        string ownershipToken,
        string directoryName,
        string claimNonce,
        string activationCommitment,
        Func<DateTimeOffset> getUtcNow,
        Func<Guid> getBootIdentifier,
        Func<long> getUptimeMilliseconds)
    {
    }

    public VsixPlanArtifactLedgerState GetState(string ownershipToken, string directoryName) =>
        throw new System.Security.SecurityException("No issuance was recorded.");

    public void Revoke(string ownershipToken, string directoryName)
    {
      _revoked = true;
    }

    public bool IsRevoked(string ownershipToken, string directoryName) => _revoked;
  }

  private sealed class ScriptedStager : ISecureArtifactStager, IAsyncDisposable
  {
    private readonly SecureArtifactStageResult? _result;
    private readonly string _directory;
    private SecureStagedArtifact? _artifact;

    public ScriptedStager(SecureArtifactStageResult result)
    {
      _result = result;
      _directory = string.Empty;
      StagedPath = string.Empty;
    }

    public ScriptedStager()
    {
      _directory = Path.Combine(
          Path.GetTempPath(),
          "Wdem",
          "PlanArtifacts",
          Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(_directory);
      StagedPath = Path.Combine(_directory, "installer.exe");
      File.WriteAllText(StagedPath, "staged");
    }

    public string StagedPath { get; }
    public string VerifiedVsixPath => Path.Combine(_directory, "extension.vsix");
    public int PathStageCalls { get; private set; }
    public int StreamStageCalls { get; private set; }

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        string sourcePath,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken)
    {
      PathStageCalls++;
      return StageCore(expectedSha256);
    }

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        Stream source,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken)
    {
      StreamStageCalls++;
      return StageCore(expectedSha256);
    }

    private Task<SecureArtifactStageResult> StageCore(string expectedSha256)
    {
      if (_result is not null)
      {
        return Task.FromResult(_result);
      }

      var readLock = new FileStream(StagedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
      _artifact = new SecureStagedArtifact(
          _directory,
          StagedPath,
          expectedSha256,
          readLock,
          ArtifactLease.Create(_directory));
      return Task.FromResult(new SecureArtifactStageResult(_artifact, null));
    }

    public async ValueTask DisposeAsync()
    {
      if (_artifact is not null)
      {
        await _artifact.DisposeAsync();
      }

      if (Directory.Exists(_directory))
      {
        Directory.Delete(_directory, recursive: true);
      }
    }
  }

  private sealed class RotatingStager : ISecureArtifactStager, IAsyncDisposable
  {
    private readonly List<SecureStagedArtifact> _artifacts = [];
    public List<string> Directories { get; } = [];

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        string sourcePath,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken) => StageCore(expectedSha256);

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        Stream source,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken) => StageCore(expectedSha256);

    private Task<SecureArtifactStageResult> StageCore(string expectedSha256)
    {
      var directory = Path.Combine(
          Path.GetTempPath(),
          "Wdem",
          "PlanArtifacts",
          Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      var path = Path.Combine(directory, "staged.vsix");
      File.WriteAllText(path, "staged");
      var artifact = new SecureStagedArtifact(
          directory,
          path,
          expectedSha256,
          new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
          ArtifactLease.Create(directory));
      Directories.Add(directory);
      _artifacts.Add(artifact);
      return Task.FromResult(new SecureArtifactStageResult(artifact, null));
    }

    public async ValueTask DisposeAsync()
    {
      foreach (var artifact in _artifacts)
      {
        await artifact.DisposeAsync();
      }

      foreach (var directory in Directories.Where(Directory.Exists))
      {
        Directory.Delete(directory, recursive: true);
      }
    }
  }

  private sealed class FakeTrustedFileVerifier(bool isTrusted) : ITrustedFileVerifier
  {
    public Task<TrustedFileVerificationResult> VerifySha256Async(
        string path,
        string expectedHash,
        CancellationToken cancellationToken) => Task.FromResult(isTrusted
            ? new TrustedFileVerificationResult(
                true,
                Path.GetFullPath(path),
                expectedHash,
                null)
            : new TrustedFileVerificationResult(
                false,
                null,
                null,
                new StructuredError(
                    WdemErrorCode.ConfigurationError,
                    "Hash mismatch.",
                    "The VSIX hash did not match.")));
  }

  private sealed class ThrowingProcessExecutor : IProcessExecutor
  {
    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken) => throw new InvalidOperationException();
  }

  private sealed class SatisfiedDependencyProvider : IResourceProvider
  {
    public string ResourceType => "planner-dependency";
    public string ProviderName => "planner-dependency";
    public ProviderCapabilities Capabilities { get; } = new();

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Succeeded,
          Exists = true
        });

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) => ValueTask.FromResult(new ResourcePlan
        {
          ResourceId = resource.Id,
          ResourceType = resource.Type,
          ProviderName = resource.Provider,
          DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
          Compliance = ComplianceStatus.Satisfied,
          IsExecutable = true
        });

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  private sealed class CountingHttpHandler : HttpMessageHandler
  {
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
      RequestCount++;
      return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
      {
        Content = new ByteArrayContent("downloaded"u8.ToArray()),
        RequestMessage = request
      });
    }
  }
}
