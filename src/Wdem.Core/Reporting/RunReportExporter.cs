using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;

namespace Wdem.Core.Reporting;

public sealed class RunReportExporter : IRunReportExporter
{
  private static readonly UTF8Encoding Utf8WithoutBom = new(false);
  private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
  private readonly LogRedactor _redactor;
  private readonly ExecutionRunRedactor _runRedactor;

  public RunReportExporter(LogRedactor redactor)
  {
    _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    _runRedactor = new ExecutionRunRedactor(redactor);
  }

  public LogRedactor Redactor => _redactor;

  public string ExportJson(ExecutionRun run)
  {
    ArgumentNullException.ThrowIfNull(run);
    return JsonSerializer.Serialize(CreateDocument(run), JsonOptions);
  }

  public string ExportMarkdown(ExecutionRun run)
  {
    ArgumentNullException.ThrowIfNull(run);
    return CreateMarkdown(_runRedactor.Redact(run));
  }

  public async Task ExportAsync(
      ExecutionRun run,
      string filePath,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(run);
    string path = ValidateFilePath(filePath);
    string extension = Path.GetExtension(path);
    string content = extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
        ? ExportJson(run)
        : extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            ? ExportMarkdown(run)
            : throw new ArgumentException(
                "Report file must use the .json or .md extension.",
                nameof(filePath));
    await WriteAtomicallyAsync(path, content, cancellationToken).ConfigureAwait(false);
  }

  public static string ValidateFilePath(string filePath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
    string path = Path.GetFullPath(filePath);
    string extension = Path.GetExtension(path);
    if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
        !extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
    {
      throw new ArgumentException(
          "Report file must use the .json or .md extension.",
          nameof(filePath));
    }

    if (Directory.Exists(path))
    {
      throw new IOException("Report destination must be a file, not a directory.");
    }

    string directory = Path.GetDirectoryName(path)
        ?? throw new DirectoryNotFoundException("Report destination has no parent directory.");
    if (!Directory.Exists(directory))
    {
      throw new DirectoryNotFoundException("Report destination directory does not exist.");
    }

    string probePath = Path.Combine(directory, $".wdem-report-{Guid.NewGuid():N}.tmp");
    try
    {
      using var probe = new FileStream(
          probePath,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.None,
          1,
          FileOptions.DeleteOnClose);
    }
    finally
    {
      File.Delete(probePath);
    }

    return path;
  }

  private ReportDocument CreateDocument(ExecutionRun run) => new(
      run.RunId,
      run.Mode,
      Text(run.ProfileSourcePath),
      Text(run.ProfileId),
      Text(run.ProfileVersion),
      run.SelectedOptionalResourceIds.Order(StringComparer.OrdinalIgnoreCase).Select(Text).ToArray(),
      run.StartedAtUtc,
      run.EndedAtUtc,
      run.State,
      run.Outcome,
      run.RetriedFromRunId,
      run.RecoveredFromRunId,
      CreateMachine(run.Machine),
      run.Graph is null ? null : CreateGraph(run.Graph),
      run.Plan is null ? null : CreatePlan(run.Plan),
      RedactDictionary(run.ResourceResults, (_, result) => CreateResourceResult(result)),
      run.RestartRequirements,
      run.RestartReasons.Select(Text).ToArray(),
      run.AcknowledgedRestartResourceIds.Order(StringComparer.OrdinalIgnoreCase).Select(Text)
          .ToArray(),
      BlockedIds(run).Select(Text).ToArray(),
      UnexecutedIds(run).Select(Text).ToArray());

  private ReportMachine CreateMachine(MachineInformation machine) => new(
      Text(machine.OperatingSystem),
      Text(machine.Architecture),
      Text(machine.ComputerName),
      Text(machine.UserName));

  private ReportGraph CreateGraph(ResourceGraph graph) => new(
      RedactDictionary(graph.Nodes, (_, node) => new ReportGraphNode(
          CreateDefinition(node.Definition),
          node.Origin,
          node.RequiredBy.Order(StringComparer.OrdinalIgnoreCase).Select(Text).ToArray())),
      graph.TopologicalLayers.OrderBy(layer => layer.Index).Select(CreateLayer).ToArray());

  private ReportGraphLayer CreateLayer(ResourceGraphLayer layer) => new(
      layer.Index,
      layer.ResourceIds.Select(Text).ToArray());

