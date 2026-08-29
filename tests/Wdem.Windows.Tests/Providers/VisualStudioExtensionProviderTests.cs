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

public sealed class VisualStudioExtensionProviderTests
{
  [Fact]
  public async Task ExecutionPlanner_AcceptsOpaqueVsixPlanLocator()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(manifests, new ThrowingProcessExecutor(), stager);
      var dependency = resource with
      {
        Id = "visual-studio",
        Type = "planner-dependency",
        Provider = "planner-dependency",
        VersionConstraint = null,
        PreferredVersion = null,
        Dependencies = [],
        Parameters = new Dictionary<string, string?>()
      };
      var graph = new ResourceGraph(
          new Dictionary<string, ResolvedResource>(StringComparer.OrdinalIgnoreCase)
          {
            [dependency.Id] = new(
                dependency,
                ResourceOrigin.AutoDependency,
                new HashSet<string>([resource.Id], StringComparer.OrdinalIgnoreCase)),
            [resource.Id] = new(resource, ResourceOrigin.Required, new HashSet<string>())
          },
          [new ResourceGraphLayer(0, [dependency.Id]), new ResourceGraphLayer(1, [resource.Id])]);
      var planner = new ExecutionPlanner(
          new ResourceProviderRegistry([new SatisfiedDependencyProvider(), provider]),
          new ComplianceEvaluator());

      var plan = await planner.CreateAsync(
          graph,
          new Dictionary<string, DetectedState>(StringComparer.OrdinalIgnoreCase)
          {
            [dependency.Id] = new DetectedState
            {
              ResourceId = dependency.Id,
              Outcome = DetectionOutcome.Succeeded,
              Exists = true
            },
            [resource.Id] = Missing(resource)
          },
          "developer",
          "1.0.0",
          CancellationToken.None);

