using Moq;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.LegacySource.Interfaces;
using Wdem.LegacySource.Models;
using Wdem.LegacySource.Providers;

namespace Wdem.LegacySource.Tests;

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
    Assert.Contains(result.StructuredErrors, error =>
        error.Code == WdemErrorCode.ProviderError && error.ResourceId == resource.Id);
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
    Assert.Equal(result.Errors.Count, result.StructuredErrors.Count);
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
    var step = Assert.Single(applied.StepResults);
    Assert.Equal("git:install", step.StepId);
    Assert.Equal(PlanAction.Install, step.Action);
    Assert.Equal(1, step.Progress);
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
    Assert.Equal(WdemErrorCode.DetectionError, detected.StructuredError!.Code);
    Assert.NotEqual(default, detected.DetectedAtUtc);
    Assert.Equal(ComplianceStatus.DetectionFailed, plan.Compliance);
    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.DetectionError, Assert.Single(plan.StructuredErrors).Code);
  }

  [Fact]
  public void Capabilities_AccuratelyDescribeLegacyOperationSupport()
  {
    var adapter = CreateAdapter();
    var sourceAdapter = new LegacyPackageManagerProviderAdapter(
        "winget",
        _packageManager.Object,
        supportsSource: true);

    Assert.False(adapter.Capabilities.SupportsSource);
    Assert.False(adapter.Capabilities.SupportsVersionConstraints);
    Assert.False(adapter.Capabilities.SupportsInstallerParameters);
    Assert.True(adapter.Capabilities.SupportsInProgressCancellation);
    Assert.True(sourceAdapter.Capabilities.SupportsSource);
  }

  [Fact]
  public async Task DetectAsync_ReturnsStructuredFailureWhenLegacyManagerThrows()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource();
    _packageManager.Setup(manager => manager.IsAvailable()).Throws(
        new InvalidOperationException("authorization=super-secret unavailable"));

    var detected = await adapter.DetectAsync(resource, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, detected.Outcome);
    Assert.Equal(WdemErrorCode.DetectionError, detected.StructuredError!.Code);
    Assert.DoesNotContain("super-secret", detected.StructuredError.Detail, StringComparison.Ordinal);
    Assert.Equal(typeof(InvalidOperationException).FullName,
        detected.StructuredError.UnderlyingExceptionType);
  }

  [Fact]
  public async Task DetectAsync_PreCancelledToken_PropagatesCancellation()
  {
    var adapter = CreateAdapter();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        await adapter.DetectAsync(CreateResource(), cancellation.Token));
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
        .Returns<AppConfig, IProgress<string>?, CancellationToken>(async (_, progress, _) =>
        {
          progress?.Report("halfway");
          cancellation.Cancel();
          await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        });

    var result = await adapter.ApplyAsync(resource, plan, null, cancellation.Token);

    Assert.Equal(ApplyOutcome.Cancelled, result.Outcome);
    Assert.Equal(WdemErrorCode.CancellationError, result.Error!.Code);
    var step = Assert.Single(result.StepResults);
    Assert.Equal("git:install", step.StepId);
    Assert.Equal(0.5, step.Progress);
    Assert.Equal(WdemErrorCode.CancellationError, step.Error!.Code);
  }

  [Fact]
  public async Task ApplyAsync_ConvertsLegacyInstallationExceptionToStructuredFailure()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource();
    var plan = await adapter.PlanAsync(resource, new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = false
    }, CancellationToken.None);
    _packageManager
        .Setup(manager => manager.InstallAsync(
            It.IsAny<AppConfig>(),
            It.IsAny<IProgress<string>?>(),
            CancellationToken.None))
        .Returns<AppConfig, IProgress<string>?, CancellationToken>((_, progress, _) =>
        {
          progress?.Report("halfway");
          throw new InvalidOperationException("api_key=super-secret rejected");
        });

    var result = await adapter.ApplyAsync(resource, plan, null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.InstallationError, result.Error!.Code);
    Assert.DoesNotContain("super-secret", result.Error.Detail, StringComparison.Ordinal);
    var step = Assert.Single(result.StepResults);
    Assert.Equal(0.5, step.Progress);
    Assert.Equal(WdemErrorCode.InstallationError, step.Error!.Code);
  }

  [Fact]
  public async Task ApplyAsync_ObservesCancellationRequestedDuringLegacyCall()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource();
    var plan = await adapter.PlanAsync(resource, new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = false
    }, CancellationToken.None);
    using var cancellation = new CancellationTokenSource();
    _packageManager
        .Setup(manager => manager.InstallAsync(
            It.IsAny<AppConfig>(),
            It.IsAny<IProgress<string>?>(),
            cancellation.Token))
        .Returns(() =>
        {
          cancellation.Cancel();
          return Task.CompletedTask;
        });

    var result = await adapter.ApplyAsync(resource, plan, null, cancellation.Token);

    Assert.Equal(ApplyOutcome.Cancelled, result.Outcome);
    Assert.Equal(WdemErrorCode.CancellationError, result.Error!.Code);
  }

  [Fact]
  public async Task ApplyAsync_ProgressSanitizesLegacyLogMessages()
  {
    var adapter = CreateAdapter();
    var resource = CreateResource();
    var plan = await adapter.PlanAsync(resource, new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = false
    }, CancellationToken.None);
    _packageManager
        .Setup(manager => manager.InstallAsync(
            It.IsAny<AppConfig>(),
            It.IsAny<IProgress<string>?>(),
            CancellationToken.None))
        .Returns<AppConfig, IProgress<string>?, CancellationToken>((_, progress, _) =>
        {
          progress?.Report("token=super-secret");
          return Task.CompletedTask;
        });
    var reports = new List<ProviderProgress>();
    var progress = new ImmediateProgress<ProviderProgress>(reports.Add);

    await adapter.ApplyAsync(resource, plan, progress, CancellationToken.None);

    Assert.All(reports, report =>
        Assert.DoesNotContain("super-secret", report.Message, StringComparison.Ordinal));
    Assert.Contains(reports, report => report.StepId == "git:install");
  }

  [Theory]
  [InlineData(ProgressFailurePoint.BeforeInstall)]
  [InlineData(ProgressFailurePoint.DuringInstall)]
  [InlineData(ProgressFailurePoint.AfterInstall)]
  public async Task ApplyAsync_ObserverFailure_DoesNotChangeSuccessfulOperation(
      ProgressFailurePoint failurePoint)
  {
    var adapter = CreateAdapter();
    var resource = CreateResource();
    var plan = await adapter.PlanAsync(resource, new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = false
    }, CancellationToken.None);
    _packageManager
        .Setup(manager => manager.InstallAsync(
            It.IsAny<AppConfig>(),
            It.IsAny<IProgress<string>?>(),
            CancellationToken.None))
        .Returns<AppConfig, IProgress<string>?, CancellationToken>((_, progress, _) =>
        {
          progress?.Report("halfway");
          return Task.CompletedTask;
        });
    var observer = new ImmediateProgress<ProviderProgress>(report =>
    {
      var shouldThrow = failurePoint switch
      {
        ProgressFailurePoint.BeforeInstall => report.Percent == 0,
        ProgressFailurePoint.DuringInstall => report.Percent == 0.5,
        ProgressFailurePoint.AfterInstall => report.Percent == 1,
        _ => false
      };
      if (shouldThrow)
      {
        throw new InvalidOperationException("token=super-secret observer failed");
      }
    });

    var result = await adapter.ApplyAsync(resource, plan, observer, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Null(result.Error);
    var diagnostic = Assert.Single(result.Diagnostics);
    Assert.Equal(WdemErrorCode.ProviderError, diagnostic.Code);
    Assert.DoesNotContain("super-secret", diagnostic.Detail, StringComparison.Ordinal);
    Assert.Equal(1, Assert.Single(result.StepResults).Progress);
    _packageManager.Verify(manager => manager.InstallAsync(
        It.IsAny<AppConfig>(),
        It.IsAny<IProgress<string>?>(),
        CancellationToken.None), Times.Once);
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

  private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
  {
    public void Report(T value) => report(value);
  }

  public enum ProgressFailurePoint
  {
    BeforeInstall,
    DuringInstall,
    AfterInstall
  }
}
