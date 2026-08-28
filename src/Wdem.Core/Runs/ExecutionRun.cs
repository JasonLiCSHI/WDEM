using System.Collections.Frozen;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Resources;

namespace Wdem.Core.Runs;

public sealed record ExecutionRun
{
  private IReadOnlySet<string> _selectedOptionalResourceIds =
      Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);
  private IReadOnlyDictionary<string, ResourceResult> _resourceResults =
      new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase);
  private IReadOnlyList<RestartPolicy> _restartRequirements = [];
  private IReadOnlyList<string> _restartReasons = [];
  private IReadOnlySet<string> _acknowledgedRestartResourceIds =
      Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

  public required Guid RunId { get; init; }
  public required RunMode Mode { get; init; }
  public required string ProfileSourcePath { get; init; }
  public required string ProfileId { get; init; }
  public required string ProfileVersion { get; init; }
  public required IReadOnlySet<string> SelectedOptionalResourceIds
  {
    get => _selectedOptionalResourceIds;
    init => _selectedOptionalResourceIds = (value ?? throw new ArgumentNullException(nameof(value)))
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
  }

  public required DateTimeOffset StartedAtUtc { get; init; }
  public DateTimeOffset? EndedAtUtc { get; init; }
  public required ExecutionState State { get; init; }
  public ExecutionOutcome? Outcome { get; init; }
  public Guid? RetriedFromRunId { get; init; }
  public required MachineInformation Machine { get; init; }
  public ResourceGraph? Graph { get; init; }
  public ExecutionPlan? Plan { get; init; }
  public IReadOnlyDictionary<string, ResourceResult> ResourceResults
  {
    get => _resourceResults;
    init => _resourceResults = (value ?? throw new ArgumentNullException(nameof(value)))
        .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
  }

  public IReadOnlyList<RestartPolicy> RestartRequirements
  {
    get => _restartRequirements;
    init => _restartRequirements = Array.AsReadOnly(
        (value ?? throw new ArgumentNullException(nameof(value))).ToArray());
  }

  public IReadOnlyList<string> RestartReasons
  {
    get => _restartReasons;
    init => _restartReasons = Array.AsReadOnly(
        (value ?? throw new ArgumentNullException(nameof(value))).ToArray());
  }

  public IReadOnlySet<string> AcknowledgedRestartResourceIds
  {
    get => _acknowledgedRestartResourceIds;
    init => _acknowledgedRestartResourceIds = (
        value ?? throw new ArgumentNullException(nameof(value)))
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
  }
}

public sealed record MachineInformation(
    string OperatingSystem,
    string Architecture,
    string ComputerName,
    string UserName);
