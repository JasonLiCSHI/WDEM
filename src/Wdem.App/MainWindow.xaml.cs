using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Core.Runs;
using Wdem.Windows.Configuration;
using Wdem.Windows.Logging;
using Wdem.Windows.Processes;
using Wdem.Windows.Runtime;

namespace Wdem.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
  private readonly WindowsTaskRuntime _runtime = new(new DefaultProcessRunner());
  private readonly JsonLineSessionLog _log;
  private WdemUserSettingsStore? _settings;
  private ProfileCatalog? _catalog;
  private LoadedProfile? _loadedProfile;
  private EnvironmentRun? _currentRun;
  private TaskGraph? _retryGraph;
  private CancellationTokenSource? _inspectCancellation;
  private Task<InspectReport>? _inspectionTask;
  private long _operationGeneration;
  private long _lastWorkflowRevision = -1;
  private bool _profileTrusted;
  private bool _isInspecting;
  private bool _isCatalogLoading;
  private bool _isProfileLoading;
  private bool _closePending;
  private bool _allowClose;

  public MainWindow()
  {
    InitializeComponent();
    DataContext = this;

    _log = JsonLineSessionLog.Create("gui");
    LogPath = _log.DisplayPath;
    AppendLog("startup", I18n.Get("StartupLog"));
    if (_log.LastError is not null)
    {
      AppendLog("warning", _log.LastError);
    }

    UpdateCommandStates();
    Loaded += async (_, _) => await InitializeSourcesAsync(autoLoadProfile: true);
  }

  public ObservableCollection<ProfileCatalogEntry> Profiles { get; } = [];

  public ObservableCollection<TaskRow> RequiredTasks { get; } = [];

  public ObservableCollection<TaskRow> OptionalTasks { get; } = [];

  public string LogPath { get; }

  public WorkspaceActionState Actions { get; private set; }

  public bool CanStartAll { get; private set; }

  public bool CanCancelAll { get; private set; }

  private IEnumerable<TaskRow> AllTasks => RequiredTasks.Concat(OptionalTasks);

  private async void RefreshProfiles_Click(object sender, RoutedEventArgs e)
  {
    LogUserAction("refresh_profiles", UserActionOutcome.Requested);
    await InitializeSourcesAsync(autoLoadProfile: true);
  }

  private async void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    UpdateCommandStates();
    if (!HasExclusiveActivity() && ProfileComboBox.SelectedItem is ProfileCatalogEntry entry)
    {
      _log.WriteUserAction(
          "select_profile",
          UserActionOutcome.Requested,
          profileId: entry.Id);
      await LoadSelectedProfileAsync();
    }
  }

  private async Task InitializeSourcesAsync(bool autoLoadProfile)
  {
    if (HasExclusiveActivity())
    {
      return;
    }

    _isCatalogLoading = true;
    _catalog = null;
    Profiles.Clear();
    ClearLoadedProfile();
    ProfileSummaryText.Text = I18n.Get("ReadySummary");
    RunSummaryText.Text = I18n.Get("ReadySummary");
    UpdateCommandStates();

    var shouldLoadProfile = false;
    try
    {
      _settings = WdemUserSettingsStore.OpenDefault();
      var source = _settings.ProfileSource;
      _catalog = new ProfileCatalog(source, _settings.CacheDirectory);
      var entries = await _catalog.ListAsync();
      foreach (var entry in entries)
      {
        Profiles.Add(entry);
      }

      ProfileComboBox.SelectedIndex = Profiles.Count > 0 ? 0 : -1;
      AppendLog("catalog", I18n.Format("CatalogCountLog", source.DisplayName, Profiles.Count));
      shouldLoadProfile = autoLoadProfile && Profiles.Count > 0;
    }
    catch (Exception exception)
    {
      _catalog = null;
      Profiles.Clear();
      ClearLoadedProfile();
      ProfileSummaryText.Text = I18n.Get("CatalogErrorTitle");
      RunSummaryText.Text = I18n.Get("SourceUnavailableSummary");
      AppendLog("catalog_error", exception.ToString());
    }
    finally
    {
      _isCatalogLoading = false;
      UpdateCommandStates();
    }

    if (shouldLoadProfile)
    {
      await LoadSelectedProfileAsync();
    }
  }

  private async Task LoadSelectedProfileAsync()
  {
    if (_catalog is null || ProfileComboBox.SelectedItem is not ProfileCatalogEntry entry)
    {
      MessageBox.Show(this, I18n.Get("SelectProfileMessage"), I18n.Get("MessageTitle"));
      return;
    }

    _isProfileLoading = true;
    ClearLoadedProfile();
    UpdateCommandStates();
    var shouldInspect = false;
    try
    {
      var loaded = await _catalog.LoadAsync(entry.Id);
      _loadedProfile = loaded;

      foreach (var definition in loaded.Profile.Tasks.Values
                   .OrderBy(value => value.Id, StringComparer.Ordinal))
      {
        var row = new TaskRow(definition);
        row.PropertyChanged += TaskRow_PropertyChanged;
        (definition.Required ? RequiredTasks : OptionalTasks).Add(row);
      }

      ProfileSummaryText.Text = $"{loaded.Profile.DisplayName} {loaded.Profile.Version}";
      AppendLog(
          "profile",
          I18n.Format(
              "ProfileLoadedLog",
              loaded.Profile.Id,
              loaded.Profile.Version,
              loaded.Location));

      _profileTrusted = EnsureTrusted();
      if (!_profileTrusted)
      {
        RunSummaryText.Text = I18n.Get("NotTrustedSummary");
        return;
      }

      ApplyWorkflowSnapshot(EnvironmentManager.CreateReadySnapshot(loaded.Profile));
      shouldInspect = true;
    }
    catch (Exception exception)
    {
      ClearLoadedProfile();
      ShowError(I18n.Get("LoadErrorTitle"), exception);
    }
    finally
    {
      _isProfileLoading = false;
      UpdateCommandStates();
    }

    if (shouldInspect)
    {
      await InspectAsync();
    }
  }

  private bool EnsureTrusted()
  {
    if (_loadedProfile is null || _settings is null || _settings.IsTrusted(_loadedProfile))
    {
      _profileTrusted = _loadedProfile is not null;
      return _profileTrusted;
    }

    var answer = MessageBox.Show(
        this,
        I18n.Format(
            "TrustMessage",
            _loadedProfile.Profile.DisplayName,
            _loadedProfile.Location),
        I18n.Get("TrustTitle"),
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No);
    if (answer != MessageBoxResult.Yes)
    {
      LogUserAction("trust_profile", UserActionOutcome.Rejected);
      AppendLog("trust", "Profile execution was not trusted.");
      _profileTrusted = false;
      return false;
    }

    try
    {
      _settings.Trust(_loadedProfile);
      LogUserAction("trust_profile", UserActionOutcome.Accepted);
      AppendLog("trust", $"Trusted {_loadedProfile.TrustIdentity}.");
      _profileTrusted = true;
      return true;
    }
    catch (Exception exception)
    {
      LogUserAction("trust_profile", UserActionOutcome.Failed);
      ShowError(I18n.Get("TrustTitle"), exception);
      _profileTrusted = false;
      return false;
    }
  }

  private async void Inspect_Click(object sender, RoutedEventArgs e)
  {
    LogUserAction("inspect_profile", UserActionOutcome.Requested);
    var outcome = await InspectAsync();
    LogUserAction("inspect_profile", outcome);
  }

  private async Task<UserActionOutcome> InspectAsync()
  {
    if (_loadedProfile is null || HasExclusiveActivity() || !_profileTrusted)
    {
      return UserActionOutcome.Rejected;
    }

    _isInspecting = true;
    _inspectCancellation = new CancellationTokenSource();
    var operationGeneration = ++_operationGeneration;
    UpdateCommandStates();
    RunSummaryText.Text = I18n.Get("InspectStart");
    AppendLog("inspect", I18n.Get("InspectStart"));
    try
    {
      _inspectionTask = EnvironmentInspector.InspectAsync(
          _loadedProfile.Profile,
          _runtime,
          CreateInspectionProgress(operationGeneration),
          _inspectCancellation.Token);
      var report = await _inspectionTask;

      foreach (var inspection in report.Tasks.Values)
      {
        var row = FindTask(inspection.TaskId);
        if (row is null)
        {
          continue;
        }

        row.Status = inspection.Compliance switch
        {
          TaskComplianceState.Satisfied => I18n.Get("SatisfiedStatus"),
          TaskComplianceState.UpgradeRequired => I18n.Get("UpgradeRequiredStatus"),
          _ => I18n.Get("NotCompliantStatus")
        };
        row.VisualState = inspection.Compliance switch
        {
          TaskComplianceState.Satisfied => TaskVisualState.Satisfied,
          TaskComplianceState.UpgradeRequired => TaskVisualState.UpgradeRequired,
          _ => TaskVisualState.NeedsAttention
        };
        row.DetectedVersion = inspection.DetectedVersion;
        row.LastResult =
            $"{I18n.Get("DetectLabel")} {I18n.Get("ExitCodeLabel")}: {inspection.DetectStep.ExitCode}";
      }

      var satisfied = report.Tasks.Values.Count(task => task.IsSatisfied);
      RunSummaryText.Text = I18n.Format("InspectCompleted", satisfied, report.Tasks.Count);
      AppendLog("inspect", RunSummaryText.Text, data: report);
      return UserActionOutcome.Completed;
    }
    catch (OperationCanceledException)
    {
      RunSummaryText.Text = I18n.Get("InspectCancelled");
      AppendLog("cancelled", RunSummaryText.Text);
      return UserActionOutcome.Cancelled;
    }
    catch (Exception exception)
    {
      ShowError(I18n.Get("InspectErrorTitle"), exception);
      return UserActionOutcome.Failed;
    }
    finally
    {
      _isInspecting = false;
      _inspectionTask = null;
      _inspectCancellation.Dispose();
      _inspectCancellation = null;
      UpdateCommandStates();
    }
  }

  private async void StartAll_Click(object sender, RoutedEventArgs e)
  {
    if (_loadedProfile is null || HasExclusiveActivity() || !_profileTrusted || !CanStartAll)
    {
      return;
    }

    var requestedTaskIds = AllTasks
        .Where(task => task.IsSelected)
        .Select(task => task.Id)
        .ToArray();
    LogUserAction("start_selected_tasks", UserActionOutcome.Requested, requestedTaskIds);
    try
    {
      var selected = OptionalTasks
          .Where(task => task.IsSelected)
          .Select(task => task.Id)
          .ToArray();
      var graph = TaskGraph.BuildForSelection(_loadedProfile.Profile, selected);
      var report = await StartRunAsync(graph);
      LogUserAction(
          "start_selected_tasks",
          ToUserActionOutcome(report),
          graph.OrderedTaskIds);
    }
    catch (Exception exception)
    {
      LogUserAction("start_selected_tasks", UserActionOutcome.Failed, requestedTaskIds);
      ShowError(I18n.Get("StartErrorTitle"), exception);
    }
  }

  private async void StartTask_Click(object sender, RoutedEventArgs e)
  {
    if (_loadedProfile is null ||
        HasExclusiveActivity() ||
        !_profileTrusted ||
        sender is not Button { Tag: TaskRow row } ||
        !row.CanStart)
    {
      return;
    }

    LogUserAction("start_task", UserActionOutcome.Requested, [row.Id]);
    try
    {
      var graph = TaskGraph.Build(_loadedProfile.Profile, [row.Id]);
      var report = await StartRunAsync(graph);
      LogUserAction("start_task", ToUserActionOutcome(report), graph.OrderedTaskIds);
    }
    catch (Exception exception)
    {
      LogUserAction("start_task", UserActionOutcome.Failed, [row.Id]);
      ShowError(I18n.Get("StartErrorTitle"), exception);
    }
  }

  private async Task<RunReport> StartRunAsync(TaskGraph graph)
  {
    if (_loadedProfile is null)
    {
      throw new InvalidOperationException("A Profile must be loaded before starting a workflow.");
    }

    AppendLog("plan", string.Join(" -> ", graph.OrderedTaskIds));
    RunSummaryText.Text = I18n.Format("RunStarted", graph.OrderedTaskIds.Count);
    var operationGeneration = ++_operationGeneration;
    _lastWorkflowRevision = -1;
    _currentRun = EnvironmentManager.StartApply(
        _loadedProfile.Profile,
        graph,
        _runtime,
        updates: CreateRunUpdates(operationGeneration));
    ApplyWorkflowSnapshot(_currentRun.Snapshot);
    UpdateCommandStates();

    RunReport report;
    try
    {
      report = await _currentRun.Completion;
      foreach (var task in report.Tasks.Values)
      {
        var row = FindTask(task.TaskId);
        if (row is not null)
        {
          row.Status = FormatOutcome(task.Outcome);
          row.VisualState = ToVisualState(task.Outcome);
          var stepResults = task.Steps.Count == 0
              ? I18n.Get("NoResult")
              : string.Join(", ", task.Steps.Select(step => $"{step.Phase}={step.ExitCode}"));
          row.LastResult = string.IsNullOrWhiteSpace(task.Error)
              ? stepResults
              : $"{stepResults}; {I18n.Get("ErrorLabel")}: {task.Error}";
        }
        AppendLog("result", $"{task.TaskId}: {task.Outcome} {task.Error}", data: task);
      }

      var succeeded = report.Tasks.Values.Count(task =>
          task.Outcome is TaskOutcome.Succeeded or TaskOutcome.NotRequired);
      RunSummaryText.Text = I18n.Format("RunCompleted", succeeded, report.Tasks.Count);
      var hasRecoverableFailure = report.Tasks.Values.Any(task =>
          task.Outcome is TaskOutcome.Failed or TaskOutcome.Blocked);
      _retryGraph = hasRecoverableFailure ? graph : null;
      AppendLog("run_summary", RunSummaryText.Text, data: report);
    }
    finally
    {
      _currentRun = null;
      UpdateCommandStates();
    }

    return report;
  }

  private void CancelAll_Click(object sender, RoutedEventArgs e)
  {
    if (_currentRun is null || !CanCancelAll)
    {
      return;
    }

    _currentRun.CancelAll();
    LogUserAction(
        "cancel_all_tasks",
        UserActionOutcome.Requested,
        _currentRun.Snapshot.Tasks.Values
            .Where(task => task.IsPlanned && task.Outcome is null)
            .Select(task => task.TaskId));
    UpdateCommandStates();
    AppendLog("cancel", I18n.Get("CancelRequested"));
  }

  private async void Retry_Click(object sender, RoutedEventArgs e)
  {
    if (_retryGraph is null || HasExclusiveActivity() || !_profileTrusted)
    {
      if (_retryGraph is null)
      {
        MessageBox.Show(this, I18n.Get("NoRetryMessage"), I18n.Get("MessageTitle"));
      }
      return;
    }

    var retryGraph = _retryGraph;
    LogUserAction("retry_plan", UserActionOutcome.Requested, retryGraph.OrderedTaskIds);
    AppendLog("retry", I18n.Get("RetryStarted"));
    var report = await StartRunAsync(retryGraph);
    LogUserAction("retry_plan", ToUserActionOutcome(report), retryGraph.OrderedTaskIds);
  }

  private void CancelTask_Click(object sender, RoutedEventArgs e)
  {
    if (_currentRun is null ||
        sender is not Button { Tag: TaskRow row } ||
        !_currentRun.Snapshot.Tasks.TryGetValue(row.Id, out var task) ||
        !task.CanCancel)
    {
      return;
    }

    _currentRun.CancelTask(row.Id);
    LogUserAction("cancel_task", UserActionOutcome.Requested, [row.Id]);
    UpdateCommandStates();
    AppendLog("cancel", $"{I18n.Get("CancelRequested")} Task: {row.Id}");
  }

  private IProgress<WorkflowProgress> CreateInspectionProgress(long operationGeneration) =>
      new Progress<WorkflowProgress>(progress =>
      {
        if (operationGeneration != _operationGeneration)
        {
          return;
        }

        var row = FindTask(progress.TaskId);
        if (row is not null)
        {
          row.Stage = progress.Stage ?? string.Empty;
          row.Percent = progress.Percent;
          if (progress.Outcome is not null)
          {
            row.Status = FormatOutcome(progress.Outcome.Value);
            row.VisualState = ToVisualState(progress.Outcome.Value);
          }
          else if (IsActivityState(progress.State))
          {
            row.Status = FormatExecutionState(progress.State);
            row.VisualState = TaskVisualState.Running;
          }
        }

        var output = progress.Message is null
            ? $"{progress.TaskId} {progress.State} {progress.Stage} {progress.Percent}%"
            : $"{progress.TaskId}/{progress.Stage} [{progress.OutputStream}] {progress.Message}";
        AppendLog("progress", output, progress);
      });

  private IProgress<WorkflowUpdate> CreateRunUpdates(long operationGeneration) =>
      new Progress<WorkflowUpdate>(update =>
      {
        if (operationGeneration != _operationGeneration ||
            update.Snapshot.Revision <= _lastWorkflowRevision)
        {
          return;
        }

        _lastWorkflowRevision = update.Snapshot.Revision;
        ApplyWorkflowSnapshot(update.Snapshot);

        if (update.Change is { } change)
        {
          var output = change.Message is null
              ? $"{change.TaskId} {change.State} {change.Stage} {change.Percent}%"
              : $"{change.TaskId}/{change.Stage} [{change.OutputStream}] {change.Message}";
          AppendLog("progress", output, change);
        }
      });

  private void ApplyWorkflowSnapshot(WorkflowSnapshot snapshot)
  {
    foreach (var task in snapshot.Tasks.Values)
    {
      var row = FindTask(task.TaskId);
      if (row is null)
      {
        continue;
      }

      row.Stage = task.Stage ?? string.Empty;
      row.Percent = task.Percent;
      row.ApplyCapabilities(task.Capabilities);
      switch (task.State)
      {
        case TaskExecutionState.NotSelected:
          row.Status = I18n.Get("NotSelectedStatus");
          row.VisualState = TaskVisualState.Pending;
          break;
        case TaskExecutionState.Pending:
        case TaskExecutionState.Ready:
          row.Status = I18n.Get("PendingStatus");
          row.VisualState = TaskVisualState.Pending;
          break;
        case TaskExecutionState.Detecting:
        case TaskExecutionState.RunningPre:
        case TaskExecutionState.Applying:
        case TaskExecutionState.RunningPost:
        case TaskExecutionState.Verifying:
        case TaskExecutionState.Running:
          row.Status = FormatExecutionState(task.State);
          row.VisualState = TaskVisualState.Running;
          break;
        case TaskExecutionState.Cancelling:
          row.Status = I18n.Get("CancellingTaskStatus");
          row.VisualState = TaskVisualState.Cancelling;
          break;
        case TaskExecutionState.Blocked:
          row.Status = I18n.Get("BlockedStatus");
          row.VisualState = TaskVisualState.Blocked;
          break;
        case TaskExecutionState.Satisfied:
          row.Status = I18n.Get("SatisfiedStatus");
          row.VisualState = TaskVisualState.Satisfied;
          break;
        case TaskExecutionState.Succeeded:
          row.Status = I18n.Get("SucceededStatus");
          row.VisualState = TaskVisualState.Succeeded;
          break;
        case TaskExecutionState.Failed:
          row.Status = I18n.Get("FailedStatus");
          row.VisualState = TaskVisualState.Failed;
          break;
        case TaskExecutionState.Cancelled:
          row.Status = I18n.Get("CancelledStatus");
          row.VisualState = TaskVisualState.Cancelled;
          break;
      }
    }

    UpdateCommandStates();
  }

  private TaskRow? FindTask(string taskId) =>
      AllTasks.FirstOrDefault(task => string.Equals(task.Id, taskId, StringComparison.Ordinal));

  private bool HasExclusiveActivity() =>
      _isCatalogLoading || _isProfileLoading || _currentRun is not null || _isInspecting;

  private void TaskRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
  {
    if (e.PropertyName == nameof(TaskRow.IsSelected) && sender is TaskRow task)
    {
      LogUserAction(
          task.IsSelected ? "select_optional_task" : "deselect_optional_task",
          UserActionOutcome.Completed,
          [task.Id]);
      UpdateCommandStates();
    }
  }

  private void ClearLoadedProfile()
  {
    foreach (var row in AllTasks.ToArray())
    {
      row.PropertyChanged -= TaskRow_PropertyChanged;
    }

    _loadedProfile = null;
    _profileTrusted = false;
    _retryGraph = null;
    _operationGeneration++;
    RequiredTasks.Clear();
    OptionalTasks.Clear();
  }

  private void UpdateCommandStates()
  {
    var workflow = _currentRun?.Snapshot;
    Actions = WorkspaceActionState.Project(new WorkspaceActionContext(
        IsCatalogLoading: _isCatalogLoading,
        IsProfileLoading: _isProfileLoading,
        IsInspecting: _isInspecting,
        WorkflowState: workflow?.State,
        HasCatalog: _catalog is not null,
        HasProfileChoice: ProfileComboBox.SelectedItem is not null,
        HasTrustedProfile: _loadedProfile is not null && _profileTrusted,
        HasRetryPlan: _retryGraph is not null));

    foreach (var task in AllTasks)
    {
      task.SetWorkspaceTaskActionsEnabled(Actions.Mode == WorkspaceMode.Ready);
    }

    CanStartAll = AllTasks.Any(task => task.IsSelected && task.CanStart);
    CanCancelAll = AllTasks.Any(task => task.CanCancel);
    OnPropertyChanged(nameof(Actions));
    OnPropertyChanged(nameof(CanStartAll));
    OnPropertyChanged(nameof(CanCancelAll));
  }

  private void ShowError(string title, Exception exception)
  {
    AppendLog("error", exception.ToString());
    MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
  }

  private async void Window_Closing(object? sender, CancelEventArgs e)
  {
    if (_allowClose)
    {
      _log.Dispose();
      return;
    }

    if (!_closePending)
    {
      LogUserAction("close_application", UserActionOutcome.Requested);
    }

    var activeRun = _currentRun?.Snapshot.IsCompleted == false ? _currentRun : null;
    var activeInspection = _inspectionTask;
    if (activeRun is null && activeInspection is null)
    {
      LogUserAction("close_application", UserActionOutcome.Completed);
      _allowClose = true;
      _log.Dispose();
      return;
    }

    e.Cancel = true;
    if (_closePending)
    {
      return;
    }

    _closePending = true;
    RunSummaryText.Text = I18n.Get("ShutdownWaiting");
    AppendLog("shutdown", RunSummaryText.Text);
    _inspectCancellation?.Cancel();
    activeRun?.CancelAll();

    try
    {
      if (activeRun is not null)
      {
        await activeRun.Completion;
      }
      if (activeInspection is not null)
      {
        await activeInspection;
      }
    }
    catch (OperationCanceledException)
    {
      // Expected while waiting for safe process-tree cancellation.
    }
    catch (Exception exception)
    {
      AppendLog("shutdown_error", exception.ToString());
    }

    _allowClose = true;
    LogUserAction("close_application", UserActionOutcome.Completed);
    _log.Dispose();
    Close();
  }

  private void OpenLogs_Click(object sender, RoutedEventArgs e)
  {
    LogUserAction("open_log_directory", UserActionOutcome.Requested);
    if (_log.Path is null)
    {
      LogUserAction("open_log_directory", UserActionOutcome.Failed);
      MessageBox.Show(
          this,
          _log.LastError ?? I18n.Get("LogsUnavailable"),
          I18n.Get("MessageTitle"));
      return;
    }

    try
    {
      Process.Start(new ProcessStartInfo
      {
        FileName = Path.GetDirectoryName(_log.Path)!,
        UseShellExecute = true
      });
      LogUserAction("open_log_directory", UserActionOutcome.Completed);
    }
    catch (Exception exception)
    {
      LogUserAction("open_log_directory", UserActionOutcome.Failed);
      ShowError(I18n.Get("MessageTitle"), exception);
    }
  }

  private static string FormatOutcome(TaskOutcome outcome) => outcome switch
  {
    TaskOutcome.Succeeded => I18n.Get("SucceededStatus"),
    TaskOutcome.NotRequired => I18n.Get("SatisfiedStatus"),
    TaskOutcome.Failed => I18n.Get("FailedStatus"),
    TaskOutcome.Cancelled => I18n.Get("CancelledStatus"),
    TaskOutcome.Blocked => I18n.Get("BlockedStatus"),
    _ => outcome.ToString()
  };

  private static TaskVisualState ToVisualState(TaskOutcome outcome) => outcome switch
  {
    TaskOutcome.Succeeded => TaskVisualState.Succeeded,
    TaskOutcome.NotRequired => TaskVisualState.Satisfied,
    TaskOutcome.Failed => TaskVisualState.Failed,
    TaskOutcome.Cancelled => TaskVisualState.Cancelled,
    TaskOutcome.Blocked => TaskVisualState.Blocked,
    _ => TaskVisualState.Pending
  };

  private static UserActionOutcome ToUserActionOutcome(RunReport report)
  {
    if (report.Tasks.Values.Any(task => task.Outcome == TaskOutcome.Cancelled))
    {
      return UserActionOutcome.Cancelled;
    }

    return report.Tasks.Values.Any(task => task.Outcome is TaskOutcome.Failed or TaskOutcome.Blocked)
        ? UserActionOutcome.Failed
        : UserActionOutcome.Completed;
  }

  private void LogUserAction(
      string operation,
      UserActionOutcome outcome,
      IEnumerable<string>? taskIds = null) =>
      _log.WriteUserAction(
          operation,
          outcome,
          _loadedProfile?.Profile.Id,
          taskIds);

  private static bool IsActivityState(TaskExecutionState state) => state is
      TaskExecutionState.Detecting or
      TaskExecutionState.RunningPre or
      TaskExecutionState.Applying or
      TaskExecutionState.RunningPost or
      TaskExecutionState.Verifying or
      TaskExecutionState.Running;

  private static string FormatExecutionState(TaskExecutionState state) => state switch
  {
    TaskExecutionState.Detecting => I18n.Get("DetectingStatus"),
    TaskExecutionState.RunningPre => I18n.Get("RunningPreStatus"),
    TaskExecutionState.Applying => I18n.Get("ApplyingStatus"),
    TaskExecutionState.RunningPost => I18n.Get("RunningPostStatus"),
    TaskExecutionState.Verifying => I18n.Get("VerifyingStatus"),
    TaskExecutionState.Running => I18n.Get("RunningStatus"),
    _ => I18n.Get("RunningStatus")
  };

  private void AppendLog(
      string category,
      string message,
      WorkflowProgress? progress = null,
      object? data = null)
  {
    var timestamp = DateTimeOffset.Now;
    LogTextBox.AppendText($"{timestamp:HH:mm:ss} [{category}] {message}{Environment.NewLine}");
    LogTextBox.ScrollToEnd();
    _log.Write(category, message, data ?? progress);
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
