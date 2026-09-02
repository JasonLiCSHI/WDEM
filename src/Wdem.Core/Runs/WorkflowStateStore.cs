using System.Collections.ObjectModel;
using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Core.Workflows;

namespace Wdem.Core.Runs;

/// <summary>
/// Owns authoritative runtime state and publishes immutable Task projections.
/// Only the workflow state machine can move a Task between runtime states.
/// </summary>
internal sealed class WorkflowStateStore
{
  private readonly Lock _gate = new();
  private readonly Dictionary<string, TaskState> _tasks;
  private readonly IProgress<WorkflowProgress>? _progress;
  private readonly IProgress<WorkflowUpdate>? _updates;
  private readonly Queue<WorkflowUpdate> _pendingPublications = new();
  private WorkflowRunState _runState;
  private WorkflowSnapshot _snapshot;
  private long _revision;
  private bool _isPublishing;

  public WorkflowStateStore(
      EnvironmentProfile profile,
      TaskGraph graph,
      IReadOnlyDictionary<string, TaskWorkflowDefinition> workflows,
      IProgress<WorkflowProgress>? progress,
      IProgress<WorkflowUpdate>? updates)
  {
    _progress = progress;
    _updates = updates;
    var planned = graph.OrderedTaskIds.ToHashSet(StringComparer.Ordinal);
    _tasks = profile.Tasks.Values.ToDictionary(
        task => task.Id,
        task => new TaskState(
            task.Required,
            planned.Contains(task.Id),
            planned.Contains(task.Id) ? TaskExecutionState.Pending : TaskExecutionState.NotSelected,
            RuntimeStateId: null,
            Stage: null,
            Percent: 0,
            Outcome: null,
            ActivityId: null,
            ActivityLocation: null,
            ActivityIndex: 0,
            ActivityCount: workflows[task.Id].ActivityCount),
        StringComparer.Ordinal);
    _runState = planned.Count == 0 ? WorkflowRunState.Completed : WorkflowRunState.Running;
    _snapshot = CreateSnapshotLocked();
  }

  public WorkflowSnapshot Snapshot
  {
    get
    {
      lock (_gate)
      {
        return _snapshot;
      }
    }
  }

