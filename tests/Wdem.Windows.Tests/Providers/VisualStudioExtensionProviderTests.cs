using System.IO.Compression;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Composition;
using Wdem.Windows.Persistence;
using Wdem.Windows.Providers;
using Wdem.Windows.Security;
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

      var result = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

      Assert.Equal(ApplyOutcome.Failed, result.Outcome);
      Assert.Equal(WdemErrorCode.ConfigurationError, result.Error!.Code);
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
  public async Task VsixManifestReader_ReadsStableIdentityFromArchiveAndRejectsMissingIdentity()
  {
    var validPath = TempVsix(
        "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\">" +
        "<Metadata><Identity Id=\"Contoso.DeveloperTools\" Version=\"3.2.0\" />" +
        "<Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" Version=\"[17.0,18.0)\" />" +
        "</Installation></Metadata>" +
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
      ITrustedFileVerifier? trustedFileVerifier = null) => new(
          new FakeVisualStudioDiscovery(Instance("17.0_a"), Instance("17.0_b")),
          manifests,
          process,
          new ComplianceEvaluator(),
          stager,
          httpClient: null,
          trustedFileVerifier ?? new FakeTrustedFileVerifier(isTrusted: true));

  private static ResourceDefinition ExtensionResource(
      string extensionId,
      string version,
      string instanceId,
      string source = @"C:\Artifacts\contoso.vsix") => new()
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
          ["expectedSha256"] = new string('A', 64),
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

    public void Add(string path, string id, string version, string instanceId) =>
        _manifests.Add(new VsixManifest(id, version, path, instanceId));

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

  private sealed class RecordingProcessExecutor(Action afterExecute) : IProcessExecutor
  {
    public List<ProcessExecutionRequest> Requests { get; } = [];

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      Requests.Add(request);
      afterExecute();
      return Task.FromResult(new ProcessExecutionResult(true, 0, [], []));
    }
  }

  private sealed class ThrowingProcessExecutor : IProcessExecutor
  {
    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken) => throw new InvalidOperationException();
  }
}