  private ReportPlan CreatePlan(ExecutionPlan plan) => new(
      plan.PlanId,
      Text(plan.Fingerprint),
      Text(plan.ProfileId),
      Text(plan.ProfileVersion),
      plan.Layers.Select(CreateLayer).ToArray(),
      plan.Resources.Select(CreatePlannedResource).ToArray(),
      plan.IsExecutable,
      plan.Errors.Select(CreateError).ToArray());

  private ReportPlannedResource CreatePlannedResource(PlannedResource resource) => new(
      CreateDefinition(resource.Definition),
      resource.Origin,
      resource.Dependencies.Select(Text).ToArray(),
      CreateResourcePlan(resource.ResourcePlan),
      resource.Status,
      resource.Risk,
      resource.RequiresElevation,
      resource.IsDestructive,
      resource.RestartPolicy,
      NullableText(resource.Reason),
      resource.BlockedBy.Select(Text).ToArray(),
      resource.Diagnostics.Select(CreateError).ToArray());

  private ReportResourceDefinition CreateDefinition(ResourceDefinition definition) => new(
      Text(definition.Id),
      Text(definition.Type),
      Text(definition.Provider),
      NullableText(definition.DisplayName),
      NullableText(definition.VersionConstraint),
      NullableText(definition.PreferredVersion),
      definition.Dependencies.Select(Text).ToArray(),
      RedactDictionary(
          definition.Parameters,
          (key, value) => _redactor.RedactNamedValue(key, value)),
      definition.PrivilegeRequirement,
      definition.RestartPolicy);

  private ReportResourcePlan CreateResourcePlan(ResourcePlan plan) => new(
      Text(plan.ResourceId),
      Text(plan.ResourceType),
      Text(plan.ProviderName),
      Text(plan.DesiredStateFingerprint),
      NullableText(plan.ExecutionPreconditionFingerprint),
      plan.Compliance,
      plan.IsExecutable,
      plan.Steps.Select(CreatePlanStep).ToArray(),
      NullableText(plan.Error),
      plan.StructuredErrors.Select(CreateError).ToArray());

  private ReportPlanStep CreatePlanStep(PlanStep step) => new(
      Text(step.Id),
      Text(step.Description),
      step.Action,
      step.PrivilegeRequirement,
      step.RestartPolicy,
      step.IsDestructive,
      NullableText(step.Reason));

  private ReportResourceResult CreateResourceResult(ResourceResult result) => new(
      Text(result.ResourceId),
      result.State,
      result.Outcome,
      result.FinalCompliance,
      result.DetectedBefore is null ? null : CreateDetectedState(result.DetectedBefore),
      result.DetectedAfter is null ? null : CreateDetectedState(result.DetectedAfter),
      result.Progress,
      NullableText(result.Message),
      result.StartedAtUtc,
      result.EndedAtUtc,
      result.Error is null ? null : CreateError(result.Error),
      result.RestartRequirement,
      result.StepResults.Select(CreateStepResult).ToArray());

  private ReportDetectedState CreateDetectedState(DetectedState state) => new(
      Text(state.ResourceId),
      state.Outcome,
      state.Exists,
      NullableText(state.Version),
      state.InstalledVersions.Select(version => new ReportSemanticVersion(
          version.Major,
          version.Minor,
          version.Patch,
          version.Revision)).ToArray(),
      NullableText(state.ConfigurationHash),
      state.DetectedAtUtc,
      RedactDictionary(
          state.Evidence,
          (key, value) => _redactor.RedactNamedValue(key, value) ?? string.Empty),
      NullableText(state.Error),
      state.StructuredError is null ? null : CreateError(state.StructuredError));

  private ReportStepResult CreateStepResult(StepResult result) => new(
      Text(result.StepId),
      Text(result.Name),
      result.State,
      result.Outcome,
      result.Progress,
      result.FirstLogSequence,
      result.LastLogSequence,
      result.ProcessExitCode,
      result.ProcessSucceeded,
      result.StartedAtUtc,
      result.EndedAtUtc,
      result.Error is null ? null : CreateError(result.Error));

  private ReportStructuredError CreateError(StructuredError error) => new(
      error.Code,
      Text(error.Summary),
      Text(error.Detail),
      NullableText(error.ResourceId),
      NullableText(error.StepId),
      error.ProcessExitCode,
      NullableText(error.LogLocation),
      NullableText(error.SuggestedAction),
      error.IsRetryable,
      NullableText(error.UnderlyingExceptionType),
      NullableText(error.UnderlyingExceptionMessage));