  public static WorkflowSnapshot CreateReadySnapshot(
      EnvironmentProfile profile,
      IReadOnlyDictionary<string, TaskWorkflowDefinition> workflows)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(workflows);
    var tasks = profile.Tasks.Values.ToDictionary(
        task => task.Id,
        task => new WorkflowTaskSnapshot(
            task.Id,
            TaskExecutionState.Ready,
            Stage: null,
            Percent: 0,
            Outcome: null,
            IsPlanned: false,
            ActivityIndex: 0,
            ActivityCount: workflows[task.Id].ActivityCount,
            new TaskCapabilities(
                CanStart: true,
                CanCancel: false,
                CanSelect: !task.Required)),
        StringComparer.Ordinal);
    return new WorkflowSnapshot(
        Revision: 0,
        WorkflowRunState.Ready,
        new ReadOnlyDictionary<string, WorkflowTaskSnapshot>(tasks));
  }

  public bool MakeReady(string taskId)
  {
    lock (_gate)
    {
      var current = GetTaskLocked(taskId);
      if (current.State == TaskExecutionState.Cancelling)
      {
        return false;
      }
      if (current.IsCompleted || current.State != TaskExecutionState.Pending)
      {
        throw new InvalidOperationException($"Task '{taskId}' cannot become ready from {current.State}.");
      }

      _tasks[taskId] = current with
      {
        State = TaskExecutionState.Ready,
        RuntimeStateId = null,
        Stage = null,
        ActivityId = null,
        ActivityLocation = null
      };
      AdvanceLocked(CreateProgressLocked(taskId));
    }

    PublishPending();
    return true;
  }

  public bool EnterState(
      string taskId,
      string runtimeStateId,
      TaskExecutionState taskState,
      string displayName)
  {
    lock (_gate)
    {
      var current = GetTaskLocked(taskId);
      if (current.State == TaskExecutionState.Cancelling)
      {
        return false;
      }
      if (current.IsCompleted)
      {
        throw new InvalidOperationException($"Completed task '{taskId}' cannot enter a workflow state.");
      }

      _tasks[taskId] = current with
      {
        State = taskState,
        RuntimeStateId = runtimeStateId,
        Stage = displayName,
        ActivityId = null,
        ActivityLocation = null
      };
      AdvanceLocked(CreateProgressLocked(taskId));
    }

    PublishPending();
    return true;
  }

  public bool BeginActivity(
      string taskId,
      string runtimeStateId,
      TaskExecutionState taskState,
      WorkflowActivity activity,
      WorkflowActivityLocation location,
      int activityIndex)
  {
    lock (_gate)
    {
      var current = GetTaskLocked(taskId);
      if (current.State == TaskExecutionState.Cancelling)
      {
        return false;
      }
      if (current.IsCompleted || !string.Equals(current.RuntimeStateId, runtimeStateId, StringComparison.Ordinal))
      {
        throw new InvalidOperationException(
            $"Task '{taskId}' is not residing in workflow state '{runtimeStateId}'.");
      }

      var percent = current.ActivityCount == 0
          ? 0
          : Math.Clamp((activityIndex - 1) * 100 / current.ActivityCount, 0, 99);
      _tasks[taskId] = current with
      {
        State = taskState,
        Stage = activity.DisplayName,
        Percent = percent,
        ActivityId = activity.Id,
        ActivityLocation = location,
        ActivityIndex = activityIndex
      };
      AdvanceLocked(CreateProgressLocked(taskId));
    }

    PublishPending();
    return true;
  }

  public TaskOutcome CompleteTask(
      string taskId,
      TaskOutcome outcome,
      string? runtimeStateId = null)
  {
    TaskOutcome effectiveOutcome;
    lock (_gate)
    {
      var current = GetTaskLocked(taskId);
      if (current.IsCompleted)
      {
        return current.Outcome ?? outcome;
      }

      effectiveOutcome = current.State == TaskExecutionState.Cancelling
          ? TaskOutcome.Cancelled
          : outcome;
      var terminalState = effectiveOutcome switch
      {
        TaskOutcome.Succeeded => TaskExecutionState.Succeeded,
        TaskOutcome.NotRequired => TaskExecutionState.Satisfied,
        TaskOutcome.Failed => TaskExecutionState.Failed,
        TaskOutcome.Cancelled => TaskExecutionState.Cancelled,
        TaskOutcome.Blocked => TaskExecutionState.Blocked,
        _ => TaskExecutionState.NotSelected
      };
      _tasks[taskId] = current with
      {
        State = terminalState,
        RuntimeStateId = runtimeStateId ?? current.RuntimeStateId,
        Stage = null,
        Percent = 100,
        Outcome = effectiveOutcome,
        ActivityId = null,
        ActivityLocation = null,
        ActivityIndex = current.ActivityCount
      };
      CompleteWorkflowIfTerminalLocked();
      AdvanceLocked(CreateProgressLocked(taskId));
    }

    PublishPending();
    return effectiveOutcome;
  }

  public void PublishOutput(
      string taskId,
      string message,
      WorkflowOutputStream stream)
  {
    lock (_gate)
    {
      var change = CreateProgressLocked(taskId) with
      {
        Message = message,
        OutputStream = stream
      };
      AdvanceLocked(change);
    }

    PublishPending();
  }

  public bool RequestCancelTask(string taskId)
  {
    lock (_gate)
    {
      var current = GetTaskLocked(taskId);
      if (!CreateTaskSnapshotLocked(taskId, current).CanCancel)
      {
        return false;
      }

      _tasks[taskId] = current with
      {
        State = TaskExecutionState.Cancelling,
        Stage = null,
        ActivityId = null,
        ActivityLocation = null
      };
      AdvanceLocked(CreateProgressLocked(taskId));
    }

    PublishPending();
    return true;
  }

  public bool RequestCancelAll()
  {
    lock (_gate)
    {
      if (_runState != WorkflowRunState.Running)
      {
        return false;
      }

      _runState = WorkflowRunState.Cancelling;
      foreach (var (taskId, current) in _tasks.ToArray())
      {
        if (current.IsPlanned && IsCancellable(current))
        {
          _tasks[taskId] = current with
          {
            State = TaskExecutionState.Cancelling,
            Stage = null,
            ActivityId = null,
            ActivityLocation = null
          };
        }
      }

      AdvanceLocked(change: null);
    }

    PublishPending();
    return true;
  }

  private void AdvanceLocked(WorkflowProgress? change)
  {
    _revision++;
    _snapshot = CreateSnapshotLocked();
    _pendingPublications.Enqueue(new WorkflowUpdate(_snapshot, change));
  }

  private WorkflowSnapshot CreateSnapshotLocked()
  {
    var tasks = _tasks.ToDictionary(
        pair => pair.Key,
        pair => CreateTaskSnapshotLocked(pair.Key, pair.Value),
        StringComparer.Ordinal);
    return new WorkflowSnapshot(
        _revision,
        _runState,
        new ReadOnlyDictionary<string, WorkflowTaskSnapshot>(tasks));
  }

  private WorkflowTaskSnapshot CreateTaskSnapshotLocked(string taskId, TaskState task) =>
      new WorkflowTaskSnapshot(
          taskId,
          task.State,
          task.Stage,
          task.Percent,
          task.Outcome,
          task.IsPlanned,
          task.ActivityIndex,
          task.ActivityCount,
          new TaskCapabilities(
              CanStart: _runState is WorkflowRunState.Ready or WorkflowRunState.Completed,
              CanCancel: _runState == WorkflowRunState.Running &&
                  task.IsPlanned &&
                  IsCancellable(task),
              CanSelect: (_runState is WorkflowRunState.Ready or WorkflowRunState.Completed) &&
                  !task.Required))
      {
        RuntimeStateId = task.RuntimeStateId,
        ActivityId = task.ActivityId,
        ActivityLocation = task.ActivityLocation
      };

  private WorkflowProgress CreateProgressLocked(string taskId)
  {
    var task = GetTaskLocked(taskId);
    return new WorkflowProgress(
        taskId,
        task.State,
        task.Stage,
        task.Percent,
        task.Outcome)
    {
      RuntimeStateId = task.RuntimeStateId,
      ActivityId = task.ActivityId,
      ActivityLocation = task.ActivityLocation
    };
  }

  private void CompleteWorkflowIfTerminalLocked()
  {
    if (_tasks.Values.Where(task => task.IsPlanned).All(task => task.IsCompleted))
    {
      _runState = WorkflowRunState.Completed;
    }
  }

  private TaskState GetTaskLocked(string taskId) =>
      _tasks.TryGetValue(taskId, out var task)
          ? task
          : throw new ArgumentException($"Unknown task id '{taskId}'.", nameof(taskId));

  private void PublishPending()
  {
    lock (_gate)
    {
      if (_isPublishing)
      {
        return;
      }

      _isPublishing = true;
    }

    try
    {
      while (true)
      {
        WorkflowUpdate update;
        lock (_gate)
        {
          if (_pendingPublications.Count == 0)
          {
            _isPublishing = false;
            return;
          }

          update = _pendingPublications.Dequeue();
        }

        if (update.Change is { } change)
        {
          _progress?.Report(change);
        }
        _updates?.Report(update);
      }
    }
    catch
    {
      lock (_gate)
      {
        _isPublishing = false;
      }
      throw;
    }
  }

  private static bool IsCancellable(TaskState task) =>
      !task.IsCompleted && task.State != TaskExecutionState.Cancelling;

  private sealed record TaskState(
      bool Required,
      bool IsPlanned,
      TaskExecutionState State,
      string? RuntimeStateId,
      string? Stage,
      int Percent,
      TaskOutcome? Outcome,
      string? ActivityId,
      WorkflowActivityLocation? ActivityLocation,
      int ActivityIndex,
      int ActivityCount)
  {
    public bool IsCompleted => !IsPlanned || Outcome is not null;
  }
}
