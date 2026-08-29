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
    var revocationStore = new TestPlanArtifactRevocationStore(
        Path.Combine(basePath, "trusted-revocations"))
    {
      RevokeFailure = new UnauthorizedAccessException("append denied")
    };
    var store = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        WindowsPlanArtifactDirectoryPolicy.ValidateRestrictedDirectory,
        WindowsPlanArtifactDirectoryPolicy.GetCurrentUserSid,
        identityNeutralPlanArtifactRoot: sharedRoot,
        protectTerminalState: true,
        revocationStore: revocationStore);

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
      Assert.Empty(Directory.EnumerateFileSystemEntries(sharedRoot));
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
  public async Task PlanAsync_DeniedSupersessionRetainsPriorPlanAndDoesNotPublishNewLocator()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    ClaimedVsixPlanArtifact? priorArtifact = null;
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var store = PlanArtifactStore(
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
          planArtifactStore: store);
      var priorPlan = await provider.PlanAsync(
          resource,
          Missing(resource),
          CancellationToken.None);
      var priorStep = Assert.Single(priorPlan.Steps);
      var priorDirectory = Assert.Single(stager.Directories);
      revocationStore.RevokeFailure = new UnauthorizedAccessException("append denied");

      var replacementPlan = await provider.PlanAsync(
          resource,
          Missing(resource),
          CancellationToken.None);

      Assert.False(replacementPlan.IsExecutable);
      Assert.Empty(replacementPlan.Steps);
      var error = Assert.Single(replacementPlan.StructuredErrors);
      Assert.Equal(WdemErrorCode.ConfigurationError, error.Code);
      Assert.Contains("prior plan remains valid", error.Detail, StringComparison.OrdinalIgnoreCase);
      Assert.True(Directory.Exists(priorDirectory));
      Assert.DoesNotContain(
          "\"revoked\":true",
          await File.ReadAllTextAsync(Path.Combine(priorDirectory, ".wdem-vsix-owner")),
          StringComparison.Ordinal);

      revocationStore.RevokeFailure = null;
      var priorClaim = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              deleteDirectory: static _ => { },
              revocationStore: revocationStore)
          .ClaimAsync(
              resource.Id,
              priorStep.Id,
              resource.Parameters["expectedSha256"]!,
              VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
              CancellationToken.None);
      priorArtifact = priorClaim.Artifact;
      Assert.True(
          priorArtifact is not null,
          $"{priorClaim.Error?.Detail} {priorClaim.Error?.UnderlyingException}");
      Assert.Null(priorClaim.Error);
    }
    finally
    {
      if (priorArtifact is not null)
      {
        await priorArtifact.DisposeAsync();
      }

      File.Delete(revocationPath);
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
      var unpublishedDirectory = Assert.Single(stager.Directories);
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
      Assert.False(File.Exists(revocationPath));
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
    var initialNow = DateTimeOffset.UtcNow;
    var expired = false;
    var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cleanupCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var verifier = new FakeTrustedFileVerifier(isTrusted: true);
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          handoffLifetime: TimeSpan.FromMinutes(5),
          getUtcNow: () => expired ? initialNow.AddHours(1) : initialNow,
          deleteDirectory: _ => cleanupCompleted.TrySetResult(),
          delay: async (_, cancellationToken) =>
          {
            delayEntered.TrySetResult();
            await releaseDelay.Task.WaitAsync(cancellationToken);
            expired = true;
          });
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: store);

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

      Assert.True(Directory.Exists(directory));
      releaseDelay.TrySetResult();
      await cleanupCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));

      Assert.True(plan.IsExecutable);
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task PlanAsync_TimerWaitsForControlledExpiryAndDurablyTerminatesBeforeCleanup()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    var initialNow = DateTimeOffset.UtcNow;
    var expired = false;
    var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cleanupAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          handoffLifetime: TimeSpan.FromMinutes(5),
          getUtcNow: () => expired ? initialNow.AddHours(1) : initialNow,
          deleteDirectory: _ => cleanupAttempted.TrySetResult(),
          delay: async (_, cancellationToken) =>
          {
            delayEntered.TrySetResult();
            await releaseDelay.Task.WaitAsync(cancellationToken);
            expired = true;
          });
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: store);

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      var locator = Assert.Single(plan.Steps).Id;
      await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

      Assert.True(plan.IsExecutable);
      Assert.True(Directory.Exists(directory));
      Assert.False(File.Exists(TerminalStatePath(directory, locator)));
      Assert.False(Directory.Exists(TerminalStatePath(directory, locator)));

      releaseDelay.TrySetResult();
      await cleanupAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));
      Assert.True(File.Exists(TerminalStatePath(directory, locator)));

      var replay = await PlanArtifactStore(
              stager,
              verifier,
              manifests)
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

      Assert.Null(replay.Artifact);
      Assert.NotNull(replay.Error);
    }
    finally
    {
      if (replayedArtifact is not null)
      {
        await replayedArtifact.DisposeAsync();
      }

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
    var cleanupAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var expired = false;
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var bootIdentifier = Guid.Parse("00112233-4455-6677-8899-AABBCCDDEEFF");
      const long issuedAtUptimeMilliseconds = 10_000;
      var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
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
                  deleteDirectory: _ => cleanupAttempted.TrySetResult(),
                  revocationStore: revocationStore,
                  getBootIdentifier: () => bootIdentifier,
                  getUptimeMilliseconds: () =>
                      expired ? issuedAtUptimeMilliseconds + 1_000 : issuedAtUptimeMilliseconds,
                  delay: async (_, cancellationToken) =>
                  {
                    delayEntered.TrySetResult();
                    await releaseDelay.Task.WaitAsync(cancellationToken);
                    expired = true;
                  }))
          .PlanAsync(resource, Missing(resource), CancellationToken.None);
      await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
      releaseDelay.TrySetResult();
      await cleanupAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));

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
          "17.0_a",
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

  [Fact]
  public async Task PlanAsync_ExpiredArtifactCannotReplayAfterCreatorExtendsMarker()
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
    var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var initialNow = DateTimeOffset.UtcNow;
    var expired = false;
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
      var store = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          handoffLifetime: TimeSpan.FromMinutes(5),
          getUtcNow: () => expired ? initialNow.AddHours(1) : initialNow,
          deleteDirectory: _ => cleanupAttempted.TrySetResult(),
          revocationStore: revocationStore,
          delay: async (_, cancellationToken) =>
          {
            delayEntered.TrySetResult();
            await releaseDelay.Task.WaitAsync(cancellationToken);
            expired = true;
          });
      var provider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: store);

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
      releaseDelay.TrySetResult();
      await cleanupAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));
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
      Assert.True(File.Exists(TerminalStatePath(
          directory,
          Assert.Single(plan.Steps).Id)));
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
  public async Task ApplyAsync_InvalidResourceDeniedRevokeSurfacesFailureAndCannotReplayApprovedArtifact()
  {
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    ClaimedVsixPlanArtifact? replayedArtifact = null;
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var store = PlanArtifactStore(
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
          planArtifactStore: store);
      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var step = Assert.Single(plan.Steps);
      var invalidParameters = resource.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value);
      invalidParameters["expectedSha256"] = "invalid";
      var invalidResource = resource with { Parameters = invalidParameters };
      revocationStore.RevokeFailure = new UnauthorizedAccessException("append denied");

      await Assert.ThrowsAsync<System.Security.SecurityException>(() =>
          provider.ApplyAsync(
              invalidResource,
              plan,
              null,
              CancellationToken.None).AsTask());

      var locator = step.Id.Split(':');
      var directory = Assert.Single(stager.Directories);
      Assert.Equal(
          VsixPlanArtifactLedgerStatus.ClaimStarted,
          revocationStore.GetState(locator[1], Path.GetFileName(directory)).Status);

      var sameWorkerReplay = await store.ClaimAsync(
          resource.Id,
          step.Id,
          resource.Parameters["expectedSha256"]!,
          "17.0_a",
          CancellationToken.None);
      Assert.Null(sameWorkerReplay.Artifact);
      Assert.NotNull(sameWorkerReplay.Error);

      revocationStore.RevokeFailure = null;
      var replay = await PlanArtifactStore(
              stager,
              verifier,
              manifests,
              deleteDirectory: static _ => { },
              revocationStore: revocationStore)
          .ClaimAsync(
              resource.Id,
              step.Id,
              resource.Parameters["expectedSha256"]!,
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
      Assert.Contains("exceeds the 16384-byte limit", error.Detail, StringComparison.Ordinal);
      Assert.False(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Fact]
  public async Task ApplyAsync_PrivilegedIssuanceFailureCannotClaimPublishedArtifact()
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
      var process = new RecordingProcessExecutor(static () => { });
      var provider = Provider(
          manifests,
          process,
          stager,
          verifier,
          planArtifactStore: store);

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var directory = Assert.Single(stager.Directories);
      var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

      Assert.True(plan.IsExecutable);
      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Empty(process.Requests);
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
      Assert.Equal("/quiet", request.Arguments[0]);
      Assert.Equal("/admin", request.Arguments[1]);
      Assert.EndsWith(".wdem-vsix-install", request.Arguments[2], StringComparison.Ordinal);
      Assert.NotEqual(stager.VerifiedVsixPath, request.Arguments[2]);
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
      Assert.False(Directory.Exists(directory));
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
      Assert.IsType<InvalidDataException>(result.Error?.UnderlyingException);
      Assert.False(Directory.Exists(directory));
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
      Assert.IsType<InvalidDataException>(result.Error?.UnderlyingException);
      Assert.False(Directory.Exists(directory));
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
        "17.0_a",
        CancellationToken.None);
    var claimed = await store.ClaimAsync(
        "extension",
        staged.StepEvidence!,
        expectedHash,
        "17.0_a",
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
          "17.0_a",
          CancellationToken.None);
      terminalStatePath = TerminalStatePath(directory, staged.StepEvidence!);
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
          "17.0_a",
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
          "17.0_a",
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
              "17.0_a",
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
              "17.0_a",
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
              "17.0_a",
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
              "17.0_a",
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
              "17.0_a",
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
              "17.0_a",
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
              "17.0_a",
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
          "17.0_a",
          CancellationToken.None);
      var firstDirectory = Assert.Single(stager.Directories);
      revocationStore.GetStateFailure = new UnauthorizedAccessException("Users lack ReadData");

      var second = await store.StageAsync(
          "extension",
          source,
          new string('A', 64),
          "17.0_a",
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
              "17.0_a",
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
          "17.0_a",
          CancellationToken.None);
      claimedArtifact = winningClaim.Artifact;
      var losingClaim = await losingStore.ClaimAsync(
          "extension",
          staged.StepEvidence!,
          expectedHash,
          "17.0_a",
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
  public async Task ApplyAsync_LosingClaimAbandonRevokesWinningContinuationAcrossStores()
  {
    const string winningNonce =
        "1111111111111111111111111111111111111111111111111111111111111111";
    const string losingNonce =
        "2222222222222222222222222222222222222222222222222222222222222222";
    var source = TempFile("vsix");
    var manifests = SourceManifestReader();
    var verifier = new FakeTrustedFileVerifier(isTrusted: true);
    await using var stager = new RotatingStager();
    var revocationPath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-test-revocations-{Guid.NewGuid():N}");
    var revocationStore = new TestPlanArtifactRevocationStore(revocationPath);
    ClaimedVsixPlanArtifact? claimedArtifact = null;
    string? terminalStatePath = null;
    try
    {
      var resource = ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a", source);
      var stagingProvider = Provider(
          manifests,
          new ThrowingProcessExecutor(),
          stager,
          verifier,
          planArtifactStore: PlanArtifactStore(
              stager,
              verifier,
              manifests,
              deleteDirectory: static _ => { },
              revocationStore: revocationStore));
      var plan = await stagingProvider.PlanAsync(
          resource,
          Missing(resource),
          CancellationToken.None);
      var step = Assert.Single(plan.Steps);
      var directory = Assert.Single(stager.Directories);
      terminalStatePath = TerminalStatePath(directory, step.Id);
      Directory.CreateDirectory(terminalStatePath);
      var winningStore = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore,
          createClaimNonce: static () => winningNonce);
      var losingStore = PlanArtifactStore(
          stager,
          verifier,
          manifests,
          deleteDirectory: static _ => { },
          revocationStore: revocationStore,
          createClaimNonce: static () => losingNonce);

      await winningStore.BeginClaimAsync(
          resource.Id,
          step.Id,
          CancellationToken.None);
      var losingResult = await Provider(
              manifests,
              new ThrowingProcessExecutor(),
              stager,
              verifier,
              planArtifactStore: losingStore)
          .ApplyAsync(resource, plan, null, CancellationToken.None);
      Directory.Delete(terminalStatePath);
      terminalStatePath = null;
      ReplaceMarkerEvidence(directory, "\"revoked\":true", "\"revoked\":false");

      var winningClaim = await winningStore.ClaimAsync(
          resource.Id,
          step.Id,
          resource.Parameters["expectedSha256"]!,
          VsixPlanVisualStudioIdentity.FromInstance(Instance("17.0_a")),
          CancellationToken.None);
      claimedArtifact = winningClaim.Artifact;

      Assert.Equal(ApplyOutcome.Failed, losingResult.Outcome);
      Assert.True(revocationStore.IsRevoked(
          step.Id.Split(':')[1],
          Path.GetFileName(directory)));
      Assert.Null(winningClaim.Artifact);
      Assert.NotNull(winningClaim.Error);
    }
    finally
    {
      if (claimedArtifact is not null)
      {
        await claimedArtifact.DisposeAsync();
      }

      DeleteTerminalState(terminalStatePath);
      File.Delete(revocationPath);
      File.Delete(source);
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
          "17.0_a",
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

    public void Consume(string ownershipToken, string directoryName)
    {
      lock (_sync)
      {
        _state = _state!.Value with { Status = VsixPlanArtifactLedgerStatus.Consumed };
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
        _state = _state!.Value with { Status = VsixPlanArtifactLedgerStatus.Revoked };
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

    public void Consume(string ownershipToken, string directoryName)
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
