using Wdem.Core.Runs;

namespace Wdem.App;

public enum WorkspaceMode
{
  Loading,
  Unavailable,
  AwaitingTrust,
  Ready,
  Inspecting,
  Running,
  Cancelling
}

internal readonly record struct WorkspaceActionContext(
    bool IsCatalogLoading,
    bool IsProfileLoading,
    bool IsInspecting,
    WorkflowRunState? WorkflowState,
    bool HasCatalog,
    bool HasProfileChoice,
    bool HasTrustedProfile,
    bool HasRetryPlan);

/// <summary>
/// Projects all workspace command availability from one immutable snapshot.
/// Click handlers still validate state so disabled controls are never the safety boundary.
/// </summary>
public readonly record struct WorkspaceActionState(
    WorkspaceMode Mode,
    bool CanRefresh,
    bool CanChooseProfile,
    bool CanInspect,
    bool CanRetry)
{
  internal static WorkspaceActionState Project(WorkspaceActionContext context)
  {
    var mode = context switch
    {
      { IsCatalogLoading: true } or { IsProfileLoading: true } => WorkspaceMode.Loading,
      { IsInspecting: true } => WorkspaceMode.Inspecting,
      { WorkflowState: WorkflowRunState.Cancelling } => WorkspaceMode.Cancelling,
      { WorkflowState: WorkflowRunState.Running } => WorkspaceMode.Running,
      { HasCatalog: false } or { HasProfileChoice: false } => WorkspaceMode.Unavailable,
      { HasTrustedProfile: false } => WorkspaceMode.AwaitingTrust,
      _ => WorkspaceMode.Ready
    };

    var idle = mode is WorkspaceMode.Unavailable or WorkspaceMode.AwaitingTrust or WorkspaceMode.Ready;
    var ready = mode == WorkspaceMode.Ready;
    return new WorkspaceActionState(
        Mode: mode,
        CanRefresh: idle,
        CanChooseProfile: idle && context.HasCatalog && context.HasProfileChoice,
        CanInspect: ready,
        CanRetry: ready && context.HasRetryPlan);
  }
}