  private string CreateMarkdown(ExecutionRun run)
  {
    ResourceResult[] results = run.ResourceResults.Values
        .OrderBy(result => result.ResourceId, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var markdown = new StringBuilder();
    markdown.AppendLine("# WDEM Run Report").AppendLine();
    Field("Profile", $"{run.ProfileId} {run.ProfileVersion}");
    Field("Profile source", run.ProfileSourcePath);
    Field("Run ID", run.RunId.ToString("D"));
    Field("Mode", run.Mode.ToString());
    Field("Started (UTC)", run.StartedAtUtc.ToString("O"));
    Field("Ended (UTC)", run.EndedAtUtc?.ToString("O") ?? "Not completed");
    Field("State", run.State.ToString());
    Field("Outcome", run.Outcome?.ToString() ?? "Unknown");
    markdown.AppendLine();

    markdown.AppendLine("## Machine").AppendLine();
    Field("Operating system", run.Machine.OperatingSystem);
    Field("Architecture", run.Machine.Architecture);
    Field("Computer", run.Machine.ComputerName);
    Field("User", run.Machine.UserName);
    markdown.AppendLine();

    markdown.AppendLine("## Selected options").AppendLine();
    AppendList(run.SelectedOptionalResourceIds.Order(StringComparer.OrdinalIgnoreCase));
    markdown.AppendLine();

    markdown.AppendLine("## Summary").AppendLine();
    Field("Satisfied", Count(results, outcome: ExecutionOutcome.NotRequired));
    Field("Succeeded", Count(results, outcome: ExecutionOutcome.Succeeded));
    Field("Failed", Count(results, outcome: ExecutionOutcome.Failed));
    Field("Blocked", results.Count(result => result.State == ExecutionState.Blocked));
    Field(
        "Cancelled / Skipped",
        results.Count(result =>
            result.State != ExecutionState.Blocked &&
            result.Outcome is ExecutionOutcome.Cancelled or ExecutionOutcome.Skipped));
    markdown.AppendLine();

    markdown.AppendLine("## Graph and plan").AppendLine();
    Field("Graph resources", run.Graph?.Nodes.Count.ToString() ?? "Unavailable");
    Field("Graph layers", run.Graph?.TopologicalLayers.Count.ToString() ?? "Unavailable");
    Field("Plan ID", run.Plan?.PlanId.ToString("D") ?? "Unavailable");
    Field("Plan fingerprint", run.Plan?.Fingerprint ?? "Unavailable");
    Field("Plan executable", run.Plan?.IsExecutable.ToString() ?? "Unavailable");
    Field("Planned resources", run.Plan?.Resources.Count.ToString() ?? "Unavailable");
    Field(
        "Planned steps",
        run.Plan?.Resources.Sum(resource => resource.ResourcePlan.Steps.Count).ToString()
            ?? "Unavailable");
    if (run.Graph is not null)
    {
      foreach (ResourceGraphLayer layer in run.Graph.TopologicalLayers.OrderBy(layer => layer.Index))
      {
        Field(
            $"Layer {layer.Index}",
            string.Join(", ", layer.ResourceIds.Order(StringComparer.OrdinalIgnoreCase)));
      }
    }

    if (run.Plan is not null)
    {
      AppendErrors("Plan errors", run.Plan.Errors);
      foreach (PlannedResource resource in run.Plan.Resources.OrderBy(
                   resource => resource.Definition.Id,
                   StringComparer.OrdinalIgnoreCase))
      {
        AppendErrors(
            $"Planned resource {resource.Definition.Id} diagnostics",
            resource.Diagnostics);
        AppendErrors(
            $"Resource plan {resource.Definition.Id} errors",
            resource.ResourcePlan.StructuredErrors);
      }
    }

    markdown.AppendLine();
    markdown.AppendLine("## Resource results").AppendLine();
    foreach (ResourceResult result in results)
    {
      markdown.Append("### ").AppendLine(result.ResourceId).AppendLine();
      Field("State", result.State.ToString());
      Field("Outcome", result.Outcome?.ToString() ?? "Unknown");
      Field("Compliance", result.FinalCompliance?.ToString() ?? "Unknown");
      Field("Detected before", DetectedVersions(result.DetectedBefore));
      Field("Detected after", DetectedVersions(result.DetectedAfter));
      Field("Restart", RestartText(result.RestartRequirement));
      Field("Started (UTC)", result.StartedAtUtc?.ToString("O") ?? "Not started");
      Field("Ended (UTC)", result.EndedAtUtc?.ToString("O") ?? "Not completed");
      if (!string.IsNullOrWhiteSpace(result.Message))
      {
        Field("Message", result.Message);
      }

      AppendError(result.Error);
      AppendError(result.DetectedBefore?.StructuredError);
      AppendError(result.DetectedAfter?.StructuredError);
      foreach (StepResult step in result.StepResults.OrderBy(step => step.StepId))
      {
        markdown.Append("- Step `").Append(step.StepId).Append("` — ")
            .Append(step.Name).Append(": ")
            .Append(step.State).Append(" / ")
            .Append(step.Outcome?.ToString() ?? "Unknown")
            .Append(", exit code: ")
            .AppendLine(step.ProcessExitCode?.ToString() ?? "n/a");
        AppendError(step.Error, "  ");
      }

      markdown.AppendLine();
    }

    markdown.AppendLine("## Blocked and unexecuted").AppendLine();
    Field("Blocked IDs", JoinOrNone(BlockedIds(run)));
    Field("Unexecuted IDs", JoinOrNone(UnexecutedIds(run)));
    markdown.AppendLine();

    markdown.AppendLine("## Restart requirements").AppendLine();
    if (run.RestartRequirements.Count == 0 &&
        results.All(result => result.RestartRequirement == RestartPolicy.NoRestart))
    {
      markdown.AppendLine("- No restart required.");
    }
    else
    {
      foreach (RestartPolicy requirement in run.RestartRequirements.Distinct())
      {
        markdown.Append("- ").AppendLine(RestartText(requirement));
      }

      foreach (ResourceResult result in results.Where(result =>
                   result.RestartRequirement != RestartPolicy.NoRestart))
      {
        markdown.Append("- ").Append(result.ResourceId).Append(": ")
            .AppendLine(RestartText(result.RestartRequirement));
      }
    }

    foreach (string reason in run.RestartReasons)
    {
      markdown.Append("- Reason: ").AppendLine(reason);
    }

    return markdown.ToString();

    void Field(string name, object value) => markdown.Append("- ").Append(name).Append(": ")
        .AppendLine(value.ToString());

    void AppendList(IEnumerable<string> values)
    {
      string[] items = values.ToArray();
      if (items.Length == 0)
      {
        markdown.AppendLine("- None");
        return;
      }

      foreach (string value in items)
      {
        markdown.Append("- ").AppendLine(value);
      }
    }

    void AppendError(StructuredError? error, string prefix = "")
    {
      if (error is null)
      {
        return;
      }

      markdown.Append(prefix).Append("- Error code: ").AppendLine(error.Code.ToString());
      markdown.Append(prefix).Append("- Error summary: ").AppendLine(error.Summary);
      markdown.Append(prefix).Append("- Error details: ").AppendLine(error.Detail);
      if (!string.IsNullOrWhiteSpace(error.SuggestedAction))
      {
        markdown.Append(prefix).Append("- Suggested action: ")
            .AppendLine(error.SuggestedAction);
      }

      if (error.ProcessExitCode is int exitCode)
      {
        markdown.Append(prefix).Append("- Error exit code: ").AppendLine(exitCode.ToString());
      }
    }

    void AppendErrors(string heading, IEnumerable<StructuredError> errors)
    {
      StructuredError[] materialized = errors.ToArray();
      if (materialized.Length == 0)
      {
        return;
      }

      markdown.Append("### ").AppendLine(heading).AppendLine();
      foreach (StructuredError error in materialized)
      {
        AppendError(error);
      }

      markdown.AppendLine();
    }
  }

  private IReadOnlyDictionary<string, TResult> RedactDictionary<TValue, TResult>(
      IReadOnlyDictionary<string, TValue> values,
      Func<string, TValue, TResult> redactValue)
  {
    var result = new SortedDictionary<string, TResult>(StringComparer.OrdinalIgnoreCase);
    foreach (KeyValuePair<string, TValue> pair in values
                 .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(pair => pair.Key, StringComparer.Ordinal))
    {
      string baseKey = Text(pair.Key);
      string key = baseKey;
      for (int suffix = 2; result.ContainsKey(key); suffix++)
      {
        key = $"{baseKey} ({suffix})";
      }

      result.Add(key, redactValue(pair.Key, pair.Value));
    }

    return result;
  }

  private static int Count(
      IEnumerable<ResourceResult> results,
      ExecutionOutcome outcome) =>
      results.Count(result => result.Outcome == outcome);

  private static string DetectedVersions(DetectedState? state)
  {
    if (state is null)
    {
      return "Unknown";
    }

    string? primary = string.IsNullOrWhiteSpace(state.Version) ? null : state.Version;
    string installed = string.Join(
        ", ",
        state.InstalledVersions.Select(FormatVersion).Distinct(StringComparer.Ordinal));
    if (primary is not null && installed.Length > 0)
    {
      return $"{primary}; installed versions: {installed}";
    }

    if (primary is not null)
    {
      return primary;
    }

    return installed.Length == 0
        ? (state.Exists ? "Present (version unknown)" : "Not present")
        : installed;
  }

  private static string FormatVersion(Versions.SemanticVersion version) => version.Revision == 0
      ? $"{version.Major}.{version.Minor}.{version.Patch}"
      : $"{version.Major}.{version.Minor}.{version.Patch}.{version.Revision}";

  private static string RestartText(RestartPolicy policy) => policy switch
  {
    RestartPolicy.RestartRequired => "Restart required",
    RestartPolicy.RestartRecommended => "Restart recommended",
    _ => "No restart"
  };

  private static string[] BlockedIds(ExecutionRun run) => run.ResourceResults.Values
      .Where(result => result.State == ExecutionState.Blocked)
      .Select(result => result.ResourceId)
      .Order(StringComparer.OrdinalIgnoreCase)
      .ToArray();

  private static string[] UnexecutedIds(ExecutionRun run) => run.ResourceResults.Values
      .Where(result => result.State is ExecutionState.Pending
          or ExecutionState.Ready
          or ExecutionState.Blocked ||
          result.Outcome is ExecutionOutcome.Cancelled
              or ExecutionOutcome.Skipped)
      .Select(result => result.ResourceId)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Order(StringComparer.OrdinalIgnoreCase)
      .ToArray();

  private static string JoinOrNone(IReadOnlyList<string> values) =>
      values.Count == 0 ? "None" : string.Join(", ", values);

  private string Text(string value) => _redactor.Redact(value);

  private string? NullableText(string? value) => value is null ? null : Text(value);

  private static async Task WriteAtomicallyAsync(
      string path,
      string content,
      CancellationToken cancellationToken)
  {
    string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
    try
    {
      await using (var stream = new FileStream(
          temporaryPath,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.None,
          4096,
          FileOptions.Asynchronous | FileOptions.WriteThrough))
      await using (var writer = new StreamWriter(stream, Utf8WithoutBom))
      {
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
      }

      cancellationToken.ThrowIfCancellationRequested();
      await MoveAtomicallyAsync(temporaryPath, path, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        File.Delete(temporaryPath);
      }
    }
  }

  private static async Task MoveAtomicallyAsync(
      string temporaryPath,
      string destinationPath,
      CancellationToken cancellationToken)
  {
    const int maximumAttempts = 10;
    for (var attempt = 1; ; attempt++)
    {
      try
      {
        File.Move(temporaryPath, destinationPath, overwrite: true);
        return;
      }
      catch (Exception exception) when (
          exception is IOException or UnauthorizedAccessException && attempt < maximumAttempts)
      {
        await Task.Delay(TimeSpan.FromMilliseconds(attempt * 10), cancellationToken)
            .ConfigureAwait(false);
      }
    }
  }

  private static JsonSerializerOptions CreateJsonOptions()
  {
    var options = new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      WriteIndented = true
    };
    options.Converters.Add(new JsonStringEnumConverter(
        JsonNamingPolicy.CamelCase,
        allowIntegerValues: false));
    return options;
  }

