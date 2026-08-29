using System.Text.Json;
using System.Text.Json.Serialization;
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
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

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
    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
    Assert.Equal(ExecutionOutcome.Skipped, run.ResourceResults["git"].Outcome);
    Assert.Equal(
        WdemErrorCode.DetectionError,
        Assert.Single(run.Plan!.Errors).Code);
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

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ResumeAsync_ExistingSuccessfulReplacementReplaysRedactedRunEvents(bool json)
  {
    const string secret = "resume-replay-secret";
    var provider = new SuccessfulProvider(secret);
    var profile = Profile() with
    {
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = Profile().Resources["git"] with
        {
          Provider = provider.ProviderName,
          Parameters = new Dictionary<string, string?> { ["access_token"] = secret }
        }
      }
    };
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var redactor = new LogRedactor();
    var sink = new RunEventHub();
    var store = new JsonExecutionRunStore(new WdemDataPaths(_directory), redactor);
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
    var prior = InterruptedRun();
    await store.CreateAsync(prior, CancellationToken.None);
    var replacement = await service.ApplyAsync(
        new RunRequest(
            prior.ProfileSourcePath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        CancellationToken.None);
    replacement = await store.SaveAsync(
        replacement with { RetriedFromRunId = prior.RunId },
        CancellationToken.None);
    var historyBefore = await store.ReadLogPageAsync(
        replacement.RunId,
        0,
        1000,
        CancellationToken.None);
    var output = new StringWriter();
    var handler = new WdemCommandHandler(
        service,
        store,
        output,
        new StringWriter(),
        redactor,
        sink);

    var exitCode = await handler.ResumeAsync(prior.RunId, json, CancellationToken.None);

    var historyAfter = await store.ReadLogPageAsync(
        replacement.RunId,
        0,
        1000,
        CancellationToken.None);
    var lines = output.ToString().Split(
        Environment.NewLine,
        StringSplitOptions.RemoveEmptyEntries);
    Assert.Equal(0, exitCode);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(historyBefore, historyAfter);
    Assert.Equal(historyBefore.Count, lines.Length);
    Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
    if (json)
    {
      var events = lines.Select(line =>
          JsonSerializer.Deserialize<RunEvent>(line, JsonOptions)!).ToArray();
      Assert.All(events, runEvent => Assert.Equal(replacement.RunId, runEvent.RunId));
      Assert.Equal(historyBefore.Select(entry => entry.Sequence),
          events.Select(runEvent => runEvent.Sequence));
      Assert.Equal(RunEventKind.RunStateChanged, events[0].Kind);
      Assert.Contains(events, runEvent =>
          runEvent.Kind == RunEventKind.StepProgress && runEvent.Progress == 0.5);
      Assert.Equal(RunEventKind.Completed, events[^1].Kind);
    }
    else
    {
      Assert.All(lines, line => Assert.Contains(
          replacement.RunId.ToString("D"),
          line,
          StringComparison.Ordinal));
      Assert.Contains(nameof(RunEventKind.RunStateChanged), lines[0], StringComparison.Ordinal);
      Assert.Contains(lines, line =>
          line.Contains(nameof(RunEventKind.StepProgress), StringComparison.Ordinal));
      Assert.Contains(nameof(RunEventKind.Completed), lines[^1], StringComparison.Ordinal);
    }
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

  private static ExecutionRun InterruptedRun() => new()
  {
    RunId = Guid.NewGuid(),
    Mode = RunMode.Apply,
    ProfileSourcePath = Path.GetFullPath("developer.yaml"),
    ProfileId = "developer",
    ProfileVersion = "1.0.0",
    SelectedOptionalResourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
    State = ExecutionState.Running,
    Machine = new MachineInformation("Windows", "X64", "machine", "user"),
    ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = new()
      {
        ResourceId = "git",
        State = ExecutionState.Running,
        DetectedBefore = new DetectedState
        {
          ResourceId = "git",
          Outcome = DetectionOutcome.Succeeded,
          Exists = false
        }
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

  private sealed class SuccessfulProvider(string secret) : IResourceProvider
  {
    private int _applyCalls;

    public string ResourceType => "package";
    public string ProviderName => "successful";
    public ProviderCapabilities Capabilities { get; } = new();
    public int ApplyCalls => Volatile.Read(ref _applyCalls);

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Succeeded,
          Exists = false
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
          Compliance = ComplianceStatus.Missing,
          IsExecutable = true,
          Steps =
          [
            new PlanStep
            {
              Id = "install",
              Description = "Install git",
              Action = PlanAction.Install,
              PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
              RestartPolicy = RestartPolicy.NoRestart
            }
          ]
        });

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      Interlocked.Increment(ref _applyCalls);
      progress?.Report(new ProviderProgress(
          "install",
          0.5,
          $"installing {secret}",
          "install"));
      return ValueTask.FromResult(new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Succeeded,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = "install",
            Action = PlanAction.Install,
            Progress = 1,
            Message = $"installed {secret}"
          }
        ]
      });
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => ValueTask.FromResult(new VerificationResult
        {
          ResourceId = resource.Id,
          Compliance = ComplianceStatus.Satisfied,
          DetectedState = new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Succeeded,
            Exists = true
          }
        });
  }
}
