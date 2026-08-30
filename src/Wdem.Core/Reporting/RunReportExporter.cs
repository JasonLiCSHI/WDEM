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

  public RunReportExporter(LogRedactor redactor)
  {
    _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
  }

  public LogRedactor Redactor => _redactor;

  public string ExportJson(ExecutionRun run)
  {
    ArgumentNullException.ThrowIfNull(run);
    return JsonSerializer.Serialize(CreateDocument(Redact(run)), JsonOptions);
  }

  public string ExportMarkdown(ExecutionRun run)
  {
    ArgumentNullException.ThrowIfNull(run);
    return CreateMarkdown(Redact(run));
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

    return path;
  }

  private ReportDocument CreateDocument(ExecutionRun run) => new(
      run.RunId,
      run.Mode,
      run.ProfileSourcePath,
      run.ProfileId,
      run.ProfileVersion,
      run.SelectedOptionalResourceIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      run.StartedAtUtc,
      run.EndedAtUtc,
      run.State,
      run.Outcome,
      run.RetriedFromRunId,
      run.RecoveredFromRunId,
      run.Machine,
      run.Graph,
      run.Plan,
      new SortedDictionary<string, ResourceResult>(
          run.ResourceResults.ToDictionary(),
          StringComparer.OrdinalIgnoreCase),
      run.RestartRequirements,
      run.RestartReasons,
      run.AcknowledgedRestartResourceIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      BlockedIds(run),
      UnexecutedIds(run));

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

  private ExecutionRun Redact(ExecutionRun run) => run with
  {
    ProfileSourcePath = Text(run.ProfileSourcePath),
    ProfileId = Text(run.ProfileId),
    ProfileVersion = Text(run.ProfileVersion),
    SelectedOptionalResourceIds = run.SelectedOptionalResourceIds.Select(Text)
        .ToHashSet(StringComparer.OrdinalIgnoreCase),
    Machine = run.Machine with
    {
      OperatingSystem = Text(run.Machine.OperatingSystem),
      Architecture = Text(run.Machine.Architecture),
      ComputerName = Text(run.Machine.ComputerName),
      UserName = Text(run.Machine.UserName)
    },
    Graph = run.Graph is null ? null : Redact(run.Graph),
    Plan = run.Plan is null ? null : Redact(run.Plan),
    ResourceResults = run.ResourceResults.ToDictionary(
        pair => Text(pair.Key),
        pair => Redact(pair.Value),
        StringComparer.OrdinalIgnoreCase),
    RestartReasons = run.RestartReasons.Select(Text).ToArray(),
    AcknowledgedRestartResourceIds = run.AcknowledgedRestartResourceIds.Select(Text)
        .ToHashSet(StringComparer.OrdinalIgnoreCase)
  };

  private ResourceResult Redact(ResourceResult result) => result with
  {
    ResourceId = Text(result.ResourceId),
    DetectedBefore = result.DetectedBefore is null ? null : Redact(result.DetectedBefore),
    DetectedAfter = result.DetectedAfter is null ? null : Redact(result.DetectedAfter),
    Message = NullableText(result.Message),
    Error = result.Error is null ? null : _redactor.Redact(result.Error),
    StepResults = result.StepResults.Select(Redact).ToArray()
  };

  private StepResult Redact(StepResult result) => result with
  {
    StepId = Text(result.StepId),
    Name = Text(result.Name),
    Error = result.Error is null ? null : _redactor.Redact(result.Error)
  };

  private DetectedState Redact(DetectedState state) => state with
  {
    ResourceId = Text(state.ResourceId),
    Version = NullableText(state.Version),
    ConfigurationHash = NullableText(state.ConfigurationHash),
    Evidence = state.Evidence.ToDictionary(
        pair => Text(pair.Key),
        pair => _redactor.RedactNamedValue(pair.Key, pair.Value) ?? string.Empty,
        StringComparer.OrdinalIgnoreCase),
    Error = NullableText(state.Error),
    StructuredError = state.StructuredError is null ? null : _redactor.Redact(state.StructuredError)
  };

  private ResourceGraph Redact(ResourceGraph graph) => graph with
  {
    Nodes = graph.Nodes.ToDictionary(
        pair => Text(pair.Key),
        pair => pair.Value with
        {
          Definition = Redact(pair.Value.Definition),
          RequiredBy = pair.Value.RequiredBy.Select(Text)
              .ToHashSet(StringComparer.OrdinalIgnoreCase)
        },
        StringComparer.OrdinalIgnoreCase),
    TopologicalLayers = graph.TopologicalLayers.Select(layer => layer with
    {
      ResourceIds = layer.ResourceIds.Select(Text).ToArray()
    }).ToArray()
  };

  private ExecutionPlan Redact(ExecutionPlan plan) => plan with
  {
    Fingerprint = Text(plan.Fingerprint),
    ProfileId = Text(plan.ProfileId),
    ProfileVersion = Text(plan.ProfileVersion),
    Layers = plan.Layers.Select(layer => layer with
    {
      ResourceIds = layer.ResourceIds.Select(Text).ToArray()
    }).ToArray(),
    Resources = plan.Resources.Select(resource => resource with
    {
      Definition = Redact(resource.Definition),
      Dependencies = resource.Dependencies.Select(Text).ToArray(),
      ResourcePlan = Redact(resource.ResourcePlan),
      Reason = NullableText(resource.Reason),
      BlockedBy = resource.BlockedBy.Select(Text).ToArray(),
      Diagnostics = resource.Diagnostics.Select(_redactor.Redact).ToArray()
    }).ToArray(),
    Errors = plan.Errors.Select(_redactor.Redact).ToArray()
  };

  private ResourcePlan Redact(ResourcePlan plan) => plan with
  {
    ResourceId = Text(plan.ResourceId),
    ResourceType = Text(plan.ResourceType),
    ProviderName = Text(plan.ProviderName),
    DesiredStateFingerprint = Text(plan.DesiredStateFingerprint),
    ExecutionPreconditionFingerprint = NullableText(plan.ExecutionPreconditionFingerprint),
    Steps = plan.Steps.Select(step => step with
    {
      Id = Text(step.Id),
      Description = Text(step.Description),
      Reason = NullableText(step.Reason)
    }).ToArray(),
    Error = NullableText(plan.Error),
    StructuredErrors = plan.StructuredErrors.Select(_redactor.Redact).ToArray()
  };

  private ResourceDefinition Redact(ResourceDefinition definition) => definition with
  {
    Id = Text(definition.Id),
    Type = Text(definition.Type),
    Provider = Text(definition.Provider),
    DisplayName = NullableText(definition.DisplayName),
    VersionConstraint = NullableText(definition.VersionConstraint),
    PreferredVersion = NullableText(definition.PreferredVersion),
    Dependencies = definition.Dependencies.Select(Text).ToArray(),
    Parameters = definition.Parameters.ToDictionary(
        pair => Text(pair.Key),
        pair => _redactor.RedactNamedValue(pair.Key, pair.Value),
        StringComparer.OrdinalIgnoreCase)
  };

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
      if (File.Exists(path))
      {
        File.Replace(temporaryPath, path, destinationBackupFileName: null);
      }
      else
      {
        File.Move(temporaryPath, path);
      }
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        File.Delete(temporaryPath);
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
      MachineInformation Machine,
      ResourceGraph? Graph,
      ExecutionPlan? Plan,
      IReadOnlyDictionary<string, ResourceResult> ResourceResults,
      IReadOnlyList<RestartPolicy> RestartRequirements,
      IReadOnlyList<string> RestartReasons,
      IReadOnlyList<string> AcknowledgedRestartResourceIds,
      IReadOnlyList<string> BlockedResourceIds,
      IReadOnlyList<string> UnexecutedResourceIds);
}
