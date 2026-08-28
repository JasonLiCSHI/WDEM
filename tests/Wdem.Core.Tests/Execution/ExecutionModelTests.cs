using System.Text.Json;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Xunit;

namespace Wdem.Core.Tests.Execution;

public sealed class ExecutionModelTests
{
  [Fact]
  public void LifecycleEnums_ExposeTheClosedVocabulary()
  {
    Assert.Equal(
        ["Pending", "Ready", "Blocked", "Running", "Completed"],
        Enum.GetNames<ExecutionState>());
    Assert.Equal(
        ["Succeeded", "Failed", "Cancelled", "NotRequired", "Skipped"],
        Enum.GetNames<ExecutionOutcome>());
    Assert.Equal(["Inspect", "Apply"], Enum.GetNames<RunMode>());
    Assert.Equal(
        [
          "ProfileError", "DependencyError", "DetectionError", "VersionError",
          "ConfigurationError", "DownloadError", "InstallationError", "VerificationError",
          "PermissionError", "ProviderError", "CancellationError", "RestartRequired"
        ],
        Enum.GetNames<WdemErrorCode>());
  }

  [Fact]
  public void ProviderModels_RetainCompatibilityAndExposeConcurrencyMetadata()
  {
    var capabilities = new ProviderCapabilities();
    var existingProgressCall = new ProviderProgress("Apply", 0.5, "Installing");
    var detailedProgress = new ProviderProgress(
        "Apply",
        0.75,
        "Configuring",
        "git:configure",
        ProviderLogLevel.Warning);

    Assert.Equal(1, capabilities.MaxConcurrentOperations);
    Assert.Null(capabilities.ConcurrencyGroup);
    Assert.Equal(2, (int)PlanAction.Configure);
    Assert.Equal(3, (int)PlanAction.Repair);
    Assert.Equal(4, (int)PlanAction.Upgrade);
    Assert.Equal(PlanAction.Upgrade, Enum.Parse<PlanAction>("Upgrade"));
    Assert.Null(existingProgressCall.StepId);
    Assert.Equal(ProviderLogLevel.Info, existingProgressCall.LogLevel);
    Assert.Equal("git:configure", detailedProgress.StepId);
    Assert.Equal(ProviderLogLevel.Warning, detailedProgress.LogLevel);

    var (stage, percent, message) = existingProgressCall;
    Assert.Equal("Apply", stage);
    Assert.Equal(0.5, percent);
    Assert.Equal("Installing", message);
  }

  [Fact]
  public void ProviderProgress_JsonRoundTripsWhileRetainingThreeFieldApi()
  {
    var original = new ProviderProgress(
        "Apply",
        0.75,
        "Configuring",
        "git:configure",
        ProviderLogLevel.Warning);

    var restored = JsonSerializer.Deserialize<ProviderProgress>(JsonSerializer.Serialize(original));

    Assert.NotNull(restored);
    Assert.Equal(original, restored);
    var (stage, percent, message) = restored;
    Assert.Equal("Apply", stage);
    Assert.Equal(0.75, percent);
    Assert.Equal("Configuring", message);
  }
}
