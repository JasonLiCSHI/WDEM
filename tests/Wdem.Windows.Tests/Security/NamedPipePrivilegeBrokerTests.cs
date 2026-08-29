using System.ComponentModel;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Execution;
using Wdem.Windows.Security;
using Xunit;

namespace Wdem.Windows.Tests.Security;

public sealed class NamedPipePrivilegeBrokerTests
{
  [Fact]
  public async Task ApplyAsync_TwoAdministratorResources_StartsOneElevatedHost()
  {
    var launcher = new RecordingElevatedHostLauncher();
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();

    await broker.ApplyAsync(Request(runId, "visual-studio"), null, CancellationToken.None);
    await broker.ApplyAsync(Request(runId, "vsix"), null, CancellationToken.None);

    Assert.Equal(1, launcher.StartCalls);
    Assert.Equal(2, launcher.Session.Requests.Count);
    Assert.All(launcher.Session.Requests, request =>
    {
      Assert.Equal(runId, request.RunId);
      Assert.False(string.IsNullOrWhiteSpace(request.PipeName));
      Assert.Equal(launcher.PipeNames.Single(), request.PipeName);
    });
  }

  [Fact]
  public async Task ApplyAsync_UacDeclined_ReturnsPermissionError()
  {
    var launcher = new RecordingElevatedHostLauncher
    {
      StartException = new Win32Exception(1223)
    };
    var broker = new NamedPipePrivilegeBroker(launcher);

    var result = await broker.ApplyAsync(
        Request(Guid.NewGuid(), "visual-studio"),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Cancelled, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
  }

  [Fact]
  public async Task ApplyAsync_UacDeclinedTwiceInOneRun_StartsElevatedHostOnce()
  {
    var launcher = new RecordingElevatedHostLauncher
    {
      StartException = new Win32Exception(1223)
    };
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();

    var first = await broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        CancellationToken.None);
    var second = await broker.ApplyAsync(
        Request(runId, "vsix"),
        null,
        CancellationToken.None);

    Assert.Equal(1, launcher.StartCalls);
    Assert.Equal(ApplyOutcome.Cancelled, second.Outcome);
    Assert.Equal(first.Error!.Code, second.Error!.Code);
    Assert.Equal(first.Error.Summary, second.Error.Summary);
    Assert.Equal(first.Error.Detail, second.Error.Detail);
    Assert.Equal("vsix", second.ResourceId);
    Assert.Equal("vsix", second.Error.ResourceId);
  }

  [Fact]
  public async Task ApplyAsync_ConcurrentUacDeclinesInOneRun_StartElevatedHostOnce()
  {
    var launcher = new RecordingElevatedHostLauncher
    {
      StartException = new Win32Exception(1223)
    };
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();

    var results = await Task.WhenAll(
        broker.ApplyAsync(
            Request(runId, "visual-studio"),
            null,
            CancellationToken.None),
        broker.ApplyAsync(
            Request(runId, "vsix"),
            null,
            CancellationToken.None));

    Assert.Equal(1, launcher.StartCalls);
    Assert.All(results, result =>
    {
      Assert.Equal(ApplyOutcome.Cancelled, result.Outcome);
      Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
      Assert.Equal("Administrator approval was declined.", result.Error.Summary);
    });
  }

  [Fact]
  public async Task ApplyAsync_UacDeclinedForDifferentRuns_RetriesElevatedLaunch()
  {
    var launcher = new RecordingElevatedHostLauncher
    {
      StartException = new Win32Exception(1223)
    };
    var broker = new NamedPipePrivilegeBroker(launcher);

    await broker.ApplyAsync(
        Request(Guid.NewGuid(), "visual-studio"),
        null,
        CancellationToken.None);
    await broker.ApplyAsync(
        Request(Guid.NewGuid(), "vsix"),
        null,
        CancellationToken.None);

    Assert.Equal(2, launcher.StartCalls);
  }

  [Fact]
  public async Task ApplyAsync_ElevatedHostLaunchFails_ReturnsCachedPermissionError()
  {
    var launcher = new RecordingElevatedHostLauncher
    {
      StartException = new InvalidOperationException("host unavailable")
    };
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();

    var first = await broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        CancellationToken.None);
    var second = await broker.ApplyAsync(
        Request(runId, "vsix"),
        null,
        CancellationToken.None);

