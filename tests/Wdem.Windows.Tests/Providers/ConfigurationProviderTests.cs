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
    var jetBrainsRoot = Path.Combine(_root, "user");
    var destinationPath = ReSharperDestination(jetBrainsRoot);
    Directory.CreateDirectory(profiles);
    var contents = Encoding.UTF8.GetBytes("<wpf:ResourceDictionary />");
    await File.WriteAllBytesAsync(sourcePath, contents);
    var expectedHash = Convert.ToHexString(SHA256.HashData(contents));
    var provider = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new ComplianceEvaluator(),
        jetBrainsRoot);
    var resource = Resource(
        "resharper-settings",
        "resharper-settings",
        "file",
        ["resharper"],
        new Dictionary<string, string?>
        {
          ["sourcePath"] = "team.DotSettings",
          ["expectedSha256"] = expectedHash,
          ["destinationPath"] = ReSharperRelativeDestination()
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
  public async Task ApplyAsync_DotSettingsRejectsStaleNoOpPlan()
  {
    var profiles = Path.Combine(_root, "profiles");
    var destinationRoot = Path.Combine(_root, "user");
    var sourcePath = Path.Combine(profiles, "team.DotSettings");
    var destinationPath = ReSharperDestination(destinationRoot);
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
    var contents = Encoding.UTF8.GetBytes("original settings");
    await File.WriteAllBytesAsync(sourcePath, contents);
    await File.WriteAllBytesAsync(destinationPath, contents);
    var expectedHash = Convert.ToHexString(SHA256.HashData(contents));
    var provider = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new ComplianceEvaluator(),
        destinationRoot);
    var resource = ReSharperSettingsResource(expectedHash, ReSharperRelativeDestination());
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);
    await File.WriteAllTextAsync(destinationPath, "changed after planning");

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    Assert.Contains("stale", applied.Error!.Detail, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task ApplyAsync_DotSettingsRejectsDestinationCreatedAfterPlanning()
  {
    var profiles = Path.Combine(_root, "profiles");
    var destinationRoot = Path.Combine(_root, "user");
    var sourcePath = Path.Combine(profiles, "team.DotSettings");
    var destinationPath = ReSharperDestination(destinationRoot);
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
    var contents = Encoding.UTF8.GetBytes("source settings");
    await File.WriteAllBytesAsync(sourcePath, contents);
    var expectedHash = Convert.ToHexString(SHA256.HashData(contents));
    var provider = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new ComplianceEvaluator(),
        destinationRoot);
    var resource = ReSharperSettingsResource(expectedHash, ReSharperRelativeDestination());
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);
    await File.WriteAllTextAsync(destinationPath, "created after planning");

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    Assert.Contains("stale", applied.Error!.Detail, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task ApplyAsync_DotSettingsRejectsDestinationChangedBeforeAtomicCommit()
  {
    var profiles = Path.Combine(_root, "profiles");
    var destinationRoot = Path.Combine(_root, "user");
    var sourcePath = Path.Combine(profiles, "team.DotSettings");
    var destinationPath = ReSharperDestination(destinationRoot);
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
    var source = Encoding.UTF8.GetBytes("new settings");
    var previous = Encoding.UTF8.GetBytes("planned settings");
    var external = Encoding.UTF8.GetBytes("external writer");
    await File.WriteAllBytesAsync(sourcePath, source);
    await File.WriteAllBytesAsync(destinationPath, previous);
    var provider = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new ComplianceEvaluator(),
        destinationRoot);
    var resource = ReSharperSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        ReSharperRelativeDestination());
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);
    var progress = new InlineProgress(update =>
    {
      if (update.Stage == "Apply")
      {
        File.WriteAllBytes(destinationPath, external);
      }
    });

    var applied = await provider.ApplyAsync(resource, plan, progress, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    Assert.Contains("stale", applied.Error!.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(external, await File.ReadAllBytesAsync(destinationPath));
  }

  [Fact]
  public async Task ApplyAsync_DotSettingsRejectsDestinationChangedAtFinalCommitPoint()
  {
    var profiles = Path.Combine(_root, "profiles");
    var jetBrainsRoot = Path.Combine(_root, "JetBrains");
    var sourcePath = Path.Combine(profiles, "team.DotSettings");
    var destinationPath = ReSharperDestination(jetBrainsRoot);
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
    var source = Encoding.UTF8.GetBytes("new settings");
    var previous = Encoding.UTF8.GetBytes("planned settings");
    var external = Encoding.UTF8.GetBytes("final concurrent writer");
    await File.WriteAllBytesAsync(sourcePath, source);
    await File.WriteAllBytesAsync(destinationPath, previous);
    var importer = new ConfigurationImporter(
        afterDestinationMove: null,
        afterDestinationDirectoryLeased: null,
        beforeDestinationPreconditionCheck: path => File.WriteAllBytes(path, external));
    var provider = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        importer,
        new ComplianceEvaluator(),
        jetBrainsRoot);
    var resource = ReSharperSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        Path.Combine("Shared", "vAny", "GlobalSettingsStorage.DotSettings"));
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    Assert.Contains("stale", applied.Error!.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(external, await File.ReadAllBytesAsync(destinationPath));
  }

  [Fact]
  public async Task ApplyAsync_DotSettingsCancellationAfterCommitFinalizesSuccess()
  {
    var profiles = Path.Combine(_root, "profiles");
    var destinationRoot = Path.Combine(_root, "user");
    var sourcePath = Path.Combine(profiles, "team.DotSettings");
    var destinationPath = ReSharperDestination(destinationRoot);
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("new settings");
    await File.WriteAllBytesAsync(sourcePath, source);
    using var cancellation = new CancellationTokenSource();
    var provider = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(_ => cancellation.Cancel()),
        new ComplianceEvaluator(),
        destinationRoot);
    var resource = ReSharperSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        ReSharperRelativeDestination());
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);

    var applied = await provider.ApplyAsync(resource, plan, null, cancellation.Token);

    Assert.Equal(ApplyOutcome.Succeeded, applied.Outcome);
    Assert.True(applied.FinalizeAfterCancellation);
    Assert.Equal(source, await File.ReadAllBytesAsync(destinationPath));
  }

  [Fact]
  public async Task ApplyAsync_VsSettingsRejectsDestinationCreatedAfterPlanning()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(settingsRoot);
    var contents = Encoding.UTF8.GetBytes("source settings");
    await File.WriteAllBytesAsync(sourcePath, contents);
    var expectedHash = Convert.ToHexString(SHA256.HashData(contents));
    var process = new RecordingProcessExecutor();
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(VisualStudioInstance()),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(expectedHash, settingsStorePath);
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);
    await File.WriteAllTextAsync(settingsStorePath, "created after planning");

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    Assert.Contains("stale", applied.Error!.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(process.Requests);
  }

  [Fact]
  public async Task ApplyAsync_VsSettingsRejectsDestinationChangedImmediatelyBeforeLaunch()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(settingsRoot);
    var source = Encoding.UTF8.GetBytes("source settings");
    var previous = Encoding.UTF8.GetBytes("planned settings");
    var external = Encoding.UTF8.GetBytes("external writer");
    await File.WriteAllBytesAsync(sourcePath, source);
    await File.WriteAllBytesAsync(settingsStorePath, previous);
    var process = new RecordingProcessExecutor();
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(VisualStudioInstance()),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        settingsStorePath);
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);
    var progress = new InlineProgress(update =>
    {
      if (update.Stage == "Apply")
      {
        File.WriteAllBytes(settingsStorePath, external);
      }
    });

    var applied = await provider.ApplyAsync(resource, plan, progress, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    Assert.Contains("stale", applied.Error!.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(external, await File.ReadAllBytesAsync(settingsStorePath));
    Assert.Empty(process.Requests);
  }

  [Fact]
  public async Task ApplyAsync_VsSettingsRejectsDestinationChangedAfterLaunchBeforeCommit()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(settingsRoot);
    var source = Encoding.UTF8.GetBytes("source settings");
    var previous = Encoding.UTF8.GetBytes("planned settings");
    var external = Encoding.UTF8.GetBytes("external writer");
    await File.WriteAllBytesAsync(sourcePath, source);
    await File.WriteAllBytesAsync(settingsStorePath, previous);
    var process = new DelegatingProcessExecutor((_, _) =>
    {
      File.WriteAllBytes(settingsStorePath, external);
      return Task.FromResult(new Wdem.Core.Processes.ProcessExecutionResult(true, 0, [], []));
    });
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(VisualStudioInstance()),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        settingsStorePath);
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    Assert.Contains("stale", applied.Error!.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(external, await File.ReadAllBytesAsync(settingsStorePath));
    Assert.Single(process.Requests);
    var step = Assert.Single(applied.StepResults);
    Assert.Equal(0, step.ProcessExitCode);
    Assert.False(step.Succeeded);
    Assert.Equal(applied.Error, step.Error);
    Assert.True(applied.FinalizeAfterCancellation);
  }

  [Fact]
  public async Task ApplyAsync_VsSettingsRejectsDestinationChangedAtFinalCommitPoint()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(settingsRoot);
    var source = Encoding.UTF8.GetBytes("source settings");
    var previous = Encoding.UTF8.GetBytes("planned settings");
    var external = Encoding.UTF8.GetBytes("final concurrent writer");
    await File.WriteAllBytesAsync(sourcePath, source);
    await File.WriteAllBytesAsync(settingsStorePath, previous);
    var importer = new ConfigurationImporter(
        afterDestinationMove: null,
        afterDestinationDirectoryLeased: null,
        beforeDestinationPreconditionCheck: path => File.WriteAllBytes(path, external));
    var process = new RecordingProcessExecutor();
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        importer,
        new FixedVisualStudioDiscovery(VisualStudioInstance()),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        settingsStorePath);
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    Assert.Contains("stale", applied.Error!.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(external, await File.ReadAllBytesAsync(settingsStorePath));
    Assert.Equal(0, Assert.Single(applied.StepResults).ProcessExitCode);
  }

  [Fact]
  public async Task ApplyAsync_VsSettingsRejectsStaleNoOpPlan()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(settingsRoot);
    var contents = Encoding.UTF8.GetBytes("source settings");
    await File.WriteAllBytesAsync(sourcePath, contents);
    await File.WriteAllBytesAsync(settingsStorePath, contents);
    var expectedHash = Convert.ToHexString(SHA256.HashData(contents));
    var process = new RecordingProcessExecutor();
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(VisualStudioInstance()),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(expectedHash, settingsStorePath);
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);
    await File.WriteAllTextAsync(settingsStorePath, "changed after planning");

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    Assert.Contains("stale", applied.Error!.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(process.Requests);
  }

  [Fact]
  public async Task ApplyAsync_VsSettingsNoOpRejectsChangedVisualStudioInstanceEvidence()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(settingsRoot);
    var contents = Encoding.UTF8.GetBytes("source settings");
    await File.WriteAllBytesAsync(sourcePath, contents);
    await File.WriteAllBytesAsync(settingsStorePath, contents);
    var expectedHash = Convert.ToHexString(SHA256.HashData(contents));
    var originalInstance = VisualStudioInstance();
    var discovery = new MutableVisualStudioDiscovery(originalInstance);
    var process = new RecordingProcessExecutor();
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        discovery,
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(expectedHash, settingsStorePath);
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);
    discovery.Instances =
    [
      originalInstance with
      {
        ProductDisplayVersion = "18.1",
        InstallationVersion = "18.1.0"
      }
    ];

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    Assert.Contains("stale", applied.Error!.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(process.Requests);
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
  public async Task ValidateAsync_VisualStudioSettingsAcceptsFileUriSource()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var resource = VisualStudioSettingsResource(new string('A', 64), "team.vssettings") with
    {
      Parameters = new Dictionary<string, string?>(
          VisualStudioSettingsResource(new string('A', 64), "team.vssettings").Parameters,
          StringComparer.OrdinalIgnoreCase)
      {
        ["sourcePath"] = new Uri(sourcePath).AbsoluteUri
      }
    };
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new EmptyVisualStudioDiscovery(),
        new NeverProcessExecutor(),
        new ComplianceEvaluator(),
        _ => Path.Combine(_root, "Visual Studio 18"));

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);

    Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
  }

  [Fact]
  public async Task ValidateAsync_VisualStudioSettingsAcceptsUncFileUriSource()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var resource = VisualStudioSettingsResource(new string('A', 64), "team.vssettings") with
    {
      Parameters = new Dictionary<string, string?>(
          VisualStudioSettingsResource(new string('A', 64), "team.vssettings").Parameters,
          StringComparer.OrdinalIgnoreCase)
      {
        ["sourcePath"] = "file://server/share/team.vssettings"
      }
    };
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new EmptyVisualStudioDiscovery(),
        new NeverProcessExecutor(),
        new ComplianceEvaluator(),
        _ => Path.Combine(_root, "Visual Studio 18"));

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);

    Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
  }

  [Fact]
  public async Task DetectAsync_VsSettingsUsesInstanceScopedLocalAppDataSettingsDirectory()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("source settings");
    await File.WriteAllBytesAsync(Path.Combine(profiles, "team.vssettings"), source);
    var expectedHash = Convert.ToHexString(SHA256.HashData(source));
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(VisualStudioInstance()),
        new NeverProcessExecutor(),
        new ComplianceEvaluator());
    var resource = VisualStudioSettingsResource(expectedHash, "team.vssettings");

    var detected = await provider.DetectAsync(resource, CancellationToken.None);

    var expected = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft",
        "VisualStudio",
        "18.0_vs-main",
        "Settings",
        "team.vssettings");
    Assert.Equal(expected, detected.Evidence["settingsStorePath"]);
    Assert.DoesNotContain(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        detected.Evidence["settingsStorePath"],
        StringComparison.OrdinalIgnoreCase);
    Assert.Equal("visual-studio-installer", provider.Capabilities.ConcurrencyGroup);
  }

  [Fact]
  public async Task DetectAsync_VsSettingsExplicitNonLaunchableInstanceIsClassifiedMissing()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("source settings");
    await File.WriteAllBytesAsync(Path.Combine(profiles, "team.vssettings"), source);
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new MutableVisualStudioDiscovery(VisualStudioInstance() with { IsLaunchable = false }),
        new NeverProcessExecutor(),
        new ComplianceEvaluator(),
        _ => Path.Combine(_root, "settings"));
    var resource = VisualStudioSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        "team.vssettings");

    var detected = await provider.DetectAsync(resource, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, detected.Outcome);
    Assert.False(detected.Exists);
    Assert.Empty(detected.Evidence);
  }

  [Fact]
  public async Task DetectAsync_VsSettingsImplicitSelectionIgnoresNonLaunchableCandidate()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("source settings");
    await File.WriteAllBytesAsync(Path.Combine(profiles, "team.vssettings"), source);
    var nonLaunchable = VisualStudioInstance() with
    {
      InstanceId = "vs-disabled",
      IsLaunchable = false
    };
    var launchable = VisualStudioInstance() with { InstanceId = "vs-ready" };
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new MutableVisualStudioDiscovery(nonLaunchable, launchable),
        new NeverProcessExecutor(),
        new ComplianceEvaluator(),
        _ => Path.Combine(_root, "settings"));
    var resource = WithOptionalInstanceSelector(VisualStudioSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        "team.vssettings"));

    var detected = await provider.DetectAsync(resource, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, detected.Outcome);
    Assert.Equal("vs-ready", detected.Evidence["visualStudioInstanceId"]);
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
  public async Task ValidateAsync_ReSharperSettingsRejectsAlternateFileWithinJetBrainsRoot()
  {
    var profiles = Path.Combine(_root, "profiles");
    var jetBrainsRoot = Path.Combine(_root, "JetBrains");
    Directory.CreateDirectory(profiles);
    var provider = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new ComplianceEvaluator(),
        jetBrainsRoot);
    var resource = ReSharperSettingsResource(
        new string('A', 64),
        Path.Combine(jetBrainsRoot, "Shared", "vAny", "Other.DotSettings"));

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);

    Assert.False(validation.IsValid);
    Assert.Contains(validation.StructuredErrors,
        error => error.Code == WdemErrorCode.ConfigurationError &&
            error.Detail.Contains("GlobalSettingsStorage", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public async Task ValidateAsync_ReSharperSettingsRejectsExactAbsoluteDestination()
  {
    var profiles = Path.Combine(_root, "profiles");
    var jetBrainsRoot = Path.Combine(_root, "JetBrains");
    Directory.CreateDirectory(profiles);
    var provider = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new ComplianceEvaluator(),
        jetBrainsRoot);
    var resource = ReSharperSettingsResource(
        new string('A', 64),
        Path.Combine(
            jetBrainsRoot,
            "Shared",
            "vAny",
            "GlobalSettingsStorage.DotSettings"));

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);

    Assert.False(validation.IsValid);
    Assert.Contains(validation.StructuredErrors,
        error => error.Code == WdemErrorCode.ConfigurationError &&
            error.Detail.Contains("relative", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public async Task ValidateAsync_ReSharperSettingsAcceptsFixedRelativeDestination()
  {
    var profiles = Path.Combine(_root, "profiles");
    var jetBrainsRoot = Path.Combine(_root, "JetBrains");
    Directory.CreateDirectory(profiles);
    var provider = new ReSharperSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new ComplianceEvaluator(),
        jetBrainsRoot);
    var resource = ReSharperSettingsResource(
        new string('A', 64),
        Path.Combine("Shared", "vAny", "GlobalSettingsStorage.DotSettings"));

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);

    Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
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
    var structuredError = Assert.IsType<StructuredError>(
        verification.DetectedState.StructuredError);
    Assert.Equal(WdemErrorCode.ConfigurationError, structuredError.Code);
  }

  [Fact]
  public async Task ApplyAsync_DeclaredSettingsStoreHashMismatchFailsFinalVerification()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("source settings");
    await File.WriteAllBytesAsync(sourcePath, source);
    var sourceHash = Convert.ToHexString(SHA256.HashData(source));
    var declaredStoreHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("different")));
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(VisualStudioInstance()),
        new RecordingProcessExecutor(),
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var original = VisualStudioSettingsResource(sourceHash, settingsStorePath);
    var resource = original with
    {
      Parameters = new Dictionary<string, string?>(original.Parameters, StringComparer.OrdinalIgnoreCase)
      {
        ["settingsStoreSha256"] = declaredStoreHash
      }
    };
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    var error = Assert.IsType<StructuredError>(applied.Error);
    Assert.Equal(WdemErrorCode.ConfigurationError, error.Code);
    var step = Assert.Single(applied.StepResults);
    Assert.Equal(0, step.ProcessExitCode);
    Assert.False(step.Succeeded);
    Assert.Equal(error, step.Error);
    Assert.True(applied.FinalizeAfterCancellation);
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
    Assert.Equal(
        ["/ResetSettings", request.Arguments[1], "/Command", "Exit"],
        request.Arguments);
    Assert.NotEqual(settingsStorePath, request.Arguments[1]);
    Assert.Equal(settingsRoot, Path.GetDirectoryName(request.Arguments[1]));
    Assert.Equal(source, await File.ReadAllBytesAsync(settingsStorePath));
    Assert.False(File.Exists(request.Arguments[1]));
    Assert.Equal(ComplianceStatus.Missing, verifiedBefore.Compliance);
  }

  [Fact]
  public async Task ApplyAsync_VsSettingsLeasesVerifiedStagingBytesWhileDevenvConsumesThem()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("verified source settings");
    await File.WriteAllBytesAsync(sourcePath, source);
    var mutationRejected = false;
    var process = new DelegatingProcessExecutor((request, _) =>
    {
      try
      {
        File.WriteAllText(request.Arguments[1], "tampered after verification");
      }
      catch (IOException)
      {
        mutationRejected = true;
      }

      Assert.Equal(source, File.ReadAllBytes(request.Arguments[1]));
      return Task.FromResult(new Wdem.Core.Processes.ProcessExecutionResult(true, 0, [], []));
    });
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(VisualStudioInstance()),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        settingsStorePath);
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, applied.Outcome);
    Assert.True(mutationRejected);
    Assert.Equal(source, await File.ReadAllBytesAsync(settingsStorePath));
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task VerifyAsync_VsSettingsRevalidatesSourceWithoutLaunchingVisualStudio(
      bool deleteSource)
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("source settings");
    await File.WriteAllBytesAsync(sourcePath, source);
    var sourceHash = Convert.ToHexString(SHA256.HashData(source));
    var process = new RecordingProcessExecutor();
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(VisualStudioInstance()),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(sourceHash, settingsStorePath);
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);
    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);
    Assert.Equal(ApplyOutcome.Succeeded, applied.Outcome);

    if (deleteSource)
    {
      File.Delete(sourcePath);
    }
    else
    {
      await File.WriteAllTextAsync(sourcePath, "tampered settings");
    }

    var verification = await provider.VerifyAsync(resource, CancellationToken.None);

    Assert.NotEqual(ComplianceStatus.Satisfied, verification.Compliance);
    Assert.Equal(DetectionOutcome.Failed, verification.DetectedState.Outcome);
    Assert.Equal(WdemErrorCode.ConfigurationError,
        verification.DetectedState.StructuredError?.Code);
    Assert.Single(process.Requests);
  }

  [Theory]
  [InlineData(false, null)]
  [InlineData(true, 17)]
  public async Task ApplyAsync_VsSettingsProcessFailurePreservesSnapshotAndCleansStaging(
      bool started,
      int? exitCode)
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(settingsRoot);
    var source = Encoding.UTF8.GetBytes("new source settings");
    var previous = Encoding.UTF8.GetBytes("previous settings snapshot");
    await File.WriteAllBytesAsync(sourcePath, source);
    await File.WriteAllBytesAsync(settingsStorePath, previous);
    var sourceHash = Convert.ToHexString(SHA256.HashData(source));
    var process = new DelegatingProcessExecutor((request, _) =>
    {
      Assert.NotEqual(settingsStorePath, request.Arguments[1]);
      Assert.Equal(settingsRoot, Path.GetDirectoryName(request.Arguments[1]));
      Assert.Equal(source, File.ReadAllBytes(request.Arguments[1]));
      return Task.FromResult(new Wdem.Core.Processes.ProcessExecutionResult(
          started, exitCode, [], []));
    });
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(VisualStudioInstance()),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(sourceHash, settingsStorePath);
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);
    var afterFailure = await provider.DetectAsync(resource, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    Assert.Equal(previous, await File.ReadAllBytesAsync(settingsStorePath));
    Assert.NotEqual(ComplianceStatus.Satisfied,
        new ComplianceEvaluator().Evaluate(resource, afterFailure).Status);
    Assert.Equal([settingsStorePath], Directory.GetFiles(settingsRoot));
    Assert.Single(process.Requests);
  }

  [Fact]
  public async Task ApplyAsync_VsSettingsLateCancellationFinalizesSuccessfulImport()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(settingsRoot);
    var source = Encoding.UTF8.GetBytes("new source settings");
    var previous = Encoding.UTF8.GetBytes("previous settings snapshot");
    await File.WriteAllBytesAsync(sourcePath, source);
    await File.WriteAllBytesAsync(settingsStorePath, previous);
    var sourceHash = Convert.ToHexString(SHA256.HashData(source));
    using var cancellation = new CancellationTokenSource();
    var process = new DelegatingProcessExecutor((request, processToken) =>
    {
      Assert.NotEqual(settingsStorePath, request.Arguments[1]);
      Assert.True(File.Exists(request.Arguments[1]));
      Assert.EndsWith(".vssettings", request.Arguments[1], StringComparison.OrdinalIgnoreCase);
      Assert.True(processToken.CanBeCanceled);
      Assert.Equal(
          Wdem.Core.Processes.ProcessCancellationMode.LaunchOnly,
          request.CancellationMode);
      cancellation.Cancel();
      return Task.FromResult(new Wdem.Core.Processes.ProcessExecutionResult(true, 0, [], []));
    });
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(VisualStudioInstance()),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(sourceHash, settingsStorePath);
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);

    var applied = await provider.ApplyAsync(resource, plan, null, cancellation.Token);

    Assert.Equal(ApplyOutcome.Succeeded, applied.Outcome);
    Assert.True(applied.FinalizeAfterCancellation);
    Assert.Equal(0, Assert.Single(applied.StepResults).ProcessExitCode);
    Assert.Equal(source, await File.ReadAllBytesAsync(settingsStorePath));
    Assert.Equal([settingsStorePath], Directory.GetFiles(settingsRoot));
    Assert.Single(process.Requests);
  }

  [Fact]
  public async Task ApplyAsync_VsSettingsCancellationBeforeLaunchDoesNotStartDevenv()
  {
    var profiles = Path.Combine(_root, "profiles");
    var settingsRoot = Path.Combine(_root, "Visual Studio 18");
    var sourcePath = Path.Combine(profiles, "team.vssettings");
    var settingsStorePath = Path.Combine(settingsRoot, "team.vssettings");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("new source settings");
    await File.WriteAllBytesAsync(sourcePath, source);
    using var cancellation = new CancellationTokenSource();
    var process = new RecordingProcessExecutor();
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new FixedVisualStudioDiscovery(VisualStudioInstance()),
        process,
        new ComplianceEvaluator(),
        _ => settingsRoot);
    var resource = VisualStudioSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        settingsStorePath);
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);
    var progress = new InlineProgress(update =>
    {
      if (update.Stage == "Apply")
      {
        cancellation.Cancel();
      }
    });

    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await provider.ApplyAsync(resource, plan, progress, cancellation.Token));

    Assert.Empty(process.Requests);
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
  public async Task PlanAsync_VsSettingsBecomesExecutableAfterFreshConstrainedInstanceDiscovery()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("settings");
    await File.WriteAllBytesAsync(Path.Combine(profiles, "team.vssettings"), source);
    var resource = WithOptionalInstanceSelector(VisualStudioSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        "team.vssettings"));
    var discovery = new MutableVisualStudioDiscovery();
    var process = new RecordingProcessExecutor();
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        discovery,
        process,
        new ComplianceEvaluator(),
        _ => Path.Combine(_root, "Visual Studio 18"));

    var missing = await provider.DetectAsync(resource, CancellationToken.None);
    var unavailablePlan = await provider.PlanAsync(resource, missing, CancellationToken.None);
    discovery.Instances = [VisualStudioInstance() with { InstanceId = "17.real" }];
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);

    Assert.False(unavailablePlan.IsExecutable);
    Assert.Equal("17.real", detected.Evidence["visualStudioInstanceId"]);
    Assert.True(plan.IsExecutable);
    Assert.Single(plan.Steps);
  }

  [Fact]
  public async Task ApplyAsync_VsSettingsRejectsSelectedInstancePathChangeAfterPlanning()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("settings");
    await File.WriteAllBytesAsync(Path.Combine(profiles, "team.vssettings"), source);
    var resource = WithOptionalInstanceSelector(VisualStudioSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        "team.vssettings"));
    var selected = VisualStudioInstance() with { InstanceId = "17.real" };
    var discovery = new MutableVisualStudioDiscovery(selected);
    var process = new RecordingProcessExecutor();
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        discovery,
        process,
        new ComplianceEvaluator(),
        _ => Path.Combine(_root, "Visual Studio 18"));
    var detected = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, detected, CancellationToken.None);
    discovery.Instances =
    [
      selected with
      {
        InstallationPath = @"D:\MovedVS",
        ProductPath = @"D:\MovedVS\Common7\IDE\devenv.exe"
      }
    ];

    var applied = await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, applied.Outcome);
    Assert.Contains("changed after planning", applied.Error!.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(process.Requests);
  }

  [Fact]
  public async Task DetectAsync_VsSettingsAmbiguityReportsCandidateIds()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var source = Encoding.UTF8.GetBytes("settings");
    await File.WriteAllBytesAsync(Path.Combine(profiles, "team.vssettings"), source);
    var resource = WithOptionalInstanceSelector(VisualStudioSettingsResource(
        Convert.ToHexString(SHA256.HashData(source)),
        "team.vssettings"));
    var provider = new VisualStudioSettingsProvider(
        new ConfigurationSourceResolver(_root, profiles),
        new ConfigurationImporter(),
        new MutableVisualStudioDiscovery(
            VisualStudioInstance() with { InstanceId = "17.a" },
            VisualStudioInstance() with { InstanceId = "17.b" }),
        new RecordingProcessExecutor(),
        new ComplianceEvaluator(),
        _ => Path.Combine(_root, "Visual Studio 18"));

    var detected = await provider.DetectAsync(resource, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, detected.Outcome);
    Assert.Contains("17.a", detected.StructuredError!.Detail, StringComparison.Ordinal);
    Assert.Contains("17.b", detected.StructuredError.Detail, StringComparison.Ordinal);
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
    var visualStudioResources = new[]
    {
      "visual-studio",
      "resharper",
      "company-vs-extension",
      "visual-studio-settings"
    };
    Assert.All(visualStudioResources, id =>
        Assert.False(result.Profile.Resources[id].Parameters.ContainsKey("instanceId")));
    Assert.All(visualStudioResources, id =>
    {
      Assert.Equal("Microsoft.VisualStudio.Product.Community",
          result.Profile.Resources[id].Parameters["productId"]);
      Assert.Equal("Community", result.Profile.Resources[id].Parameters["edition"]);
      Assert.Equal("VisualStudio.18.Release",
          result.Profile.Resources[id].Parameters["channelId"]);
    });
    Assert.DoesNotContain(
        "wdem-vs-community",
        await File.ReadAllTextAsync(profilePath),
        StringComparison.OrdinalIgnoreCase);

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
    Assert.Equal(
        "Shared/vAny/GlobalSettingsStorage.DotSettings",
        reSharperResource.Parameters["destinationPath"]);
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
    Assert.True(File.Exists(Path.Combine(
        reSharperRoot,
        "Shared",
        "vAny",
        "GlobalSettingsStorage.DotSettings")));

    var settingsRoot = Path.Combine(_root, "current-user", "Visual Studio 18");
    var process = new RecordingProcessExecutor();
    var instance = VisualStudioInstance() with { InstanceId = "17.real" };
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

  private static ResourceDefinition ReSharperSettingsResource(
      string expectedHash,
      string destinationPath) => Resource(
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

  private static string ReSharperDestination(string jetBrainsRoot) => Path.Combine(
      jetBrainsRoot,
      "Shared",
      "vAny",
      "GlobalSettingsStorage.DotSettings");

  private static string ReSharperRelativeDestination() => Path.Combine(
      "Shared",
      "vAny",
      "GlobalSettingsStorage.DotSettings");

  private static ResourceDefinition WithOptionalInstanceSelector(ResourceDefinition resource)
  {
    var parameters = resource.Parameters
        .Where(pair => !string.Equals(pair.Key, "instanceId", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    parameters["productId"] = "Microsoft.VisualStudio.Product.Community";
    return resource with { Parameters = parameters };
  }

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

  private sealed class MutableVisualStudioDiscovery(
      params Wdem.Windows.VisualStudio.VisualStudioInstance[] instances)
      : Wdem.Windows.VisualStudio.IVisualStudioDiscovery
  {
    public IReadOnlyList<Wdem.Windows.VisualStudio.VisualStudioInstance> Instances { get; set; } =
        instances;

    public Task<IReadOnlyList<Wdem.Windows.VisualStudio.VisualStudioInstance>> DiscoverAsync(
        IReadOnlyList<string> requiredWorkloads,
        IReadOnlyList<string> requiredComponents,
        CancellationToken cancellationToken) => Task.FromResult(Instances);
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

  private sealed class DelegatingProcessExecutor(
      Func<Wdem.Core.Processes.ProcessExecutionRequest,
          CancellationToken,
          Task<Wdem.Core.Processes.ProcessExecutionResult>> handler)
      : Wdem.Core.Processes.IProcessExecutor
  {
    public List<Wdem.Core.Processes.ProcessExecutionRequest> Requests { get; } = [];

    public Task<Wdem.Core.Processes.ProcessExecutionResult> ExecuteAsync(
        Wdem.Core.Processes.ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      Requests.Add(request);
      return handler(request, cancellationToken);
    }
  }

  private sealed class InlineProgress(Action<ProviderProgress> handler) : IProgress<ProviderProgress>
  {
    public void Report(ProviderProgress value) => handler(value);
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
