using Moq;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using WinHome.Interfaces;
using WinHome.Models;
using WinHome.Providers;

namespace WinHome.Tests;

public sealed class LegacyPackageManagerProviderAdapterTests
{
  private readonly Mock<ICancellablePackageManager> _packageManager = new();

  [Fact]
  public async Task ValidateAsync_RejectsMissingPackageId()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource(parameters: new Dictionary<string, string?>());

    var result = await adapter.ValidateAsync(resource, CancellationToken.None);

    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, error => error.Contains(
        LegacyPackageManagerProviderAdapter.PackageIdParameter,
        StringComparison.Ordinal));
  }

  [Fact]
  public async Task ValidateAsync_RejectsUnsupportedLegacyCapabilities()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource() with
    {
      VersionConstraint = ">=2.50 <3.0",
      PreferredVersion = "2.51.0",
      Parameters = new Dictionary<string, string?>
      {
        [LegacyPackageManagerProviderAdapter.PackageIdParameter] = "Git.Git",
        [LegacyPackageManagerProviderAdapter.SourceParameter] = "company",
        [LegacyPackageManagerProviderAdapter.InstallerParametersParameter] = "--unsafe-raw-argument"
      }
    };

    var result = await adapter.ValidateAsync(resource, CancellationToken.None);

    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, error => error.Contains("versions", StringComparison.Ordinal));
    Assert.Contains(result.Errors, error => error.Contains("source", StringComparison.Ordinal));
    Assert.Contains(result.Errors, error => error.Contains("installer parameters", StringComparison.Ordinal));
  }

  [Fact]
  public async Task ValidateAsync_RejectsUnknownParameters()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource() with
    {
      Parameters = new Dictionary<string, string?>
      {
        [LegacyPackageManagerProviderAdapter.PackageIdParameter] = "Git.Git",
        ["unexpected"] = "value"
      }
    };

    var result = await adapter.ValidateAsync(resource, CancellationToken.None);

    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, error => error.Contains("unexpected", StringComparison.Ordinal));
  }

  [Fact]
  public async Task MissingPackage_CanBePlannedAppliedAndVerified()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource();
    _packageManager.Setup(manager => manager.IsAvailable()).Returns(true);
    _packageManager
        .SetupSequence(manager => manager.IsInstalled("Git.Git"))
        .Returns(false)
        .Returns(true);

    var detected = await adapter.DetectAsync(resource, CancellationToken.None);
    var plan = await adapter.PlanAsync(resource, detected, CancellationToken.None);
    var applied = await adapter.ApplyAsync(resource, plan, null, CancellationToken.None);
    var verified = await adapter.VerifyAsync(resource, CancellationToken.None);

    Assert.Equal(ComplianceStatus.Missing, plan.Compliance);
    Assert.True(plan.IsExecutable);
    Assert.Single(plan.Steps);
    Assert.Equal(ApplyOutcome.Succeeded, applied.Outcome);
    Assert.Equal(ComplianceStatus.Satisfied, verified.Compliance);
    _packageManager.Verify(manager => manager.InstallAsync(
        It.Is<AppConfig>(app =>
            app.Id == "Git.Git" &&
            app.Manager == "winget" &&
            app.ResourceId == "git"),
        It.IsAny<IProgress<string>?>(),
        CancellationToken.None), Times.Once);
  }

  [Fact]
  public async Task SatisfiedPackage_ProducesNotRequiredPlan()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource();
    _packageManager.Setup(manager => manager.IsAvailable()).Returns(true);
    _packageManager.Setup(manager => manager.IsInstalled("Git.Git")).Returns(true);

    var detected = await adapter.DetectAsync(resource, CancellationToken.None);
    var plan = await adapter.PlanAsync(resource, detected, CancellationToken.None);
    var result = await adapter.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ComplianceStatus.Satisfied, plan.Compliance);
    Assert.False(plan.RequiresApply);
    Assert.Equal(ApplyOutcome.NotRequired, result.Outcome);
    _packageManager.Verify(
        manager => manager.InstallAsync(
            It.IsAny<AppConfig>(),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>()),
        Times.Never);
  }

  [Fact]
  public async Task UnavailableManager_BlocksExecutionPlan()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource();
    _packageManager.Setup(manager => manager.IsAvailable()).Returns(false);

    var detected = await adapter.DetectAsync(resource, CancellationToken.None);
    var plan = await adapter.PlanAsync(resource, detected, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, detected.Outcome);
    Assert.Equal(ComplianceStatus.DetectionFailed, plan.Compliance);
    Assert.False(plan.IsExecutable);
  }

  [Fact]
  public async Task ApplyAsync_RejectsPlanForDifferentResource()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource();
    var plan = new ResourcePlan
    {
      ResourceId = "dotnet-sdk",
      ResourceType = "package",
      ProviderName = "winget",
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
      Compliance = ComplianceStatus.Missing,
      IsExecutable = true,
      Steps =
      [
        new PlanStep
        {
          Id = "dotnet-sdk:install",
          Description = "Install .NET SDK.",
          Action = PlanAction.Install,
          PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
          RestartPolicy = RestartPolicy.NoRestart
        }
      ]
    };

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        await adapter.ApplyAsync(resource, plan, null, CancellationToken.None));
  }

  [Fact]
  public async Task ApplyAsync_RejectsChangedPackageAfterPlanApproval()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource();
    var detected = new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = false
    };
    var plan = await adapter.PlanAsync(resource, detected, CancellationToken.None);
    var changedResource = resource with
    {
      Parameters = new Dictionary<string, string?>
      {
        [LegacyPackageManagerProviderAdapter.PackageIdParameter] = "Microsoft.VisualStudioCode"
      }
    };

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        await adapter.ApplyAsync(changedResource, plan, null, CancellationToken.None));
    _packageManager.Verify(
        manager => manager.InstallAsync(
            It.IsAny<AppConfig>(),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>()),
        Times.Never);
  }

  [Fact]
  public async Task ApplyAsync_ReturnsCancelledWhenInstallationIsCancelled()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource();
    var detected = new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = false
    };
    var plan = await adapter.PlanAsync(resource, detected, CancellationToken.None);
    using var cancellation = new CancellationTokenSource();
    _packageManager
        .Setup(manager => manager.InstallAsync(
            It.IsAny<AppConfig>(),
            It.IsAny<IProgress<string>?>(),
            cancellation.Token))
        .Returns(async () =>
        {
          cancellation.Cancel();
          await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        });

    var result = await adapter.ApplyAsync(resource, plan, null, cancellation.Token);

    Assert.Equal(ApplyOutcome.Cancelled, result.Outcome);
  }

  private LegacyPackageManagerProviderAdapter CreateAdapter() =>
      new("winget", _packageManager.Object);

  private static ResourceDefinition CreateResource(
      IReadOnlyDictionary<string, string?>? parameters = null) =>
      new()
      {
        Id = "git",
        Type = "package",
        Provider = "winget",
        DisplayName = "Git",
        Parameters = parameters ?? new Dictionary<string, string?>
        {
          [LegacyPackageManagerProviderAdapter.PackageIdParameter] = "Git.Git"
        }
      };
}
