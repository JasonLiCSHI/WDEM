using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
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

  [WindowsFact]
  public async Task PlanAndApply_DefaultStoreHandsOffFromCurrentUserToApprovedApply()
  {
    var source = TempFile("vsix");
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
    try
    {
      var resource = ExtensionResource(
          "Contoso.DeveloperTools",
          "3.2.x",
          "17.0_a",
          source,
          hash);
      var provider = new VisualStudioExtensionProvider(
          new FakeVisualStudioDiscovery(Instance("17.0_a")),
          manifests,
          process,
          new ComplianceEvaluator());

      var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);
      var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

      Assert.True(plan.IsExecutable);
      Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    }
    finally
    {
      File.Delete(source);
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
      var replacement = step.Id[^1] == 'A' ? 'B' : 'A';
      var tampered = plan with
      {
        Steps = [step with { Id = step.Id[..^1] + replacement }]
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
      var provider = Provider(manifests, new ThrowingProcessExecutor(), stager);
      var plan = await provider.PlanAsync(
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

      var result = await provider.ApplyAsync(
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
  public async Task ApplyAsync_MalformedCreatorSidReturnsStructuredFailureAndCleansRegisteredArtifact()
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
      var malformedStepId = ReplaceEncodedEvidence(
          step.Id,
          "\"creatorSid\":\"S-1-0-0\"",
          "\"creatorSid\":\"not-a-sid\"");

      var result = await provider.ApplyAsync(
          resource,
          plan with { Steps = [step with { Id = malformedStepId }] },
          null,
          CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Equal(WdemErrorCode.ConfigurationError, result.Error?.Code);
      Assert.IsType<ArgumentException>(result.Error?.UnderlyingException);
      Assert.False(Directory.Exists(directory));
    }
    finally
    {
      File.Delete(source);
    }
  }

  [Theory]
  [InlineData("null")]
  [InlineData("{}")]
  public void HasValidStepEvidence_MalformedJsonReturnsFalse(string json)
  {
    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    var valid = VsixPlanArtifactStore.HasValidStepEvidence(
        "extension",
        $"extension:install:vsix-v1:{encoded}");

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
    var stagingStore = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        (_, recordedCreator) => validatedCreators.Add(recordedCreator),
        () => creatorSid);
    var claimingStore = new VsixPlanArtifactStore(
        stager,
        verifier,
        manifests,
        (_, recordedCreator) => validatedCreators.Add(recordedCreator),
        () => "S-1-5-18");
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
        $"extension:install:{staged.StepEvidence}",
        expectedHash,
        "17.0_a",
        CancellationToken.None);

    await using var artifact = Assert.IsType<ClaimedVsixPlanArtifact>(claimed.Artifact);
    Assert.StartsWith(creatorSid + "\n", marker, StringComparison.Ordinal);
    Assert.Equal([creatorSid, creatorSid], validatedCreators);
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
      Action<string, string>? validateRestrictedDirectory = null)
  {
    var verifier = trustedFileVerifier ?? new FakeTrustedFileVerifier(isTrusted: true);
    var artifactStager = stager ?? new SecureArtifactStager(verifier: verifier);
    var artifactStore = new VsixPlanArtifactStore(
        artifactStager,
        verifier,
        manifests,
        validateRestrictedDirectory: validateRestrictedDirectory ?? ((_, _) => { }),
        getCurrentUserSid: static () => "S-1-0-0",
        handoffLifetime: handoffLifetime);
    return new VisualStudioExtensionProvider(
        new FakeVisualStudioDiscovery(Instance("17.0_a"), Instance("17.0_b")),
        manifests,
        process,
        new ComplianceEvaluator(),
        artifactStager,
        httpClient,
        verifier,
        artifactStore);
  }

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

  private static string ReplaceEncodedEvidence(
      string stepId,
      string expected,
      string replacement)
  {
    const string evidencePrefix = "vsix-v1:";
    var evidenceIndex = stepId.IndexOf(evidencePrefix, StringComparison.Ordinal);
    Assert.True(evidenceIndex >= 0);
    var encodedIndex = stepId.IndexOf(':', evidenceIndex + evidencePrefix.Length) + 1;
    Assert.True(encodedIndex > evidenceIndex + evidencePrefix.Length);
    var encoded = stepId[encodedIndex..].Replace('-', '+').Replace('_', '/');
    encoded += new string('=', (4 - encoded.Length % 4) % 4);
    var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    Assert.Contains(expected, json, StringComparison.Ordinal);
    json = json.Replace(expected, replacement, StringComparison.Ordinal);
    var tampered = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
    return stepId[..encodedIndex] + tampered;
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
      _directory = Path.Combine(Path.GetTempPath(), $"wdem-staged-vsix-{Guid.NewGuid():N}");
      Directory.CreateDirectory(_directory);
      StagedPath = Path.Combine(_directory, "installer.exe");
      File.WriteAllText(StagedPath, "staged");
    }

    public string StagedPath { get; }
    public string VerifiedVsixPath => Path.Combine(_directory, "extension.vsix");

    public Task<SecureArtifactStageResult> StageVerifiedAsync(
        string sourcePath,
        string expectedSha256,
        SecureArtifactKind kind,
        CancellationToken cancellationToken)
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
        CancellationToken cancellationToken)
    {
      var directory = Path.Combine(Path.GetTempPath(), $"wdem-rotating-{Guid.NewGuid():N}");
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
