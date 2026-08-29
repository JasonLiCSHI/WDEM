using System.Security.Cryptography;
using System.Text;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Providers;
using Wdem.Core.Profiles;
using Wdem.Core.Resources;
using Wdem.Windows.Configuration;
using Wdem.Windows.Composition;
using Wdem.Windows.Providers;
using Xunit;

namespace Wdem.Windows.Tests.Providers;

public sealed class ConfigurationProviderTests : IDisposable
{
  private readonly string _root = Path.Combine(
      Path.GetTempPath(),
      $"wdem-configuration-provider-{Guid.NewGuid():N}");

  [Fact]
  public async Task ApplyAsync_DotSettingsCopiesAtomicallyAndVerifiesDestinationHash()
  {
    var profiles = Path.Combine(_root, "profiles");
    var sourcePath = Path.Combine(profiles, "team.DotSettings");
    var destinationPath = Path.Combine(_root, "user", "team.DotSettings");
    Directory.CreateDirectory(profiles);
    var contents = Encoding.UTF8.GetBytes("<wpf:ResourceDictionary />");
    await File.WriteAllBytesAsync(sourcePath, contents);
    var expectedHash = Convert.ToHexString(SHA256.HashData(contents));
    var provider = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new ComplianceEvaluator(),
        Path.Combine(_root, "user"));
    var resource = Resource(
        "resharper-settings",
        "resharper-settings",
        "file",
        ["resharper"],
        new Dictionary<string, string?>
        {
          ["sourcePath"] = "team.DotSettings",
          ["expectedSha256"] = expectedHash,
          ["destinationPath"] = destinationPath
        });
    var missing = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, missing, CancellationToken.None);

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);
    var verified = await provider.VerifyAsync(resource, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, applied.Outcome);
    Assert.Equal(contents, await File.ReadAllBytesAsync(destinationPath));
    Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(
        await File.ReadAllBytesAsync(destinationPath))));
    Assert.Equal(ComplianceStatus.Satisfied, verified.Compliance);
    Assert.Equal(expectedHash, verified.DetectedState.ConfigurationHash);
  }

  [Fact]
  public async Task ValidateAsync_VsSettingsRequiresVisualStudioAndTrustedSource()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new EmptyVisualStudioDiscovery(),
        new NeverProcessExecutor(),
        new ComplianceEvaluator());
    var resource = Resource(
        "visual-studio-settings",
        "visual-studio-settings",
        "visual-studio-settings",
        [],
        new Dictionary<string, string?>
        {
          ["sourcePath"] = "team.vssettings",
          ["expectedSha256"] = "not-a-sha256",
          ["instanceId"] = "vs-main",
          ["settingsStorePath"] = Path.Combine(_root, "settings", "team.vssettings")
        });

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);

    Assert.False(validation.IsValid);
    Assert.Contains(validation.StructuredErrors, error => error.Code == WdemErrorCode.DependencyError);
    Assert.Contains(validation.StructuredErrors, error => error.Code == WdemErrorCode.ConfigurationError);
  }

  [Fact]
  public async Task ValidateAsync_ReSharperSettingsRejectsAlternateDataStreamDestination()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var destinationRoot = Path.Combine(_root, "user");
    var provider = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new ComplianceEvaluator(),
        destinationRoot);
    var resource = Resource(
        "resharper-settings",
        "resharper-settings",
        "file",
        ["resharper"],
        new Dictionary<string, string?>
        {
          ["sourcePath"] = "team.DotSettings",
          ["expectedSha256"] = new string('A', 64),
          ["destinationPath"] = "host.DotSettings:stream.DotSettings"
        });

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);

    Assert.False(validation.IsValid);
    Assert.Contains(validation.StructuredErrors,
        error => error.Code == WdemErrorCode.ConfigurationError &&
            error.Detail.Contains("alternate data stream", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public async Task ValidateAsync_VisualStudioSettingsRejectsAlternateDataStreamDestination()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new EmptyVisualStudioDiscovery(),
        new NeverProcessExecutor(),
        new ComplianceEvaluator(),
        _ => Path.Combine(_root, "Visual Studio 18"));
    var resource = VisualStudioSettingsResource(
        new string('A', 64),
        "host.vssettings:stream.vssettings");

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);

    Assert.False(validation.IsValid);
    Assert.Contains(validation.StructuredErrors,
        error => error.Code == WdemErrorCode.ConfigurationError &&
            error.Detail.Contains("alternate data stream", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public async Task VerifyAsync_DeclaredSettingsStoreHashMismatchIsConfigurationMismatch()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(settingsRoot);
    var source = Encoding.UTF8.GetBytes("source settings");
    await File.WriteAllBytesAsync(sourcePath, source);
    await File.WriteAllTextAsync(settingsStorePath, "different settings");
    var sourceHash = Convert.ToHexString(SHA256.HashData(source));
    var instance = VisualStudioInstance();
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(instance),
        new NeverProcessExecutor(),
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(sourceHash, settingsStorePath) with
    {
      Parameters = new Dictionary<string, string?>(
          VisualStudioSettingsResource(sourceHash, settingsStorePath).Parameters,
          StringComparer.OrdinalIgnoreCase)
      {
        ["settingsStoreSha256"] = sourceHash
      }
    };

    var verification = await provider.VerifyAsync(resource, CancellationToken.None);

    Assert.Equal(ComplianceStatus.ConfigurationMismatch, verification.Compliance);
    Assert.Equal(WdemErrorCode.ConfigurationError,
        verification.DetectedState.StructuredError?.Code ?? WdemErrorCode.ConfigurationError);
  }

  [Fact]
  public async Task ApplyAsync_VsSettingsUsesSelectedDevenvWithTokenizedArgumentsOnlyDuringApply()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team settings.vssettings");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("source settings");
    await File.WriteAllBytesAsync(sourcePath, source);
    var sourceHash = Convert.ToHexString(SHA256.HashData(source));
    var instance = VisualStudioInstance();
    var process = new RecordingProcessExecutor();
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(instance),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(sourceHash, settingsStorePath);

    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var verifiedBefore = await provider.VerifyAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);
    Assert.Empty(process.Requests);

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, applied.Outcome);
    var request = Assert.Single(process.Requests);
    Assert.Equal(instance.ProductPath, request.FileName);
    Assert.Equal(["/Command", "File.ImportSettings", settingsStorePath], request.Arguments);
    Assert.Equal(ComplianceStatus.Missing, verifiedBefore.Compliance);
  }

  [Fact]
  public async Task PlanAsync_VsSettingsRejectsNonDevenvProductPathWithoutExecution()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("source settings");
    await File.WriteAllBytesAsync(sourcePath, source);
    var sourceHash = Convert.ToHexString(SHA256.HashData(source));
    var instance = VisualStudioInstance() with { ProductPath = @"C:\Windows\System32\cmd.exe" };
    var process = new RecordingProcessExecutor();
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(instance),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(
        sourceHash,
        Path.Combine(settingsRoot, "team.vssettings"));
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, detected.Outcome);
    Assert.Equal(WdemErrorCode.ConfigurationError, detected.StructuredError?.Code);
    Assert.False(plan.IsExecutable);
    Assert.Empty(process.Requests);
  }

  [Fact]
  public async Task CSharpDeveloperProfile_LoadsAndEnterpriseInputsExpandOnlyWhenSelected()
  {
    var profilePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "profiles", "csharp-developer.yaml"));
    var registry = new ResourceProviderRegistry([
      new AcceptingProvider("visual-studio", "visual-studio"),
      new AcceptingProvider("dotnet-sdk", "winget"),
      new AcceptingProvider("git", "winget"),
      new AcceptingProvider("resharper", "winget"),
      new AcceptingProvider("visual-studio-extension", "vsix"),
      new AcceptingProvider("resharper-settings", "file"),
      new AcceptingProvider("visual-studio-settings", "visual-studio-settings")
    ]);
    var result = await new DirectoryProfileCatalog(Path.GetDirectoryName(profilePath)!, registry)
        .LoadFileAsync(profilePath);

    Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(error => error.Detail)));
    Assert.Equal("csharp-developer", result.Profile!.Id);
    Assert.Equal(["visual-studio", "dotnet-sdk", "git"],
        result.Profile.RequiredResources.Select(resource => resource.Id));
    Assert.All(result.Profile.OptionalResources, resource => Assert.False(resource.DefaultSelected));
    Assert.Equal("${WDEM_COMPANY_VSIX_PATH}",
        result.Profile.Resources["company-vs-extension"].Parameters["sourcePath"]);
    Assert.Equal("${WDEM_COMPANY_VSIX_SHA256}",
        result.Profile.Resources["company-vs-extension"].Parameters["expectedSha256"]);

    var unselected = ProfileValueExpander.ExpandSelected(result.Profile, [], _ => null);
    var partial = ProfileValueExpander.ExpandSelected(
        result.Profile,
        ["company-vs-extension"],
        name => name == "WDEM_COMPANY_VSIX_PATH" ? @"C:\company.vsix" : null);
    var valid = ProfileValueExpander.ExpandSelected(
        result.Profile,
        ["company-vs-extension"],
        name => name == "WDEM_COMPANY_VSIX_PATH"
            ? @"C:\company.vsix"
            : new string('A', 64));

    Assert.True(unselected.IsValid);
    Assert.False(partial.IsValid);
    Assert.Equal(WdemErrorCode.ProfileError, Assert.Single(partial.Errors).Code);
    Assert.True(valid.IsValid);
    Assert.Equal(@"C:\company.vsix",
        valid.Profile!.Resources["company-vs-extension"].Parameters["sourcePath"]);

    var missingInputs = new ResourceGraphBuilder(_ => null).TryBuild(
        result.Profile,
        new ProfileSelection(new HashSet<string>(["company-vs-extension"], StringComparer.OrdinalIgnoreCase)));
    Assert.Equal(2, missingInputs.Errors.Count(error => error.Code == WdemErrorCode.ProfileError));
  }

  [Fact]
  public async Task CSharpDeveloperProfile_ValidatesWithProductionProviderCatalog()
  {
    var repositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));
    var profiles = Path.Combine(repositoryRoot, "profiles");
    var providers = WindowsProviderCompositionFactory.Create(
        logFilePath: null,
        repositoryRoot,
        profiles).Providers;

    var result = await new DirectoryProfileCatalog(profiles, providers)
        .LoadAsync("csharp-developer", CancellationToken.None);

    Assert.True(result.IsValid,
        string.Join(Environment.NewLine, result.Errors.Select(error => error.Detail)));
  }

  [Fact]
  public async Task CSharpDeveloperProfile_SettingsAssetsHaveRealHashesAndPortableDestinations()
  {
    var repositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));
    var profiles = Path.Combine(repositoryRoot, "profiles");
    var providers = WindowsProviderCompositionFactory.Create(null, repositoryRoot, profiles).Providers;
    var loaded = await new DirectoryProfileCatalog(profiles, providers)
        .LoadAsync("csharp-developer", CancellationToken.None);
    Assert.True(loaded.IsValid);
    var profile = loaded.Profile!;
    var reSharperResource = profile.Resources["resharper-settings"];
    var visualStudioResource = profile.Resources["visual-studio-settings"];

    AssertConfigurationAsset(repositoryRoot, reSharperResource, ".DotSettings");
    AssertConfigurationAsset(repositoryRoot, visualStudioResource, ".vssettings");
    Assert.False(Path.IsPathFullyQualified(reSharperResource.Parameters["destinationPath"]!));
    Assert.False(Path.IsPathFullyQualified(visualStudioResource.Parameters["settingsStorePath"]!));
    Assert.DoesNotContain("Users\\Default", await File.ReadAllTextAsync(
        Path.Combine(profiles, "csharp-developer.yaml")), StringComparison.OrdinalIgnoreCase);

    var reSharperRoot = Path.Combine(_root, "current-user", "JetBrains");
    var reSharper = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(repositoryRoot, profiles),
        new ConfigurationImporter(),
        new ComplianceEvaluator(),
        reSharperRoot);
    var reSharperDetected = await reSharper.DetectAsync(reSharperResource, CancellationToken.None);
    var reSharperPlan = await reSharper.PlanAsync(reSharperResource, reSharperDetected, CancellationToken.None);
    var reSharperApplied = await reSharper.ApplyAsync(
        reSharperResource, reSharperPlan, null, CancellationToken.None);
    Assert.Equal(ApplyOutcome.Succeeded, reSharperApplied.Outcome);
    Assert.Equal(ComplianceStatus.Satisfied,
        (await reSharper.VerifyAsync(reSharperResource, CancellationToken.None)).Compliance);

    var settingsRoot = Path.Combine(_root, "current-user", "Visual Studio 18");
    var process = new RecordingProcessExecutor();
    var instance = VisualStudioInstance() with
    {
      InstanceId = visualStudioResource.Parameters["instanceId"]!
    };
    var visualStudio = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(repositoryRoot, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(instance),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var vsDetected = await visualStudio.DetectAsync(visualStudioResource, CancellationToken.None);
    var vsPlan = await visualStudio.PlanAsync(visualStudioResource, vsDetected, CancellationToken.None);
    Assert.Empty(process.Requests);
    var vsApplied = await visualStudio.ApplyAsync(
        visualStudioResource, vsPlan, null, CancellationToken.None);
    Assert.Equal(ApplyOutcome.Succeeded, vsApplied.Outcome);
    Assert.Equal(ComplianceStatus.Satisfied,
        (await visualStudio.VerifyAsync(visualStudioResource, CancellationToken.None)).Compliance);
    Assert.Single(process.Requests);
  }

  [Theory]
  [InlineData("", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
  [InlineData(@"C:\company.vsix", "not-a-sha256")]
  public async Task EnterpriseVsix_MalformedSelectedInputReturnsProfileErrorBeforePlanning(
      string path,
      string hash)
  {
    var repositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));
    var profiles = Path.Combine(repositoryRoot, "profiles");
    var catalog = new DirectoryProfileCatalog(
        profiles,
        WindowsProviderCompositionFactory.Create(null, repositoryRoot, profiles).Providers);
    var loaded = await catalog.LoadAsync("csharp-developer", CancellationToken.None);
    Assert.True(loaded.IsValid);

    var graph = new ResourceGraphBuilder(name => name switch
    {
      "WDEM_COMPANY_VSIX_PATH" => path,
      "WDEM_COMPANY_VSIX_SHA256" => hash,
      _ => null
    }).TryBuild(
        loaded.Profile!,
        new ProfileSelection(new HashSet<string>(["company-vs-extension"], StringComparer.OrdinalIgnoreCase)));

    var error = Assert.Single(graph.Errors);
    Assert.Equal(WdemErrorCode.ProfileError, error.Code);
  }

  private static ResourceDefinition Resource(
      string id,
      string type,
      string provider,
      IReadOnlyList<string> dependencies,
      IReadOnlyDictionary<string, string?> parameters) => new()
      {
        Id = id,
        Type = type,
        Provider = provider,
        Dependencies = dependencies,
        Parameters = parameters
      };

  private static ResourceDefinition VisualStudioSettingsResource(
      string expectedHash,
      string settingsStorePath) => Resource(
          "visual-studio-settings",
          "visual-studio-settings",
          "visual-studio-settings",
          ["visual-studio"],
          new Dictionary<string, string?>
          {
            ["sourcePath"] = "team.vssettings",
            ["expectedSha256"] = expectedHash,
            ["instanceId"] = "vs-main",
            ["edition"] = "Community",
            ["channelId"] = "VisualStudio.18.Release",
            ["settingsStorePath"] = settingsStorePath
          });

  private static void AssertConfigurationAsset(
      string repositoryRoot,
      ResourceDefinition resource,
      string extension)
  {
    var source = resource.Parameters["sourcePath"]!;
    Assert.EndsWith(extension, source, StringComparison.OrdinalIgnoreCase);
    var path = Path.GetFullPath(Path.Combine(repositoryRoot, source));
    Assert.True(File.Exists(path), $"Missing configuration asset: {path}");
    Assert.Equal(
        resource.Parameters["expectedSha256"],
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
        ignoreCase: true);
  }

  private static Wdem.Windows.VisualStudio.VisualStudioInstance VisualStudioInstance() => new()
  {
    InstanceId = "vs-main",
    InstallationPath = @"C:\VS",
    ProductId = "Microsoft.VisualStudio.Product.Community",
    ProductPath = @"C:\VS\Common7\IDE\devenv.exe",
    ProductDisplayVersion = "18.0",
    InstallationVersion = "18.0.0",
    ChannelId = "VisualStudio.18.Release",
    Edition = "Community",
    IsComplete = true,
    IsLaunchable = true
  };

  public void Dispose()
  {
    if (Directory.Exists(_root))
    {
      Directory.Delete(_root, recursive: true);
    }
  }

  private sealed class EmptyVisualStudioDiscovery : Wdem.Windows.VisualStudio.IVisualStudioDiscovery
  {
    public Task<IReadOnlyList<Wdem.Windows.VisualStudio.VisualStudioInstance>> DiscoverAsync(
        IReadOnlyList<string> requiredWorkloads,
        IReadOnlyList<string> requiredComponents,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Wdem.Windows.VisualStudio.VisualStudioInstance>>([]);
  }

  private sealed class NeverProcessExecutor : Wdem.Core.Processes.IProcessExecutor
  {
    public Task<Wdem.Core.Processes.ProcessExecutionResult> ExecuteAsync(
        Wdem.Core.Processes.ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken) => throw new InvalidOperationException("Validation must not launch a process.");
  }

  private sealed class FixedVisualStudioDiscovery(
      Wdem.Windows.VisualStudio.VisualStudioInstance instance) : Wdem.Windows.VisualStudio.IVisualStudioDiscovery
  {
    public Task<IReadOnlyList<Wdem.Windows.VisualStudio.VisualStudioInstance>> DiscoverAsync(
        IReadOnlyList<string> requiredWorkloads,
        IReadOnlyList<string> requiredComponents,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Wdem.Windows.VisualStudio.VisualStudioInstance>>([instance]);
  }

  private sealed class RecordingProcessExecutor : Wdem.Core.Processes.IProcessExecutor
  {
    public List<Wdem.Core.Processes.ProcessExecutionRequest> Requests { get; } = [];

    public Task<Wdem.Core.Processes.ProcessExecutionResult> ExecuteAsync(
        Wdem.Core.Processes.ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      Requests.Add(request);
      return Task.FromResult(new Wdem.Core.Processes.ProcessExecutionResult(true, 0, [], []));
    }
  }

  private sealed class AcceptingProvider(string resourceType, string providerName) : IResourceProvider
  {
    public string ResourceType => resourceType;
    public string ProviderName => providerName;
    public ProviderCapabilities Capabilities { get; } = new();
    public ValueTask<ProviderValidationResult> ValidateAsync(ResourceDefinition resource, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);
    public ValueTask<DetectedState> DetectAsync(ResourceDefinition resource, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<ResourcePlan> PlanAsync(ResourceDefinition resource, DetectedState currentState, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<ResourceApplyResult> ApplyAsync(ResourceDefinition resource, ResourcePlan plan, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<VerificationResult> VerifyAsync(ResourceDefinition resource, CancellationToken cancellationToken) => throw new NotSupportedException();
  }
}
