using Wdem.App;
using Xunit;

namespace Wdem.App.Tests;

public sealed class WorkspaceActionStateTests
{
  [Fact]
  public void SourceUnavailable_OnlyRefreshIsEnabled()
  {
    var state = WorkspaceActionState.Project(Context());

    Assert.Equal(WorkspaceMode.Unavailable, state.Mode);
    Assert.True(state.CanRefresh);
    Assert.False(state.CanChooseProfile);
    Assert.False(state.CanInspect);
    Assert.False(state.CanRetry);
  }

  [Fact]
  public void ReadyProfile_EnablesWorkspaceActions()
  {
    var state = WorkspaceActionState.Project(Context(
        hasCatalog: true,
        hasProfileChoice: true,
        hasTrustedProfile: true));

    Assert.Equal(WorkspaceMode.Ready, state.Mode);
    Assert.True(state.CanChooseProfile);
    Assert.True(state.CanInspect);
  }

  [Fact]
  public void RunningWorkflow_DisablesWorkspaceActions()
  {
    var state = WorkspaceActionState.Project(Context(
        isRunning: true,
        hasCatalog: true,
        hasProfileChoice: true,
        hasTrustedProfile: true));

    Assert.Equal(WorkspaceMode.Running, state.Mode);
    Assert.False(state.CanRefresh);
    Assert.False(state.CanChooseProfile);
    Assert.False(state.CanInspect);
  }

  [Fact]
  public void AwaitingTrust_DoesNotAllowCommandExecution()
  {
    var state = WorkspaceActionState.Project(Context(
        hasCatalog: true,
        hasProfileChoice: true));

    Assert.Equal(WorkspaceMode.AwaitingTrust, state.Mode);
    Assert.True(state.CanRefresh);
    Assert.True(state.CanChooseProfile);
    Assert.False(state.CanInspect);
  }

  private static WorkspaceActionContext Context(
      bool isRunning = false,
      bool hasCatalog = false,
      bool hasProfileChoice = false,
      bool hasTrustedProfile = false) =>
      new(
          IsCatalogLoading: false,
          IsProfileLoading: false,
          IsInspecting: false,
          WorkflowState: isRunning ? Wdem.Core.Runs.WorkflowRunState.Running : null,
          HasCatalog: hasCatalog,
          HasProfileChoice: hasProfileChoice,
          HasTrustedProfile: hasTrustedProfile,
          HasRetryPlan: false);
}