  private sealed record ReportDocument(
      Guid RunId,
      RunMode Mode,
      string ProfileSourcePath,
      string ProfileId,
      string ProfileVersion,
      IReadOnlyList<string> SelectedOptionalResourceIds,
      DateTimeOffset StartedAtUtc,
      DateTimeOffset? EndedAtUtc,
      ExecutionState State,
      ExecutionOutcome? Outcome,
      Guid? RetriedFromRunId,
      Guid? RecoveredFromRunId,
      ReportMachine Machine,
      ReportGraph? Graph,
      ReportPlan? Plan,
      IReadOnlyDictionary<string, ReportResourceResult> ResourceResults,
      IReadOnlyList<RestartPolicy> RestartRequirements,
      IReadOnlyList<string> RestartReasons,
      IReadOnlyList<string> AcknowledgedRestartResourceIds,
      IReadOnlyList<string> BlockedResourceIds,
      IReadOnlyList<string> UnexecutedResourceIds);

  private sealed record ReportMachine(
      string OperatingSystem,
      string Architecture,
      string ComputerName,
      string UserName);

  private sealed record ReportGraph(
      IReadOnlyDictionary<string, ReportGraphNode> Nodes,
      IReadOnlyList<ReportGraphLayer> TopologicalLayers);

  private sealed record ReportGraphNode(
      ReportResourceDefinition Definition,
      ResourceOrigin Origin,
      IReadOnlyList<string> RequiredBy);