      Assert.True(
          plan.IsExecutable,
          string.Join(Environment.NewLine, plan.Errors.Select(error => error.Detail)));
      var plannedResource = Assert.Single(plan.Resources, item => item.Definition.Id == resource.Id);
      var step = Assert.Single(plannedResource.ResourcePlan.Steps);
      Assert.True(step.Id.Length <= 128);
    }
    finally
    {
      File.Delete(source);
    }
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
  public async Task ApplyAsync_InvalidHashStopsBeforeVsixInstaller()
  {
    var source = TempFile("not-a-vsix");
    try
    {
      var resource = ExtensionResource(
          "Contoso.DeveloperTools",
          "3.2.x",
          "17.0_a",
          source);
      var manifests = new FakeVsixManifestReader
      {
        SourceManifest = new VsixManifest(
            "Contoso.DeveloperTools",
            "3.2.0",
            "source!/extension.vsixmanifest",
            "17.0_a")
      };
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          new ScriptedStager(new SecureArtifactStageResult(
              null,
              new StructuredError(
                  WdemErrorCode.ConfigurationError,
                  "Hash mismatch.",
                  "The artifact hash did not match."))));
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

      Assert.False(plan.IsExecutable);
      Assert.Equal(WdemErrorCode.ConfigurationError, Assert.Single(plan.StructuredErrors).Code);
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task PlanAsync_HashMismatchBlocksBeforeAdministratorStep()
  {
    var source = TempFile("not-trusted");
    try
    {
      var resource = ExtensionResource(
          "Contoso.DeveloperTools",
          "3.2.x",
          "17.0_a",
          source);
      var manifests = new FakeVsixManifestReader
      {
        SourceManifest = new VsixManifest(
            "Contoso.DeveloperTools",
            "3.2.0",
            "source!/extension.vsixmanifest",
            "17.0_a")
      };
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          trustedFileVerifier: new FakeTrustedFileVerifier(isTrusted: false));

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

      Assert.False(plan.IsExecutable);
      Assert.Empty(plan.Steps);
      Assert.Equal(WdemErrorCode.ConfigurationError, Assert.Single(plan.StructuredErrors).Code);
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task PlanAsync_IncompatibleInstallationTargetBlocksBeforeAdministratorStep()
  {
    var source = TempFile("vsix");
    try
    {
      var resource = ExtensionResource(
          "Contoso.DeveloperTools",
          "3.2.x",
          "17.0_a",
          source);
      var manifests = new FakeVsixManifestReader
      {
        SourceManifest = new VsixManifest(
            "Contoso.DeveloperTools",
            "3.2.0",
            "source!/extension.vsixmanifest",
            "17.0_a",
            [new VsixInstallationTarget("Microsoft.VisualStudio.Enterprise", "[17.0,18.0)")])
      };
      var provider = Provider(manifests, new ThrowingProcessExecutor());

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

      Assert.False(plan.IsExecutable);
      Assert.Empty(plan.Steps);
      Assert.Equal(WdemErrorCode.ConfigurationError, Assert.Single(plan.StructuredErrors).Code);
    }
    finally
    {
      File.Delete(source);
    }
  }

  [WindowsFact]
  public async Task PlanAsync_IncompatibleSourceDoesNotRequireRootFileCreationToDiscardArtifact()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-incompatible-{Guid.NewGuid():N}");
    var sharedRoot = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    var source = TempFile("vsix");
    Directory.CreateDirectory(sharedRoot);
    using var identity = WindowsIdentity.GetCurrent();
    var rootSecurity = new DirectorySecurity();
    rootSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    rootSecurity.SetOwner(identity.User!);
    rootSecurity.AddAccessRule(new FileSystemAccessRule(
        identity.User!,
        FileSystemRights.FullControl,
        AccessControlType.Allow));
    rootSecurity.AddAccessRule(new FileSystemAccessRule(
        identity.User!,
        FileSystemRights.CreateFiles,
        AccessControlType.Deny));
    new DirectoryInfo(sharedRoot).SetAccessControl(rootSecurity);

    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a",
          [new VsixInstallationTarget("Microsoft.VisualStudio.Enterprise", "[17.0,18.0)")])
    };
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    var stager = new SecureArtifactStager(
        new TestPlanArtifactDirectoryPolicy(sharedRoot),
        verifier);
    var store = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory,
        WindowsPlanArtifactDirectoryPolicy.GetCurrentUserSid,
        identityNeutralPlanArtifactRoot: sharedRoot,
        protectTerminalState: true,
        revocationStore: new TestPlanArtifactRevocationStore(
            Path.Combine(basePath, "trusted-revocations")));

    try
    {
      var resource = ExtensionResource(
          "Contoso.DeveloperTools",
          "3.2.x",
          "17.0_a",
          source);
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: store);

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

      Assert.False(plan.IsExecutable);
      Assert.Empty(plan.Steps);
      var error = Assert.Single(plan.StructuredErrors);
      Assert.Equal(WdemErrorCode.ConfigurationError, error.Code);
      Assert.Equal("VSIX source is incompatible.", error.Summary);
      Assert.Empty(Directory.EnumerateDirectories(sharedRoot));
    }
    finally
    {
      File.Delete(source);
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }

  [Fact]
  public async Task PlanAsync_ReplanningCleansSupersededStagedArtifact()
  {
    var source = TempFile("vsix");
    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a")
    };
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(manifests, new ThrowingProcessExecutor(), stager);

      var first = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var firstDirectory = Assert.Single(stager.Directories);
      var second = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

      Assert.True(first.IsExecutable);
      Assert.True(second.IsExecutable);
      Assert.False(Directory.Exists(firstDirectory));
      Assert.True(Directory.Exists(stager.Directories[1]));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task PlanAsync_ReplanningRevokesSupersededArtifactWhenCleanupIsBlocked()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var stagingStore = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { });
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: stagingStore);
      var stalePlan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var staleDirectory = Assert.Single(stager.Directories);
      var currentPlan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var staleMarker = await File.ReadAllTextAsync(
          Path.Combine(staleDirectory, ".wdem-vsix-owner"));
      var process = new RecordingProcessExecutor(static () => { });
      var freshProvider = Provider(
          manifests,
          process,
          stager,
          verifier,
          planArtifactStore: PlanArtifactStore(stager, verifier, manifests));
      var result = await freshProvider.ApplyAsync(
          resource,
          stalePlan,
          null,
          CancellationToken.None);

      Assert.True(currentPlan.IsExecutable);
      Assert.Contains("\"revoked\":true", staleMarker, StringComparison.Ordinal);
      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Empty(process.Requests);
    }
    finally
    {
      ArtifactCleanupQueue.Shared.RetryPending();
      File.Delete(source);
    }
  }

  [Fact]
  public async Task PlanAsync_ReplanningRevokesSupersededArtifactWhenCreatorPreloadsRevokedMarker()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
      var stagingStore = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore);
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: stagingStore);
      var stalePlan = await provider.PlanAsync(
          resource,
          Missing(resource),
          CancellationToken.None);
      var staleDirectory = Assert.Single(stager.Directories);
      var staleStep = Assert.Single(stalePlan.Steps);
      ReplaceMarkerEvidence(staleDirectory, "\"revoked\":false", "\"revoked\":true");

      var currentPlan = await provider.PlanAsync(
          resource,
          Missing(resource),
          CancellationToken.None);
      ReplaceMarkerEvidence(staleDirectory, "\"revoked\":true", "\"revoked\":false");
      var replay = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: revocationStore)
          .ClaimAsync(
              resource.Id,
              staleStep.Id,
              new string('A', 64),
              new VsixPlanVisualStudioIdentity(
                  "17.0_a",
                  "Microsoft.VisualStudio.Product.Community",
                  "17.0.0"),
              CancellationToken.None);
      replayedArtifact = replay.Artifact;

      Assert.True(currentPlan.IsExecutable);
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
      File.Delete(source);
    }
  }

  [Fact]
  public async Task PlanAsync_PublicationFailureCannotBeActivatedByUserLedgerAppend()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore);
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: store);

      var first = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      revocationStore.RevokeFailure = new IOException("superseded revoke failed");
      var failed = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var unpublishedDirectory = stager.Directories[1];
      var marker = JsonNode.Parse(await File.ReadAllTextAsync(
          Path.Combine(unpublishedDirectory, ".wdem-vsix-owner")))!;
      var ownershipToken = marker["ownershipToken"]!.GetValue<string>();
      var directoryName = Path.GetFileName(unpublishedDirectory);
      revocationStore.Activate(ownershipToken, directoryName);
      var locator = $"vsix-v2:{ownershipToken}:{directoryName}:{new string('B', 43)}";

      var replay = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: revocationStore)
          .ClaimAsync(
              resource.Id,
              locator,
              new string('A', 64),
              new VsixPlanVisualStudioIdentity(
                  "17.0_a",
                  "Microsoft.VisualStudio.Product.Community",
                  "17.0.0"),
              CancellationToken.None);
      replayedArtifact = replay.Artifact;

      Assert.True(first.IsExecutable);
      Assert.False(failed.IsExecutable);
      Assert.True(Directory.Exists(unpublishedDirectory));
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
      File.Delete(source);
    }
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
              "17.0_a",
              CancellationToken.None);
      var locator = Assert.IsType<string>(staged.StepEvidence);
      var activationProof = locator.Split(':')[3];
      var directory = Path.GetDirectoryName(stager.VerifiedVsixPath)!;

      Assert.Equal(43, activationProof.Length);
      Assert.DoesNotContain(
          activationProof,
          await File.ReadAllTextAsync(Path.Combine(directory, ".wdem-vsix-owner")),
          StringComparison.Ordinal);
      Assert.DoesNotContain(
          activationProof,
          await File.ReadAllTextAsync(revocationPath),
          StringComparison.Ordinal);
    }
    finally
    {
      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanAsync_AbandonedStagedArtifactExpiresDeterministically()
  {
    var source = TempFile("vsix");
    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a")
    };
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          handoffLifetime: TimeSpan.FromMilliseconds(20));

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      for (var attempt = 0; attempt < 100 && Directory.Exists(directory); attempt++)
      {
        await Task.Delay(10);
      }

      Assert.True(plan.IsExecutable);
      Assert.False(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task PlanAsync_TimerWaitsForDurableExpiryAndRetainsRegistrationWhenRevocationFails()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationAttempted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var revocationStore = new TestPlanArtifactRevocationStore(revocationPath)
      {
        BeforeRevoke = () => revocationAttempted.TrySetResult(),
        RevokeFailure = new IOException("revocation append failed")
      };
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          handoffLifetime: TimeSpan.FromMilliseconds(300),
          deleteDirectory: static _ => { },
          revocationStore: revocationStore);
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: store);

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      await Task.Delay(80);

      Assert.True(plan.IsExecutable);
      Assert.False(revocationAttempted.Task.IsCompleted);
      Assert.True(Directory.Exists(directory));

      await revocationAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.True(Directory.Exists(directory));

      var replay = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: revocationStore)
          .ClaimAsync(
              resource.Id,
              Assert.Single(plan.Steps).Id,
              new string('A', 64),
              new VsixPlanVisualStudioIdentity(
                  "17.0_a",
                  "Microsoft.VisualStudio.Product.Community",
                  "17.0.0"),
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
      File.Delete(source);
    }
  }

  [Fact]
  public async Task PlanAsync_ExpiredActiveLocatorCannotReplayAfterWallClockRollback()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationAttempted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var bootIdentifier = Guid.Parse("00112233-4455-6677-8899-AABBCCDDEEFF");
      const long issuedAtUptimeMilliseconds = 10_000;
      var revocationStore = new TestPlanArtifactRevocationStore(revocationPath)
      {
        BeforeRevoke = () => revocationAttempted.TrySetResult(),
        RevokeFailure = new IOException("revocation append failed")
      };
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var plan = await Provider(
              manifests,
              new ThrowingProcessExecutor(),
              stager,
              verifier,
              planArtifactStore: PlanArtifactStore(
                  stager,
                  verifier,
                  manifests,
                  handoffLifetime: TimeSpan.FromMilliseconds(30),
                  deleteDirectory: static _ => { },
                  revocationStore: revocationStore,
                  getBootIdentifier: () => bootIdentifier,
                  getUptimeMilliseconds: () => issuedAtUptimeMilliseconds))
          .PlanAsync(resource, Missing(resource), CancellationToken.None);
      await revocationAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));

      var replay = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              getUtcNow: () => DateTimeOffset.UtcNow.AddDays(-1),
              revocationStore: revocationStore,
              getBootIdentifier: () => bootIdentifier,
              getUptimeMilliseconds: () => issuedAtUptimeMilliseconds + 1_000)
          .ClaimAsync(
              resource.Id,
              Assert.Single(plan.Steps).Id,
              new string('A', 64),
              new VsixPlanVisualStudioIdentity(
                  "17.0_a",
                  "Microsoft.VisualStudio.Product.Community",
                  "17.0.0"),
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
      File.Delete(source);
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
              "17.0_a",
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
              "17.0_a",
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
        "17.0_a",
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
    var revocationAttempted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var uptimeReadCount = 0;
    try
    {
      var revocationStore = new TestPlanArtifactRevocationStore(revocationPath)
      {
        BeforeRevoke = () => revocationAttempted.TrySetResult()
      };
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          handoffLifetime: TimeSpan.FromHours(1),
          deleteDirectory: static _ => { },
          revocationStore: revocationStore,
          getUptimeMilliseconds: () =>
              Interlocked.Increment(ref uptimeReadCount) == 1 ? 10_000 : -1);

      var staged = await store.StageAsync(
          "extension",
          stager.StagedPath,
          new string('A', 64),
          "17.0_a",
          CancellationToken.None);

      Assert.NotNull(staged.StepEvidence);
      await revocationAttempted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
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
              "17.0_a",
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
              "17.0_a",
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
  [InlineData(false)]
  [InlineData(true)]
  public async Task PlanAsync_ExpiredArtifactCannotReplayAfterCreatorExtendsMarker(
      bool revokeFails)
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var cleanupAttempted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var revocationAttempted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var revocationStore = new TestPlanArtifactRevocationStore(revocationPath)
      {
        BeforeRevoke = () => revocationAttempted.TrySetResult(),
        RevokeFailure = revokeFails ? new IOException("revocation append failed") : null
      };
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          handoffLifetime: TimeSpan.FromMilliseconds(20),
          deleteDirectory: _ => cleanupAttempted.TrySetResult(),
          revocationStore: revocationStore);
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: store);

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      await (revokeFails ? revocationAttempted.Task : cleanupAttempted.Task)
          .WaitAsync(TimeSpan.FromSeconds(5));
      var markerPath = Path.Combine(directory, ".wdem-vsix-owner");
      var marker = JsonNode.Parse(await File.ReadAllTextAsync(markerPath))!;
      marker["expiresAtUtc"] = DateTimeOffset.UtcNow.AddHours(24);
      await File.WriteAllTextAsync(markerPath, marker.ToJsonString());
      var replay = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              revocationStore: revocationStore)
          .ClaimAsync(
              resource.Id,
              Assert.Single(plan.Steps).Id,
              new string('A', 64),
              new VsixPlanVisualStudioIdentity(
                  "17.0_a",
                  "Microsoft.VisualStudio.Product.Community",
                  "17.0.0"),
              CancellationToken.None);
      replayedArtifact = replay.Artifact;

      Assert.Null(replay.Artifact);
      Assert.NotNull(replay.Error);
      Assert.Equal(!revokeFails, cleanupAttempted.Task.IsCompletedSuccessfully);
      Assert.Equal(
          !revokeFails,
          revocationStore.IsRevoked(
              marker["ownershipToken"]!.GetValue<string>(),
              Path.GetFileName(directory)));
    }
    finally
    {
      if (replayedArtifact is not null)
      {
        await replayedArtifact.DisposeAsync();
      }

      File.Delete(revocationPath);
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_InvalidResourceImmediatelyAbandonsApprovedArtifact()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(manifests, new ThrowingProcessExecutor(), stager);
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      var invalidParameters = resource.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value);
      invalidParameters["expectedSha256"] = "invalid";
      var invalidResource = resource with { Parameters = invalidParameters };

      var result = await provider.ApplyAsync(
          invalidResource,
          plan,
          null,
          CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.False(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_InvalidPlanImmediatelyAbandonsApprovedArtifact()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(manifests, new ThrowingProcessExecutor(), stager);
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      var step = Assert.Single(plan.Steps);
      var invalidPlan = plan with { Steps = [step with { IsDestructive = true }] };

      var result = await provider.ApplyAsync(
          resource,
          invalidPlan,
          null,
          CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.False(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_InvalidPlanWithExtraStepImmediatelyAbandonsApprovedArtifact()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(manifests, new ThrowingProcessExecutor(), stager);
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      var step = Assert.Single(plan.Steps);
      var invalidPlan = plan with { Steps = [step, step with { Id = "unexpected" }] };

      var result = await provider.ApplyAsync(
          resource,
          invalidPlan,
          null,
          CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.False(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_PreClaimCancellationImmediatelyAbandonsApprovedArtifact()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(manifests, new ThrowingProcessExecutor(), stager);
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      using var cancellation = new CancellationTokenSource();
      cancellation.Cancel();

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
          provider.ApplyAsync(resource, plan, null, cancellation.Token).AsTask());

      Assert.False(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_MismatchedResourceDoesNotAbandonAnotherApprovedPlan()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var otherResource = resource with { Id = "other-extension" };
      var provider = Provider(manifests, new ThrowingProcessExecutor(), stager);
      var otherPlan = await provider.PlanAsync(
          otherResource,
          Missing(otherResource),
          CancellationToken.None);
      var directory = Assert.Single(stager.Directories);

      var result = await provider.ApplyAsync(
          resource,
          otherPlan,
          null,
          CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.True(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task PlanAsync_OversizedDurableMarkerFailsAndCleansStagedArtifact()
  {
    var source = TempFile("vsix");
    var targets = Enumerable.Range(0, 300)
        .Select(index => new VsixInstallationTarget(
            index == 0
                ? "Microsoft.VisualStudio.Community"
                : $"Contoso.VisualStudio.Target.{index:D3}.{new string('X', 80)}",
            "[17.0,18.0)"))
        .ToArray();
    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a",
          targets)
    };
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource(
          "Contoso.DeveloperTools",
          "3.2.x",
          "17.0_a",
          source);
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          getUtcNow: static () => new DateTimeOffset(
              2026,
              8,
              29,
              15,
              31,
              11,
              TimeSpan.Zero).AddTicks(6_493_749));

      var plan = await provider.PlanAsync(
          resource,
          Missing(resource),
          CancellationToken.None);
      var directory = Assert.Single(stager.Directories);

      Assert.False(plan.IsExecutable);
      Assert.Empty(plan.Steps);
      var error = Assert.Single(plan.StructuredErrors);
      Assert.Equal(WdemErrorCode.ConfigurationError, error.Code);
      Assert.Equal(
          "The VSIX ownership marker is 46031 bytes and exceeds the 16384-byte limit.",
          error.Detail);
      Assert.False(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task PlanAsync_IssuanceAppendFailureDoesNotPublishClaimableArtifact()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          revocationStore: new ThrowingIssuanceRevocationStore());
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: store);

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);

      Assert.False(plan.IsExecutable);
      Assert.Empty(plan.Steps);
      Assert.False(File.Exists(Path.Combine(directory, ".wdem-vsix-owner")));
      Assert.False(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_UsesSelectedInstallerAndOnlyTokenizedArguments()
  {
    var source = TempFile("vsix");
    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a")
    };
    await using var stager = new ScriptedStager();
    var process = new RecordingProcessExecutor(() => manifests.Add(
        @"C:\VS\17.0_a\Common7\IDE\Extensions\Contoso\extension.vsixmanifest",
        "Contoso.DeveloperTools",
        "3.2.0",
        "17.0_a"));
    try
    {
      var resource = ExtensionResource(
          "Contoso.DeveloperTools",
          "3.2.x",
          "17.0_a",
          source);
      var provider = Provider(manifests, process, stager);
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

      var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

      Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
      var request = Assert.Single(process.Requests);
      Assert.Equal(
          Path.GetFullPath(@"C:\VS\17.0_a\Common7\IDE\VSIXInstaller.exe"),
          request.FileName);
      Assert.Equal(["/quiet", "/admin", stager.VerifiedVsixPath], request.Arguments);
      Assert.DoesNotContain(request.Arguments, argument => argument.Contains("devenv", StringComparison.OrdinalIgnoreCase));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Theory]
  [InlineData("Microsoft.VisualStudio.Product.Enterprise", "17.0.0")]
  [InlineData("Microsoft.VisualStudio.Product.Community", "17.1.0")]
  public async Task ApplyAsync_RejectsSelectedInstanceReplacedAfterPlanning(
      string productId,
      string installationVersion)
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    await using var stager = new RotatingStager();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    var store = PlanArtifactStore(stager, verifier, manifests);
    var process = new RecordingProcessExecutor(() => manifests.Add(
        @"C:\VS\17.0_a\Common7\IDE\Extensions\Contoso\extension.vsixmanifest",
        "Contoso.DeveloperTools",
        "3.2.0",
        "17.0_a"));
    try
    {
      var resource = ExtensionResource(
          "Contoso.DeveloperTools",
          "3.2.x",
          "17.0_a",
          source);
      var planningProvider = Provider(
          manifests,
          process,
          stager,
          trustedFileVerifier: verifier,
          planArtifactStore: store);
      var plan = await planningProvider.PlanAsync(
          resource,
          Missing(resource),
          CancellationToken.None);
      var replacement = Instance("17.0_a") with
      {
        ProductId = productId,
        InstallationVersion = installationVersion
      };
      var applyingProvider = Provider(
          manifests,
          process,
          stager,
          trustedFileVerifier: verifier,
          planArtifactStore: store,
          discovery: new FakeVisualStudioDiscovery(replacement));

      var result = await applyingProvider.ApplyAsync(
          resource,
          plan,
          null,
          CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Empty(process.Requests);
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_UsesArtifactApprovedByPlanWithoutReacquiringSource()
  {
    var source = TempFile("vsix");
    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a")
    };
    await using var stager = new ScriptedStager();
    var process = new RecordingProcessExecutor(() => manifests.Add(
        @"C:\VS\17.0_a\Common7\IDE\Extensions\Contoso\extension.vsixmanifest",
        "Contoso.DeveloperTools",
        "3.2.0",
        "17.0_a"));
    try
    {
      var resource = ExtensionResource(
          "Contoso.DeveloperTools",
          "3.2.x",
          "17.0_a",
          source);
      var provider = Provider(manifests, process, stager);
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      File.Delete(source);

      var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

      Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
      Assert.Single(process.Requests);
    }
    finally
    {
      if (File.Exists(source))
      {
        File.Delete(source);
      }
    }
  }

  [Fact]
  public async Task ApplyAsync_HttpsSourceIsAcquiredOnlyWhilePlanning()
  {
    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a")
    };
    await using var stager = new ScriptedStager();
    var handler = new CountingHttpHandler();
    using var httpClient = new HttpClient(handler);
    var process = new RecordingProcessExecutor(() => manifests.Add(
        @"C:\VS\17.0_a\Common7\IDE\Extensions\Contoso\extension.vsixmanifest",
        "Contoso.DeveloperTools",
        "3.2.0",
        "17.0_a"));
    var resource = ExtensionResource(
        "Contoso.DeveloperTools",
        "3.2.x",
        "17.0_a",
        "https://artifacts.example.test/contoso.vsix");
    var provider = Provider(manifests, process, stager, httpClient: httpClient);

    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
    var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Equal(1, handler.RequestCount);
  }

  [Fact]
  public async Task PlanAsync_HttpsSourceStreamsDirectlyIntoRestrictedStaging()
  {
    var manifests = SourceManifestReader();
    await using var stager = new ScriptedStager();
    using var httpClient = new HttpClient(new CountingHttpHandler());
    var resource = ExtensionResource(
        "Contoso.DeveloperTools",
        "3.2.x",
        "17.0_a",
        "https://artifacts.example.test/contoso.vsix");
    var provider = Provider(
        manifests,
        new ThrowingProcessExecutor(),
        stager,
        httpClient: httpClient);

    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    Assert.True(plan.IsExecutable);
    Assert.Equal(1, stager.StreamStageCalls);
    Assert.Equal(0, stager.PathStageCalls);
  }

  [WindowsFact]
  public async Task PlanAndApply_StoreHandsOffFromCurrentUserToApprovedApply()
  {
    var source = TempFile("vsix");
    var sharedRoot = Path.Combine(
        Path.GetTempPath(),
        $"wdem-handoff-{Guid.NewGuid():N}",
        "Wdem",
        "PlanArtifacts");
    string hash;
    await using (var sourceStream = File.OpenRead(source))
    {
      hash = Convert.ToHexString(await SHA256.HashDataAsync(sourceStream));
    }
    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a")
    };
    var process = new RecordingProcessExecutor(() => manifests.Add(
        @"C:\VS\17.0_a\Common7\IDE\Extensions\Contoso\extension.vsixmanifest",
        "Contoso.DeveloperTools",
        "3.2.0",
        "17.0_a"));
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    var stager = new SecureArtifactStager(
        new TestPlanArtifactDirectoryPolicy(sharedRoot),
        verifier);
    var store = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory,
        WindowsPlanArtifactDirectoryPolicy.GetCurrentUserSid,
        identityNeutralPlanArtifactRoot: sharedRoot);
    try
    {
      var resource = ExtensionResource(
          "Contoso.DeveloperTools",
          "3.2.x",
          "17.0_a",
          source,
          hash);
      var provider = Provider(
          manifests,
          process,
          stager,
          trustedFileVerifier: verifier,
          planArtifactStore: store);

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

      Assert.True(plan.IsExecutable);
      Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    }
    finally
    {
      File.Delete(source);
      var basePath = Directory.GetParent(Directory.GetParent(sharedRoot)!.FullName)!.FullName;
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }

  [Fact]
  public async Task ApplyAsync_InstallerFailureRemainsFailedWhenPostDetectionIsCompliant()
  {
    var source = TempFile("vsix");
    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a")
    };
    await using var stager = new ScriptedStager();
    var process = new RecordingProcessExecutor(
        () => manifests.Add(
            @"C:\VS\17.0_a\Common7\IDE\Extensions\Contoso\extension.vsixmanifest",
            "Contoso.DeveloperTools",
            "3.2.0",
            "17.0_a"),
        new ProcessExecutionResult(true, 1, [], []));
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(manifests, process, stager);
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

      var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Equal(1, result.Error!.ProcessExitCode);
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_RejectsChangedApprovedArtifactWithoutExecutingInstaller()
  {
    var source = TempFile("vsix");
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("staged")));
    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a")
    };
    await using var stager = new ScriptedStager();
    var process = new RecordingProcessExecutor(() => { });
    try
    {
      var resource = ExtensionResource(
          "Contoso.DeveloperTools",
          "3.2.x",
          "17.0_a",
          source,
          hash);
      var provider = Provider(manifests, process, stager, new TrustedFileVerifier());
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      await File.WriteAllTextAsync(stager.VerifiedVsixPath, "tampered");

      var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Empty(process.Requests);
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_RejectsTamperedApprovedPlanEvidenceWithoutExecutingInstaller()
  {
    var source = TempFile("vsix");
    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a")
    };
    await using var stager = new ScriptedStager();
    var process = new RecordingProcessExecutor(() => { });
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(manifests, process, stager);
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Path.GetDirectoryName(stager.VerifiedVsixPath)!;
      var step = Assert.Single(plan.Steps);
      const int tamperIndex = 8;
      var replacement = step.Id[tamperIndex] == 'A' ? 'B' : 'A';
      var tampered = plan with
      {
        Steps =
        [
          step with
          {
            Id = step.Id[..tamperIndex] + replacement + step.Id[(tamperIndex + 1)..]
          }
        ]
      };

      var result = await provider.ApplyAsync(resource, tampered, null, CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Empty(process.Requests);
      Assert.True(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_TamperedOtherResourceEvidenceDoesNotAbandonRegisteredArtifact()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var otherResource = resource with { Id = "other-extension" };
      var verifier = new FakeTrustedFileVerifier(isTrusted: true);
      var stagingStore = PlanArtifactStore(stager, verifier, manifests);
      var applyingStore = PlanArtifactStore(stager, verifier, manifests);
      var stagingProvider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: stagingStore);
      var applyingProvider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: applyingStore);
      var plan = await stagingProvider.PlanAsync(
          otherResource,
          Missing(otherResource),
          CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      var step = Assert.Single(plan.Steps);
      var replacement = step.Id[^1] == 'A' ? 'B' : 'A';
      var tampered = plan with
      {
        Steps = [step with { Id = step.Id[..^1] + replacement }]
      };

      var result = await applyingProvider.ApplyAsync(
          resource,
          tampered,
          null,
          CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.True(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_FreshStoreRejectsMalformedCreatorSidMarkerWithoutDeletingIt()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var stagingStore = PlanArtifactStore(stager, verifier, manifests);
      var applyingStore = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          (path, creatorSid) =>
          {
            _ = path;
            _ = new System.Security.Principal.SecurityIdentifier(creatorSid);
          });
      var stagingProvider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: stagingStore);
      var applyingProvider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: applyingStore);
      var plan = await stagingProvider.PlanAsync(
          resource,
          Missing(resource),
          CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      var step = Assert.Single(plan.Steps);
      ReplaceMarkerEvidence(
          directory,
          "\"creatorSid\":\"S-1-0-0\"",
          "\"creatorSid\":\"not-a-sid\"");

      var result = await applyingProvider.ApplyAsync(
          resource,
          plan,
          null,
          CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Equal(WdemErrorCode.ConfigurationError, result.Error?.Code);
      Assert.IsType<ArgumentException>(result.Error?.UnderlyingException);
      Assert.True(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_FreshStoreStalePlanDoesNotDeleteSupersedingArtifact()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var stagingStore = PlanArtifactStore(stager, verifier, manifests);
      var applyingStore = PlanArtifactStore(stager, verifier, manifests);
      var stagingProvider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: stagingStore);
      var applyingProvider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: applyingStore);
      var stalePlan = await stagingProvider.PlanAsync(
          resource,
          Missing(resource),
          CancellationToken.None);
      var currentPlan = await stagingProvider.PlanAsync(
          resource,
          Missing(resource),
          CancellationToken.None);
      var currentDirectory = stager.Directories[1];
      var staleStep = Assert.Single(stalePlan.Steps);
      var replacement = staleStep.Id[^1] == 'A' ? 'B' : 'A';

      var result = await applyingProvider.ApplyAsync(
          resource,
          stalePlan with
          {
            Steps = [staleStep with { Id = staleStep.Id[..^1] + replacement }]
          },
          null,
          CancellationToken.None);

      Assert.True(currentPlan.IsExecutable);
      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.True(Directory.Exists(currentDirectory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_MalformedCreatorSidMarkerReturnsFailureWithoutDeletingIt()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    await using var stager = new RotatingStager();
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          validateRestrictedDirectory: (path, creatorSid) =>
          {
            _ = path;
            _ = new System.Security.Principal.SecurityIdentifier(creatorSid);
          });
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      var step = Assert.Single(plan.Steps);
      ReplaceMarkerEvidence(
          directory,
          "\"creatorSid\":\"S-1-0-0\"",
          "\"creatorSid\":\"not-a-sid\"");

      var result = await provider.ApplyAsync(
          resource,
          plan,
          null,
          CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Equal(WdemErrorCode.ConfigurationError, result.Error?.Code);
      Assert.IsType<ArgumentException>(result.Error?.UnderlyingException);
      Assert.True(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
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
        "17.0_a",
        CancellationToken.None);
    var marker = await File.ReadAllTextAsync(Path.Combine(
        Path.GetDirectoryName(stager.VerifiedVsixPath)!,
        ".wdem-vsix-owner"));
    var claimed = await claimingStore.ClaimAsync(
        "extension",
        staged.StepEvidence!,
        expectedHash,
        "17.0_a",
        CancellationToken.None);

    await using var artifact = Assert.IsType<ClaimedVsixPlanArtifact>(claimed.Artifact);
    Assert.Contains($"\"creatorSid\":\"{creatorSid}\"", marker, StringComparison.Ordinal);
    Assert.Contains("\"resourceId\":\"extension\"", marker, StringComparison.Ordinal);
    Assert.Equal([creatorSid, creatorSid], validatedCreators);
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
    var terminalStatePath = Path.Combine(
        Path.GetDirectoryName(directory)!,
        $".{Path.GetFileName(directory)}.wdem-vsix-terminal");
    ClaimedVsixPlanArtifact? firstArtifact = null;
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var staged = await stagingStore.StageAsync(
          "extension",
          stager.StagedPath,
          expectedHash,
          "17.0_a",
          CancellationToken.None);
      var firstClaim = await claimingStore.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          "17.0_a",
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
          "17.0_a",
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
      File.Delete(terminalStatePath);
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
          "17.0_a",
          CancellationToken.None);
      await File.WriteAllTextAsync(stager.VerifiedVsixPath, "tampered");

      var failedClaim = await claimingStore.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          "17.0_a",
          CancellationToken.None);
      await File.WriteAllBytesAsync(stager.VerifiedVsixPath, original);
      var replay = await replayStore.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          "17.0_a",
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
  public async Task PlanArtifactStore_ClaimStartAppendFailureRetainsActiveLocatorForSafeRetry()
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
              "17.0_a",
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
              "17.0_a",
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
              "17.0_a",
              CancellationToken.None);
      retriedArtifact = retried.Artifact;

      Assert.NotNull(retriedArtifact);
      Assert.Null(retried.Error);
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
  public async Task PlanArtifactStore_AbandonAppendsRevocationWhenLedgerReadIsDenied()
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
              "17.0_a",
              CancellationToken.None);
      var directory = Path.GetDirectoryName(stager.VerifiedVsixPath)!;
      var marker = JsonNode.Parse(await File.ReadAllTextAsync(
          Path.Combine(directory, ".wdem-vsix-owner")))!;
      var ownershipToken = marker["ownershipToken"]!.GetValue<string>();
      var directoryName = Path.GetFileName(directory);
      revocationStore.GetStateFailure = new UnauthorizedAccessException("Users lack ReadData");

      await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              deleteDirectory: _ => deleted = true,
              revocationStore: revocationStore)
          .AbandonAsync("extension", staged.StepEvidence!, CancellationToken.None);

      Assert.True(revocationStore.IsRevoked(ownershipToken, directoryName));
      Assert.True(deleted);
    }
    finally
    {
      File.Delete(revocationPath);
    }
  }

  [Fact]
  public async Task PlanArtifactStore_SupersessionAppendsRevocationWhenLedgerReadIsDenied()
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
          "17.0_a",
          CancellationToken.None);
      var locatorParts = first.StepEvidence!.Split(':');
      revocationStore.GetStateFailure = new UnauthorizedAccessException("Users lack ReadData");

      var second = await store.StageAsync(
          "extension",
          source,
          new string('A', 64),
          "17.0_a",
          CancellationToken.None);

      Assert.NotNull(second.StepEvidence);
      Assert.True(revocationStore.IsRevoked(locatorParts[1], locatorParts[2]));
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
              "17.0_a",
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
              "17.0_a",
              CancellationToken.None),
          secondStore.ClaimAsync(
              "extension",
              staged.StepEvidence!,
              expectedHash,
              "17.0_a",
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
    var terminalStatePath = Path.Combine(
        Path.GetDirectoryName(directory)!,
        $".{Path.GetFileName(directory)}.wdem-vsix-terminal");
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      await File.WriteAllTextAsync(revocationPath, "attacker-controlled-garbage");
      var staged = await stagingStore.StageAsync(
          "extension",
          stager.StagedPath,
          expectedHash,
          "17.0_a",
          CancellationToken.None);
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
          "17.0_a",
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
      File.Delete(terminalStatePath);
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
          "17.0_a",
          CancellationToken.None);
      Assert.NotNull(staged.StepEvidence);
      Assert.InRange(staged.StepEvidence.Length, 1, 128);

      var claimed = await claimingStore.ClaimAsync(
          "extension",
          staged.StepEvidence,
          expectedHash,
          "17.0_a",
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
          "17.0_a",
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
          "17.0_a",
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
          "17.0_a",
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
  public async Task ApplyAsync_RebindsApprovedEvidenceToDesiredHash()
  {
    var source = TempFile("vsix");
    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a")
    };
    await using var stager = new ScriptedStager();
    var process = new RecordingProcessExecutor(() => { });
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(manifests, process, stager);
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var changedParameters = resource.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value);
      changedParameters["expectedSha256"] = new string('B', 64);
      var changedResource = resource with { Parameters = changedParameters };
      var forgedPlan = plan with
      {
        DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(changedResource)
      };

      var result = await provider.ApplyAsync(
          changedResource,
          forgedPlan,
          null,
          CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Empty(process.Requests);
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_RebindsApprovedEvidenceToSelectedInstance()
  {
    var source = TempFile("vsix");
    var manifests = new FakeVsixManifestReader
    {
      SourceManifest = new VsixManifest(
          "Contoso.DeveloperTools",
          "3.2.0",
          "source!/extension.vsixmanifest",
          "17.0_a")
    };
    await using var stager = new ScriptedStager();
    var process = new RecordingProcessExecutor(() => { });
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var provider = Provider(manifests, process, stager);
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var changedParameters = resource.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value);
      changedParameters["instanceId"] = "17.0_b";
      var changedResource = resource with { Parameters = changedParameters };
      var forgedPlan = plan with
      {
        DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(changedResource)
      };

      var result = await provider.ApplyAsync(
          changedResource,
          forgedPlan,
          null,
          CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Empty(process.Requests);
    }
    finally
    {
      File.Delete(source);
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
      ISecureArtifactStager? stager = null,
      ITrustedFileVerifier? trustedFileVerifier = null,
      HttpClient? httpClient = null,
      TimeSpan? handoffLifetime = null,
      Action<string, string>? validateRestrictedDirectory = null,
      IVsixPlanArtifactStore? planArtifactStore = null,
      Func<DateTimeOffset>? getUtcNow = null,
      IVisualStudioDiscovery? discovery = null)
  {
    var verifier = trustedFileVerifier ?? new FakeTrustedFileVerifier(isTrusted: true);
    var artifactStager = stager ?? new SecureArtifactStager(verifier: verifier);
    var artifactStore = planArtifactStore ?? PlanArtifactStore(
        artifactStager,
        verifier,
        manifests,
        validateRestrictedDirectory,
        handoffLifetime,
        getUtcNow);
    return new VisualStudioExtensionProvider(
        discovery ?? new FakeVisualStudioDiscovery(Instance("17.0_a"), Instance("17.0_b")),
        manifests,
        process,
        new ComplianceEvaluator(),
        artifactStager,
        httpClient,
        verifier,
        artifactStore);
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
      Func<long>? getUptimeMilliseconds = null) => new(
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
          getUptimeMilliseconds: getUptimeMilliseconds);

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
    public Action? BeforeRevoke { get; init; }
    public Exception? RevokeFailure { get; set; }
    public Exception? ClaimStartedFailure { get; set; }
    public Exception? GetStateFailure { get; set; }

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

    public void ClaimStarted(string ownershipToken, string directoryName)
    {
      if (ClaimStartedFailure is not null)
      {
        throw ClaimStartedFailure;
      }

      Append(handle => WindowsPlanArtifactDirectoryPolicy.WriteClaimStartedRecord(
          handle,
          ownershipToken,
          directoryName));
    }

    public void Consume(string ownershipToken, string directoryName) =>
        Append(handle => WindowsPlanArtifactDirectoryPolicy.WriteConsumedRecord(
            handle,
            ownershipToken,
            directoryName));

    public VsixPlanArtifactLedgerState GetState(string ownershipToken, string directoryName)
    {
      if (GetStateFailure is not null)
      {
        throw GetStateFailure;
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

      Append(handle => WindowsPlanArtifactDirectoryPolicy.WriteRevocationRecord(
          handle,
          ownershipToken,
          directoryName));
    }

    public bool IsRevoked(string ownershipToken, string directoryName) =>
        File.Exists(path) && WindowsPlanArtifactDirectoryPolicy.ContainsRevocationRecord(
            File.ReadAllBytes(path),
            ownershipToken,
            directoryName);

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

  private sealed class ThrowingIssuanceRevocationStore : IVsixPlanArtifactRevocationStore
  {
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

    public void ClaimStarted(string ownershipToken, string directoryName)
    {
    }

    public void Consume(string ownershipToken, string directoryName)
    {
    }

    public VsixPlanArtifactLedgerState GetState(string ownershipToken, string directoryName) =>
        throw new System.Security.SecurityException("No issuance was recorded.");

    public void Revoke(string ownershipToken, string directoryName)
    {
    }

    public bool IsRevoked(string ownershipToken, string directoryName) => false;
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

  private sealed class RecordingProcessExecutor(
      Action afterExecute,
      ProcessExecutionResult? result = null) : IProcessExecutor
  {
    public List<ProcessExecutionRequest> Requests { get; } = [];

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      Requests.Add(request);
      afterExecute();
      return Task.FromResult(result ?? new ProcessExecutionResult(true, 0, [], []));
    }
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
