using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;

namespace Wdem.Core.Reporting;

public sealed class ExecutionRunRedactor
{
  private readonly LogRedactor _redactor;

  public ExecutionRunRedactor(LogRedactor redactor)
  {
    _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
  }

  public ExecutionRun Redact(ExecutionRun run)
  {
    ArgumentNullException.ThrowIfNull(run);
    return run with
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
      ResourceResults = RedactDictionary(run.ResourceResults, (_, result) => Redact(result)),
      RestartReasons = run.RestartReasons.Select(Text).ToArray(),
      AcknowledgedRestartResourceIds = run.AcknowledgedRestartResourceIds.Select(Text)
          .ToHashSet(StringComparer.OrdinalIgnoreCase)
    };
  }

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
    Evidence = RedactDictionary(
        state.Evidence,
        (key, value) => _redactor.RedactNamedValue(key, value) ?? string.Empty),
    Error = NullableText(state.Error),
    StructuredError = state.StructuredError is null ? null : _redactor.Redact(state.StructuredError)
  };

  private ResourceGraph Redact(ResourceGraph graph) => graph with
  {
    Nodes = RedactDictionary(
        graph.Nodes,
        (_, node) => node with
        {
          Definition = Redact(node.Definition),
          RequiredBy = node.RequiredBy.Select(Text)
              .ToHashSet(StringComparer.OrdinalIgnoreCase)
        }),
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
    Parameters = RedactDictionary(
        definition.Parameters,
        (key, value) => _redactor.RedactNamedValue(key, value))
  };

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

  private string Text(string value) => _redactor.Redact(value);

  private string? NullableText(string? value) => value is null ? null : Text(value);
}
