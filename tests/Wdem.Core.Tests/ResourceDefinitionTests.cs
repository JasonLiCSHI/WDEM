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
  public void ProviderRegistry_RejectsInvalidProviderDescriptors()
  {
    Assert.Throws<ArgumentNullException>(() => new ResourceProviderRegistry(
        [new InvalidDescriptorProvider(capabilities: null)]));
    Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceProviderRegistry(
        [new InvalidDescriptorProvider(capabilities: new ProviderCapabilities
        {
          MaxConcurrentOperations = 0
        })]));
    Assert.Throws<ArgumentException>(() => new ResourceProviderRegistry(
        [new InvalidDescriptorProvider(resourceType: "")]));
    Assert.Throws<ArgumentException>(() => new ResourceProviderRegistry(
        [new InvalidDescriptorProvider(providerName: " ")]));
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

  [Fact]
  public void ProviderModels_SnapshotCallerOwnedCollections()
  {
    var legacyErrors = new List<string> { "first" };
    var structuredErrors = new List<StructuredError>
    {
      new(WdemErrorCode.ProviderError, "first", "first")
    };
    var versions = new List<SemanticVersion> { new(10, 0, 100) };
    var evidence = new Dictionary<string, string> { ["path"] = "original" };
    var steps = new List<PlanStep> { Step("git:install") };
    var stepResults = new List<ProviderStepResult>
    {
      new() { StepId = "git:install", Action = PlanAction.Install }
    };
    var validation = new ProviderValidationResult
    {
      Errors = legacyErrors,
      StructuredErrors = structuredErrors
    };
    var state = new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Succeeded,
      InstalledVersions = versions,
      Evidence = evidence
    };
    var plan = Plan(steps, structuredErrors);
    var apply = new ResourceApplyResult
    {
      ResourceId = "git",
      Outcome = ApplyOutcome.Succeeded,
      StepResults = stepResults,
      Diagnostics = structuredErrors
    };

    legacyErrors[0] = "mutated";
    structuredErrors.Clear();
    versions[0] = new SemanticVersion(1, 0, 0);
    evidence["path"] = "mutated";
    steps.Clear();
    stepResults.Clear();

    Assert.Equal("first", Assert.Single(validation.Errors));
    Assert.Single(validation.StructuredErrors);
    Assert.Equal(new SemanticVersion(10, 0, 100), Assert.Single(state.InstalledVersions));
    Assert.Equal("original", state.Evidence["path"]);
    Assert.Single(plan.Steps);
    Assert.Single(plan.StructuredErrors);
    Assert.Single(apply.StepResults);
    Assert.Single(apply.Diagnostics);
  }

  [Fact]
  public void ProviderModelCollections_CannotBeMutatedThroughConcreteInterfaces()
  {
    var error = new StructuredError(WdemErrorCode.ProviderError, "error", "error");
    var validation = new ProviderValidationResult
    {
      Errors = ["error"],
      StructuredErrors = [error]
    };
    var state = new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Succeeded,
      InstalledVersions = [new SemanticVersion(10, 0, 100)],
      Evidence = new Dictionary<string, string> { ["path"] = "git.exe" }
    };
    var plan = Plan([Step("git:install")], [error]);
    var apply = new ResourceApplyResult
    {
      ResourceId = "git",
      Outcome = ApplyOutcome.Succeeded,
      StepResults = [new ProviderStepResult
      {
        StepId = "git:install",
        Action = PlanAction.Install
      }],
      Diagnostics = [error]
    };

    AssertReadOnlyList(validation.Errors, "injected");
    AssertReadOnlyList(validation.StructuredErrors, error);
    AssertReadOnlyList(state.InstalledVersions, new SemanticVersion(1, 0, 0));
    AssertReadOnlyDictionary(state.Evidence, "injected", "value");
    AssertReadOnlyList(plan.Steps, Step("injected"));
    AssertReadOnlyList(plan.StructuredErrors, error);
    AssertReadOnlyList(apply.StepResults, new ProviderStepResult
    {
      StepId = "injected",
      Action = PlanAction.None
    });
    AssertReadOnlyList(apply.Diagnostics, error);
  }

  [Fact]
  public void ProviderModels_RejectNullCollectionsAndElements()
  {
    var error = new StructuredError(WdemErrorCode.ProviderError, "error", "error");

    Assert.Throws<ArgumentNullException>(() => new ProviderValidationResult { Errors = null! });
    Assert.Throws<ArgumentException>(() => new ProviderValidationResult { Errors = [null!] });
    Assert.Throws<ArgumentNullException>(() => new ProviderValidationResult
    {
      StructuredErrors = null!
    });
    Assert.Throws<ArgumentException>(() => new ProviderValidationResult
    {
      StructuredErrors = [null!]
    });
    Assert.Throws<ArgumentNullException>(() => new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Succeeded,
      InstalledVersions = null!
    });
    Assert.Throws<ArgumentNullException>(() => Plan(null!, [error]));
    Assert.Throws<ArgumentException>(() => Plan([null!], [error]));
    Assert.Throws<ArgumentNullException>(() => new ResourceApplyResult
    {
      ResourceId = "git",
      Outcome = ApplyOutcome.Succeeded,
      StepResults = null!
    });
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

  [Fact]
  public void ApprovedFingerprint_CoversOriginalDefinitionAndExecutableSteps()
  {
    var resource = new ResourceDefinition
    {
      Id = "admin-resource",
      Type = "test",
      Provider = "test",
      Parameters = new Dictionary<string, string?>
      {
        ["password"] = "first-secret"
      }
    };
    var plan = new ResourcePlan
    {
      ResourceId = resource.Id,
      ResourceType = resource.Type,
      ProviderName = resource.Provider,
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
      Compliance = ComplianceStatus.Missing,
      IsExecutable = true,
      Steps =
      [
        new PlanStep
        {
          Id = "admin-resource:apply",
          Description = "Apply resource.",
          Action = PlanAction.Configure,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
          RestartPolicy = RestartPolicy.NoRestart
        }
      ]
    };
    var approved = ApprovedResourceFingerprint.Create(resource, plan);
    var changedDefinition = resource with
    {
      Parameters = new Dictionary<string, string?>
      {
        ["password"] = "second-secret"
      }
    };
    var changedStep = plan with
    {
      Steps = [plan.Steps.Single() with { Action = PlanAction.Upgrade }]
    };

    Assert.NotEqual(approved, ApprovedResourceFingerprint.Create(changedDefinition, plan));
    Assert.NotEqual(approved, ApprovedResourceFingerprint.Create(resource, changedStep));
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

  private sealed class InvalidDescriptorProvider(
      string? resourceType = "package",
      string? providerName = "invalid",
      ProviderCapabilities? capabilities = null) : IResourceProvider
  {
    public string ResourceType { get; } = resourceType!;
    public string ProviderName { get; } = providerName!;
    public ProviderCapabilities Capabilities { get; } = capabilities!;

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  private static PlanStep Step(string id) => new()
  {
    Id = id,
    Description = id,
    Action = PlanAction.Install,
    PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
    RestartPolicy = RestartPolicy.NoRestart
  };

  private static ResourcePlan Plan(
      IReadOnlyList<PlanStep> steps,
      IReadOnlyList<StructuredError> errors) => new()
      {
        ResourceId = "git",
        ResourceType = "package",
        ProviderName = "winget",
        DesiredStateFingerprint = "fingerprint",
        Compliance = ComplianceStatus.Missing,
        IsExecutable = true,
        Steps = steps,
        StructuredErrors = errors
      };

  private static void AssertReadOnlyList<T>(IReadOnlyList<T> values, T injected)
  {
    var list = Assert.IsAssignableFrom<IList<T>>(values);
    Assert.Throws<NotSupportedException>(() => list.Add(injected));
    Assert.Throws<NotSupportedException>(() => list[0] = injected);
  }

  private static void AssertReadOnlyDictionary<TKey, TValue>(
      IReadOnlyDictionary<TKey, TValue> values,
      TKey key,
      TValue value) where TKey : notnull
  {
    var dictionary = Assert.IsAssignableFrom<IDictionary<TKey, TValue>>(values);
    Assert.Throws<NotSupportedException>(() => dictionary.Add(key, value));
  }
}