  private sealed record ReportGraphLayer(int Index, IReadOnlyList<string> ResourceIds);

  private sealed record ReportPlan(
      Guid PlanId,
      string Fingerprint,
      string ProfileId,
      string ProfileVersion,
      IReadOnlyList<ReportGraphLayer> Layers,
      IReadOnlyList<ReportPlannedResource> Resources,
      bool IsExecutable,
      IReadOnlyList<ReportStructuredError> Errors);

  private sealed record ReportPlannedResource(
      ReportResourceDefinition Definition,
      ResourceOrigin Origin,
      IReadOnlyList<string> Dependencies,
      ReportResourcePlan ResourcePlan,
      PlannedResourceStatus Status,
      PlanRisk Risk,
      bool RequiresElevation,
      bool IsDestructive,
      RestartPolicy RestartPolicy,
      string? Reason,
      IReadOnlyList<string> BlockedBy,
      IReadOnlyList<ReportStructuredError> Diagnostics);

  private sealed record ReportResourceDefinition(
      string Id,
      string Type,
      string Provider,
      string? DisplayName,
      string? VersionConstraint,
      string? PreferredVersion,
      IReadOnlyList<string> Dependencies,
      IReadOnlyDictionary<string, string?> Parameters,
      PrivilegeRequirement PrivilegeRequirement,
      RestartPolicy RestartPolicy);

