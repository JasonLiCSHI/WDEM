using System.Text.Json;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Versions;
using Xunit;

namespace Wdem.Core.Tests;

public sealed class ResourceDefinitionTests
{
  [Fact]
  public void Defaults_AreBehaviorSafe()
  {
    var resource = new ResourceDefinition
    {
      Id = "git",
      Type = "package",
      Provider = "winget"
    };

    Assert.Empty(resource.Dependencies);
    Assert.Empty(resource.Parameters);
    Assert.Equal(PrivilegeRequirement.CurrentUser, resource.PrivilegeRequirement);
    Assert.Equal(RestartPolicy.NoRestart, resource.RestartPolicy);
  }

  [Fact]
  public void ProviderRegistry_ResolvesByResourceTypeAndProviderName()
  {
    var winget = new StubProvider("package", "winget");
    var registry = new ResourceProviderRegistry([winget]);

    var resolved = registry.GetRequired("PACKAGE", "WINGET");

    Assert.Same(winget, resolved);
  }

  [Fact]
  public void ProviderRegistry_RejectsDuplicateProviderKeys()
  {
    var providers = new[]
    {
      new StubProvider("package", "winget"),
      new StubProvider("PACKAGE", "WINGET")
    };

    Assert.Throws<InvalidOperationException>(() => new ResourceProviderRegistry(providers));
  }

  [Fact]
  public void ProviderRegistry_RejectsNullProvidersAndInvalidLookups()
  {
    Assert.Throws<ArgumentNullException>(() => new ResourceProviderRegistry(null!));
    Assert.Throws<ArgumentNullException>(() => new ResourceProviderRegistry([null!]));

    var registry = new ResourceProviderRegistry([new StubProvider("package", "winget")]);
    Assert.Throws<ArgumentException>(() => registry.TryGet(" ", "winget", out _));
    Assert.Throws<ArgumentException>(() => registry.GetRequired("package", ""));
  }

  [Fact]
  public void ProviderValidation_IsInvalidForEitherDiagnosticRepresentation()
  {
    var structuredError = new StructuredError(
        WdemErrorCode.ProviderError,
        "Invalid provider input",
        "The provider rejected its input.");

    Assert.False(ProviderValidationResult.Invalid("legacy error").IsValid);
    Assert.False(new ProviderValidationResult
    {
      StructuredErrors = [structuredError]
    }.IsValid);
    Assert.True(ProviderValidationResult.Valid.IsValid);
  }

  [Fact]
  public void ProviderModels_ExposeSafeMetadataDefaults()
  {
    var state = new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Succeeded
    };
    var apply = new ResourceApplyResult
    {
      ResourceId = "git",
      Outcome = ApplyOutcome.NotRequired
    };
    var plan = new ResourcePlan
    {
      ResourceId = "git",
      ResourceType = "package",
      ProviderName = "winget",
      DesiredStateFingerprint = "fingerprint",
      Compliance = ComplianceStatus.Satisfied,
      IsExecutable = true
    };

    Assert.Empty(state.InstalledVersions);
    Assert.Null(state.ConfigurationHash);
    Assert.NotEqual(default, state.DetectedAtUtc);
    Assert.Null(state.StructuredError);
    Assert.Empty(apply.StepResults);
    Assert.Null(apply.Error);
    Assert.Empty(plan.StructuredErrors);
  }

  [Theory]
  [InlineData(-0.1, 0)]
  [InlineData(0.25, 0.25)]
  [InlineData(1.1, 1)]
  [InlineData(double.NaN, 0)]
  [InlineData(double.NegativeInfinity, 0)]
  [InlineData(double.PositiveInfinity, 1)]
  public void ProviderProgress_NormalizesPercent(double supplied, double expected)
  {
    var progress = new ProviderProgress("Apply", supplied, "working");

    Assert.Equal(expected, progress.Percent);
  }

  [Fact]
  public void ProviderProgress_SanitizesSecretsAndRoundTripsStructuredFields()
  {
    var progress = new ProviderProgress(
        "Apply",
        0.5,
        "Downloading token=super-secret",
        "git:install",
        ProviderLogLevel.Warning);

    var restored = JsonSerializer.Deserialize<ProviderProgress>(JsonSerializer.Serialize(progress));

    Assert.NotNull(restored);
    Assert.DoesNotContain("super-secret", restored.Message, StringComparison.Ordinal);
    Assert.Contains("[REDACTED]", restored.Message, StringComparison.Ordinal);
    Assert.Equal("git:install", restored.StepId);
    Assert.Equal(ProviderLogLevel.Warning, restored.LogLevel);
  }

  [Fact]
  public void ProviderStepResult_CarriesAuditableOperationMetadata()
  {
    var result = new ProviderStepResult
    {
      StepId = "git:install",
      Action = PlanAction.Install,
      Progress = 1,
      ProcessExitCode = 0,
      Message = "Installed"
    };

    Assert.Equal("git:install", result.StepId);
    Assert.Equal(PlanAction.Install, result.Action);
    Assert.Equal(0, result.ProcessExitCode);
  }

  [Fact]
  public void Fingerprint_IsStableAcrossDictionaryInsertionOrder()
  {
    var first = new ResourceDefinition
    {
      Id = "git",
      Type = "package",
      Provider = "winget",
      Parameters = new Dictionary<string, string?>
      {
        ["packageId"] = "Git.Git",
        ["source"] = "winget"
      }
    };
    var second = first with
    {
      Parameters = new Dictionary<string, string?>
      {
        ["source"] = "winget",
        ["packageId"] = "Git.Git"
      }
    };

    Assert.Equal(
        ResourceDefinitionFingerprint.Create(first),
        ResourceDefinitionFingerprint.Create(second));
  }

  private sealed class StubProvider(string resourceType, string providerName) : IResourceProvider
  {
    public string ResourceType { get; } = resourceType;
    public string ProviderName { get; } = providerName;
    public ProviderCapabilities Capabilities { get; } = new();

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
  }
}