    Assert.Equal(1, launcher.StartCalls);
    Assert.Equal(ApplyOutcome.Failed, first.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, first.Error!.Code);
    Assert.Equal("Elevated host could not be started.", first.Error.Summary);
    Assert.Equal(first.Error.Summary, second.Error!.Summary);
    Assert.Equal(first.Error.Detail, second.Error.Detail);
    Assert.Equal("vsix", second.Error.ResourceId);
  }

  [Fact]
  public void ElevatedResourceRequest_ContainsOnlyApprovedIdentifiers()
  {
    var properties = typeof(ElevatedResourceRequest)
        .GetProperties()
        .Select(property => property.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();

    Assert.Equal(
        ["PipeName", "PlanFingerprint", "ResourceId", "RunId"],
        properties);
  }

  [Fact]
  public async Task ApplyAsync_CancellationTerminatesElevatedHost()
  {
    var launcher = new RecordingElevatedHostLauncher();
    launcher.Session.WaitForCancellation = true;
    var broker = new NamedPipePrivilegeBroker(launcher);
    using var cancellation = new CancellationTokenSource();

    var apply = broker.ApplyAsync(
        Request(Guid.NewGuid(), "visual-studio"),
        null,
        cancellation.Token);
    await launcher.Session.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
    Assert.Equal(1, launcher.Session.TerminateCalls);
  }

  [Fact]
  public async Task ApplyAsync_DifferentRuns_StartDifferentElevatedHosts()
  {
    var launcher = new RecordingElevatedHostLauncher();
    var broker = new NamedPipePrivilegeBroker(launcher);

    await broker.ApplyAsync(
        Request(Guid.NewGuid(), "visual-studio"),
        null,
        CancellationToken.None);
    await broker.ApplyAsync(
        Request(Guid.NewGuid(), "vsix"),
        null,
        CancellationToken.None);

    Assert.Equal(2, launcher.StartCalls);
    Assert.Equal(2, launcher.PipeNames.Distinct(StringComparer.Ordinal).Count());
  }

  [Fact]
  public async Task CompleteRunAsync_TerminatesElevatedHost()
  {
    var launcher = new RecordingElevatedHostLauncher();
    var broker = new NamedPipePrivilegeBroker(launcher);
    var runId = Guid.NewGuid();
    await broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        CancellationToken.None);

    await broker.CompleteRunAsync(runId, CancellationToken.None);

    Assert.Equal(1, launcher.Session.TerminateCalls);
  }

  [Fact]
  public async Task DisposeAsync_ActiveApplyWaitsForCancellationWithoutDisposedSemaphoreRace()
  {
    var launcher = new RecordingElevatedHostLauncher();
    launcher.Session.WaitForCancellation = true;
    var broker = new NamedPipePrivilegeBroker(launcher);
    using var cancellation = new CancellationTokenSource();
    var runId = Guid.NewGuid();
    var apply = broker.ApplyAsync(
        Request(runId, "visual-studio"),
        null,
        cancellation.Token);
    await launcher.Session.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var disposing = broker.DisposeAsync().AsTask();
    await launcher.Session.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var disposedBeforeApplyCompleted = disposing.IsCompleted;
    cancellation.Cancel();
    var exception = await Record.ExceptionAsync(() => apply);
    await disposing.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(disposedBeforeApplyCompleted);
    Assert.IsAssignableFrom<OperationCanceledException>(exception);
    await Assert.ThrowsAsync<ObjectDisposedException>(() => broker.ApplyAsync(
        Request(Guid.NewGuid(), "vsix"),
        null,
        CancellationToken.None));
  }

  [Fact]
  public async Task ApplyAsync_CurrentUserPlan_BypassesBroker()
  {
    var provider = new RecordingProvider();
    var broker = new RecordingPrivilegeBroker();
    var dispatcher = new PrivilegeAwareResourceApplyDispatcher(
        new DirectResourceApplyDispatcher(),
        broker);
    var runId = Guid.NewGuid();
    var resource = Resource(PrivilegeRequirement.CurrentUser);
    var plan = Plan(resource, PrivilegeRequirement.CurrentUser);

    await dispatcher.ApplyAsync(
        runId,
        provider,
        resource,
        plan,
        null,
        CancellationToken.None);

    Assert.Equal(1, provider.ApplyCalls);
    Assert.Empty(broker.Requests);
  }

  [Fact]
  public async Task ApplyAsync_AdministratorPlan_UsesRestrictedBrokerRequest()
  {
    var provider = new RecordingProvider();
    var broker = new RecordingPrivilegeBroker();
    var dispatcher = new PrivilegeAwareResourceApplyDispatcher(
        new DirectResourceApplyDispatcher(),
        broker);
    var runId = Guid.NewGuid();
    var resource = Resource(PrivilegeRequirement.Administrator);
    var plan = Plan(resource, PrivilegeRequirement.Administrator);

    await dispatcher.ApplyAsync(
        runId,
        provider,
        resource,
        plan,
        null,
        CancellationToken.None);

    Assert.Equal(0, provider.ApplyCalls);
    var request = Assert.Single(broker.Requests);
    Assert.Equal(runId, request.RunId);
    Assert.Equal(resource.Id, request.ResourceId);
    Assert.Equal(
        ApprovedResourceFingerprint.Create(resource, plan),
        request.PlanFingerprint);
    Assert.Equal(string.Empty, request.PipeName);
  }

  private static ElevatedResourceRequest Request(Guid runId, string resourceId) => new(
      runId,
      resourceId,
      new string('A', 64),
      string.Empty);

  private static ResourceDefinition Resource(PrivilegeRequirement privilege) => new()
  {
    Id = "resource",
    Type = "test",
    Provider = "test",
    PrivilegeRequirement = privilege
  };

  private static ResourcePlan Plan(
      ResourceDefinition resource,
      PrivilegeRequirement privilege) => new()
      {
        ResourceId = resource.Id,
        ResourceType = resource.Type,
        ProviderName = resource.Provider,
        DesiredStateFingerprint = new string('C', 64),
        Compliance = ComplianceStatus.Missing,
        IsExecutable = true,
        Steps =
        [
          new PlanStep
          {
            Id = "resource:apply",
            Description = "Apply resource.",
            Action = PlanAction.Configure,
            PrivilegeRequirement = privilege,
            RestartPolicy = RestartPolicy.NoRestart
          }
        ]
      };

  private sealed class RecordingElevatedHostLauncher : IElevatedHostLauncher
  {
    public int StartCalls { get; private set; }
    public Exception? StartException { get; init; }
    public List<string> PipeNames { get; } = [];
    public RecordingElevatedHostSession Session { get; } = new();

    public Task<IElevatedHostSession> StartAsync(
        Guid runId,
        string pipeName,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      StartCalls++;
      PipeNames.Add(pipeName);
      if (StartException is not null)
      {
        throw StartException;
      }

      return Task.FromResult<IElevatedHostSession>(Session);
    }
  }

  private sealed class RecordingElevatedHostSession : IElevatedHostSession
  {
    public List<ElevatedResourceRequest> Requests { get; } = [];
    public bool WaitForCancellation { get; set; }
    public int TerminateCalls { get; private set; }
    public TaskCompletionSource RequestStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Terminated { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ResourceApplyResult> ApplyAsync(
        ElevatedResourceRequest request,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Requests.Add(request);
      RequestStarted.TrySetResult();
      if (WaitForCancellation)
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      }

      return new ResourceApplyResult
      {
        ResourceId = request.ResourceId,
        Outcome = ApplyOutcome.Succeeded
      };
    }

    public Task TerminateAsync(CancellationToken cancellationToken)
    {
      TerminateCalls++;
      Terminated.TrySetResult();
      return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class RecordingPrivilegeBroker : IPrivilegeBroker
  {
    public List<ElevatedResourceRequest> Requests { get; } = [];

    public Task<ResourceApplyResult> ApplyAsync(
        ElevatedResourceRequest request,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      Requests.Add(request);
      return Task.FromResult(new ResourceApplyResult
      {
        ResourceId = request.ResourceId,
        Outcome = ApplyOutcome.Succeeded
      });
    }
  }

  private sealed class RecordingProvider : IResourceProvider
  {
    public string ResourceType => "test";
    public string ProviderName => "test";
    public ProviderCapabilities Capabilities { get; } = new();
    public int ApplyCalls { get; private set; }

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      ApplyCalls++;
      return ValueTask.FromResult(new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Succeeded
      });
    }

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
    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
  }
}