  private sealed record ReportResourcePlan(
      string ResourceId,
      string ResourceType,
      string ProviderName,
      string DesiredStateFingerprint,
      string? ExecutionPreconditionFingerprint,
      ComplianceStatus Compliance,
      bool IsExecutable,
      IReadOnlyList<ReportPlanStep> Steps,
      string? Error,
      IReadOnlyList<ReportStructuredError> StructuredErrors);

  private sealed record ReportPlanStep(
      string Id,
      string Description,
      PlanAction Action,
      PrivilegeRequirement PrivilegeRequirement,
      RestartPolicy RestartPolicy,
      bool IsDestructive,
      string? Reason);

  private sealed record ReportResourceResult(
      string ResourceId,
      ExecutionState State,
      ExecutionOutcome? Outcome,
      ComplianceStatus? FinalCompliance,
      ReportDetectedState? DetectedBefore,
      ReportDetectedState? DetectedAfter,
      double Progress,
      string? Message,
      DateTimeOffset? StartedAtUtc,
      DateTimeOffset? EndedAtUtc,
      ReportStructuredError? Error,
      RestartPolicy RestartRequirement,
      IReadOnlyList<ReportStepResult> StepResults);

  private sealed record ReportDetectedState(
      string ResourceId,
      DetectionOutcome Outcome,
      bool Exists,
      string? Version,
      IReadOnlyList<ReportSemanticVersion> InstalledVersions,
      string? ConfigurationHash,
      DateTimeOffset DetectedAtUtc,
      IReadOnlyDictionary<string, string> Evidence,
      string? Error,
      ReportStructuredError? StructuredError);

  private sealed record ReportSemanticVersion(int Major, int Minor, int Patch, int Revision);

  private sealed record ReportStepResult(
      string StepId,
      string Name,
      ExecutionState State,
      ExecutionOutcome? Outcome,
      double Progress,
      long FirstLogSequence,
      long LastLogSequence,
      int? ProcessExitCode,
      bool? ProcessSucceeded,
      DateTimeOffset? StartedAtUtc,
      DateTimeOffset? EndedAtUtc,
      ReportStructuredError? Error);

  private sealed record ReportStructuredError(
      WdemErrorCode Code,
      string Summary,
      string Detail,
      string? ResourceId,
      string? StepId,
      int? ProcessExitCode,
      string? LogLocation,
      string? SuggestedAction,
      bool IsRetryable,
      string? UnderlyingExceptionType,
      string? UnderlyingExceptionMessage);
}
