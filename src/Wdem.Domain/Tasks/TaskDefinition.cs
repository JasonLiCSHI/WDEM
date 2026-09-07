using Wdem.Domain.Versions;
using Wdem.Domain.Workflows;

namespace Wdem.Domain.Tasks;

public sealed record TaskDefinition
{
  public TaskDefinition(
      string id,
      string displayName,
      bool required,
      IReadOnlyList<string> dependsOn,
      VersionRequirement? versionRequirement,
      string? preferredVersion,
      string? source,
      CommandDefinition detect,
      IReadOnlyList<CommandDefinition> pre,
      CommandDefinition? apply,
      IReadOnlyList<CommandDefinition> post,
      string? description = null,
      TaskWorkflowDefinition? workflow = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(id);
    ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
    ArgumentNullException.ThrowIfNull(dependsOn);
    ArgumentNullException.ThrowIfNull(detect);
    ArgumentNullException.ThrowIfNull(pre);
    ArgumentNullException.ThrowIfNull(post);

    Id = id;
    DisplayName = displayName;
    Required = required;
    DependsOn = Array.AsReadOnly(dependsOn.ToArray());
    VersionRequirement = versionRequirement;
    PreferredVersion = preferredVersion;
    Source = source;
    Detect = detect;
    Pre = Array.AsReadOnly(pre.ToArray());
    Apply = apply;
    Post = Array.AsReadOnly(post.ToArray());
    Description = description;
    Workflow = workflow;
  }

  public string Id { get; }

  public string DisplayName { get; }

  public bool Required { get; }

  public IReadOnlyList<string> DependsOn { get; }

  public VersionRequirement? VersionRequirement { get; }

  public string? PreferredVersion { get; }

  public string? Source { get; }

  public CommandDefinition Detect { get; }

  public IReadOnlyList<CommandDefinition> Pre { get; }

  public CommandDefinition? Apply { get; }

  public IReadOnlyList<CommandDefinition> Post { get; }

  public string? Description { get; }

  public TaskWorkflowDefinition? Workflow { get; }
}
