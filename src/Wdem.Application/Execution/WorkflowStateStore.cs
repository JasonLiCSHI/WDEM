using System.Collections.ObjectModel;
using Wdem.Application.Execution;
using Wdem.Application.Runtime;
using Wdem.Domain.Execution;
using Wdem.Domain.Profiles;
using Wdem.Domain.Workflows;

namespace Wdem.Application.Execution;

/// <summary>
/// Owns authoritative runtime state and publishes immutable Task projections.
/// Only the workflow state machine can move a Task between runtime states.
/// </summary>
internal sealed class WorkflowStateStore
{
  /// <summary>Constants for progress calculation.</summary>
  private static class ProgressConstants
  {
    /// <summary>Minimum progress percentage.</summary>
    public const int MinPercent = 0;

    /// <summary>Maximum progress percentage before completion (100% means done).</summary>
    public const int MaxPercentInProgress = 99;

    /// <summary>Completion percentage.</summary>
    public const int CompletePercent = 100;
  }

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
      IReadOnlyCollection<string> plannedTaskIds,
      IReadOnlyDictionary<string, TaskWorkflowDefinition> workflows,
      IProgress<WorkflowProgress>? progress,
      IProgress<WorkflowUpdate>? updates)
  {
    _progress = progress;
    _updates = updates;
    var planned = plannedTaskIds.ToHashSet(StringComparer.Ordinal);
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

  /// <summary>
  /// Creates a ready snapshot with all tasks in Ready state and capable of being started.
  /// </summary>
  /// <param name="profile">The environment profile.</param>
  /// <param name="workflows">Workflow definitions for each task.</param>
  /// <returns>A snapshot representing the ready state.</returns>
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

  /// <summary>
  /// Transitions a task from Pending to Ready state.
  /// </summary>
  /// <param name="taskId">The task identifier.</param>
  /// <returns>True if transition succeeded; false if task was being cancelled.</returns>
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

  /// <summary>
  /// Records that a task has entered a workflow state.
  /// </summary>
  /// <param name="taskId">The task identifier.</param>
  /// <param name="runtimeStateId">The workflow runtime state identifier.</param>
  /// <param name="taskState">The execution state to transition to.</param>
  /// <param name="displayName">The display name of the state.</param>
  /// <returns>True if transition succeeded; false if task was being cancelled.</returns>
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

  /// <summary>
  /// Records that a workflow activity is beginning execution.
  /// </summary>
  /// <param name="taskId">The task identifier.</param>
  /// <param name="runtimeStateId">The workflow runtime state identifier.</param>
  /// <param name="taskState">The execution state to transition to.</param>
  /// <param name="activity">The activity being executed.</param>
  /// <param name="location">The location within the workflow (Entry/Residence/Exit).</param>
  /// <param name="activityIndex">The activity index within the sequence.</param>
  /// <returns>True if transition succeeded; false if task was being cancelled.</returns>
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

      var progressPercent = CalculateProgressPercent(activityIndex, current.ActivityCount);
      _tasks[taskId] = current with
      {
        State = taskState,
        Stage = activity.DisplayName,
        Percent = progressPercent,
        ActivityId = activity.Id,
        ActivityLocation = location,
        ActivityIndex = activityIndex
      };
      AdvanceLocked(CreateProgressLocked(taskId));
    }

    PublishPending();
    return true;
  }

  /// <summary>
  /// Completes a task with the specified outcome.
  /// </summary>
  /// <param name="taskId">The task identifier.</param>
  /// <param name="outcome">The desired task outcome.</param>
  /// <param name="runtimeStateId">Optional runtime state identifier at completion.</param>
  /// <returns>The effective outcome applied to the task.</returns>
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
      var terminalState = MapOutcomeToTerminalState(effectiveOutcome);
      _tasks[taskId] = current with
      {
        State = terminalState,
        RuntimeStateId = runtimeStateId ?? current.RuntimeStateId,
        Stage = null,
        Percent = ProgressConstants.CompletePercent,
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

  /// <summary>
  /// Publishes activity output to observers.
  /// </summary>
  /// <param name="taskId">The task identifier.</param>
  /// <param name="message">The output message.</param>
  /// <param name="stream">The output stream (stdout or stderr).</param>
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

  /// <summary>
  /// Requests cancellation of a specific task.
  /// </summary>
  /// <param name="taskId">The task identifier.</param>
  /// <returns>True if cancellation request succeeded; false if task cannot be cancelled.</returns>
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

  /// <summary>
  /// Requests cancellation of all planned tasks.
  /// </summary>
  /// <returns>True if cancellation succeeded; false if workflow is not running.</returns>
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

  /// <summary>
  /// Advances the workflow state and creates a new snapshot.
  /// </summary>
  /// <param name="change">Optional progress change to publish.</param>
  private void AdvanceLocked(WorkflowProgress? change)
  {
    _revision++;
    _snapshot = CreateSnapshotLocked();
    _pendingPublications.Enqueue(new WorkflowUpdate(_snapshot, change));
  }

  /// <summary>
  /// Creates an immutable snapshot of the current workflow state.
  /// </summary>
  /// <returns>A snapshot of all task states.</returns>
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

  /// <summary>
  /// Creates a snapshot for a single task with its current state and capabilities.
  /// </summary>
  /// <param name="taskId">The task identifier.</param>
  /// <param name="task">The task state record.</param>
  /// <returns>A snapshot of the task's current state.</returns>
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
          CreateTaskCapabilities(task))
      {
        RuntimeStateId = task.RuntimeStateId,
        ActivityId = task.ActivityId,
        ActivityLocation = task.ActivityLocation
      };

  /// <summary>
  /// Determines what actions are available for a task in its current state.
  /// </summary>
  /// <param name="task">The task state record.</param>
  /// <returns>The capabilities available for this task.</returns>
  private TaskCapabilities CreateTaskCapabilities(TaskState task) =>
      new TaskCapabilities(
          CanStart: _runState is WorkflowRunState.Ready or WorkflowRunState.Completed,
          CanCancel: _runState == WorkflowRunState.Running &&
              task.IsPlanned &&
              IsCancellable(task),
          CanSelect: (_runState is WorkflowRunState.Ready or WorkflowRunState.Completed) &&
              !task.Required);

  /// <summary>
  /// Creates a progress update for a task.
  /// </summary>
  /// <param name="taskId">The task identifier.</param>
  /// <returns>A progress report for the task.</returns>
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

  /// <summary>
  /// Checks if all planned tasks are complete and transitions workflow to Completed state.
  /// </summary>
  private void CompleteWorkflowIfTerminalLocked()
  {
    if (_tasks.Values.Where(task => task.IsPlanned).All(task => task.IsCompleted))
    {
      _runState = WorkflowRunState.Completed;
    }
  }

  /// <summary>
  /// Retrieves a task's state, throwing if not found.
  /// </summary>
  /// <param name="taskId">The task identifier.</param>
  /// <returns>The task's state record.</returns>
  /// <exception cref="ArgumentException">Thrown when task is not found.</exception>
  private TaskState GetTaskLocked(string taskId) =>
      _tasks.TryGetValue(taskId, out var task)
          ? task
          : throw new ArgumentException($"Unknown task id '{taskId}'.", nameof(taskId));

  /// <summary>
  /// Publishes pending workflow updates to all observers.
  /// </summary>
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

  /// <summary>
  /// Determines if a task can be cancelled.
  /// </summary>
  /// <param name="task">The task state record.</param>
  /// <returns>True if the task is not completed and not already cancelling.</returns>
  private static bool IsCancellable(TaskState task) =>
      !task.IsCompleted && task.State != TaskExecutionState.Cancelling;

  /// <summary>
  /// Maps a task outcome to its corresponding terminal execution state.
  /// </summary>
  /// <param name="outcome">The task outcome.</param>
  /// <returns>The corresponding terminal execution state.</returns>
  private static TaskExecutionState MapOutcomeToTerminalState(TaskOutcome outcome) => outcome switch
  {
    TaskOutcome.Succeeded => TaskExecutionState.Succeeded,
    TaskOutcome.NotRequired => TaskExecutionState.Satisfied,
    TaskOutcome.Failed => TaskExecutionState.Failed,
    TaskOutcome.Cancelled => TaskExecutionState.Cancelled,
    TaskOutcome.Blocked => TaskExecutionState.Blocked,
    _ => TaskExecutionState.NotSelected
  };

  /// <summary>
  /// Calculates the progress percentage for an ongoing activity.
  /// </summary>
  /// <param name="activityIndex">The current activity index.</param>
  /// <param name="activityCount">The total number of activities.</param>
  /// <returns>The progress percentage, clamped between 0 and 99.</returns>
  private static int CalculateProgressPercent(int activityIndex, int activityCount) =>
      activityCount == 0
          ? ProgressConstants.MinPercent
          : Math.Clamp((activityIndex - 1) * 100 / activityCount, ProgressConstants.MinPercent, ProgressConstants.MaxPercentInProgress);

  /// <summary>
  /// Immutable record representing the runtime state of a single task.
  /// </summary>
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
    /// <summary>True if the task has finished execution (planned and has an outcome).</summary>
    public bool IsCompleted => !IsPlanned || Outcome is not null;
  }
}
