using Wdem.Cli;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Windows.Persistence;
using Xunit;

namespace Wdem.Windows.Tests.Cli;

public sealed class WdemCommandHandlerIntegrationTests : IDisposable
{
  private readonly string _directory = Path.Combine(
      Path.GetTempPath(), $"wdem-cli-integration-{Guid.NewGuid():N}");

  [Fact]
  public async Task ApplyAsync_RealDetectionFailureReturnsExecutionExitCode()
  {
    var provider = new DetectionFailureProvider();
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var handler = new WdemCommandHandler(
        service,
        store,
        new StringWriter(),
        new StringWriter(),
        redactor,
        sink);

    var exitCode = await handler.ApplyAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        json: true,
        CancellationToken.None);

    Assert.Equal(3, exitCode);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task InspectAsync_RealDetectionFailureReturnsExecutionExitCode()
  {
    var provider = new DetectionFailureProvider();
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var handler = new WdemCommandHandler(
        service,
        store,
        new StringWriter(),
        new StringWriter(),
        redactor,
        sink);

    var exitCode = await handler.InspectAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        json: true,
        CancellationToken.None);

    var run = Assert.Single(await store.ListAsync(CancellationToken.None));
    Assert.Equal(3, exitCode);
    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task InspectAsync_RealDependencyValidationFailureReturnsProfileExitCode()
  {
    var provider = new DetectionFailureProvider();
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var profile = Profile() with
    {
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = Profile().Resources["git"] with { Dependencies = ["missing"] }
      }
    };
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(profile),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var handler = new WdemCommandHandler(
        service,
        store,
        new StringWriter(),
        new StringWriter(),
        redactor,
        sink);

    var exitCode = await handler.InspectAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        json: true,
        CancellationToken.None);

    var run = Assert.Single(await store.ListAsync(CancellationToken.None));
    Assert.Equal(2, exitCode);
    Assert.Equal(WdemErrorCode.DependencyError, Assert.Single(run.Plan!.Errors).Code);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_InvalidMaterializedProfileRedactsDiagnosticsBeforePersistenceAndOutput()
  {
    const string secret = "invalid-profile-hunter2";
    var profile = Profile() with
    {
      Resources = Profile().Resources.ToDictionary(
          pair => pair.Key,
          pair => pair.Value with
          {
            Parameters = new Dictionary<string, string?>
            {
              ["access_token"] = secret
            }
          },
          StringComparer.OrdinalIgnoreCase)
    };
    var diagnostic = new StructuredError(
        WdemErrorCode.ProviderError,
        $"Invalid provider value {secret}",
        $"Provider detail {secret}")
    {
      UnderlyingException = new InvalidOperationException(secret)
    };
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
    var registry = new ResourceProviderRegistry([new DetectionFailureProvider()]);
    var compliance = new ComplianceEvaluator();
    var service = new EnvironmentRunService(
        new FixedProfileCatalog(new ProfileLoadResult
        {
          Profile = profile,
          SourcePath = Path.GetFullPath("developer.yaml"),
          Errors = [diagnostic]
        }),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider: null,
        sink,
        redactor);
    var output = new StringWriter();
    var handler = new WdemCommandHandler(
        service,
        store,
        output,
        new StringWriter(),
        redactor,
        sink);

    var exitCode = await handler.ApplyAsync(
        new RunRequest(
            Path.GetFullPath("developer.yaml"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        json: true,
        CancellationToken.None);

    var run = Assert.Single(await store.ListAsync(CancellationToken.None));
    Assert.Equal(3, exitCode);
    Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
    Assert.DoesNotContain(secret, await File.ReadAllTextAsync(store.LogPath(run.RunId)),
        StringComparison.Ordinal);
    Assert.DoesNotContain(secret, await File.ReadAllTextAsync(store.SnapshotPath(run.RunId)),
        StringComparison.Ordinal);
  }

  public void Dispose()
  {
    if (Directory.Exists(_directory))
    {
      Directory.Delete(_directory, recursive: true);
    }
  }

  private static DeveloperProfile Profile() => new()
  {
    Id = "developer",
    Version = "1.0.0",
    DisplayName = "Developer",
    Description = "Developer workstation",
    RequiredResources = [new ProfileResourceReference { Id = "git" }],
    Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = new()
      {
        Id = "git",
        Type = "package",
        Provider = "failing"
      }
    }
  };

  private sealed class FixedProfileCatalog : IProfileCatalog
  {
    private readonly ProfileLoadResult _result;

    public FixedProfileCatalog(DeveloperProfile profile)
        : this(new ProfileLoadResult
        {
          Profile = profile,
          SourcePath = Path.GetFullPath("developer.yaml")
        })
    {
    }

    public FixedProfileCatalog(ProfileLoadResult result)
    {
      _result = result;
    }

    public Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        LoadFileAsync(id, cancellationToken);

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(_result with { SourcePath = Path.GetFullPath(path) });
    }

    public Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProfileLoadResult>>([]);
  }

  private sealed class DetectionFailureProvider : IResourceProvider
  {
    public string ResourceType => "package";
    public string ProviderName => "failing";
    public ProviderCapabilities Capabilities { get; } = new();
    public int ApplyCalls { get; private set; }

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Failed,
          Exists = false,
          StructuredError = new StructuredError(
              WdemErrorCode.DetectionError,
              "Detection failed.",
              "The provider could not inspect the resource.")
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
          Compliance = ComplianceStatus.DetectionFailed,
          IsExecutable = false
        });

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      ApplyCalls++;
      throw new InvalidOperationException("A non-executable plan must not be applied.");
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A non-executable plan must not be verified.");
  }
}
