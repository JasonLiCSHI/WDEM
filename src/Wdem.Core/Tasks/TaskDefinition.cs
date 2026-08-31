namespace Wdem.Core.Tasks;

using Wdem.Core.Workflows;

public sealed record TaskDefinition(
    string Id,
    string DisplayName,
    bool Required,
    IReadOnlyList<string> DependsOn,
    string? VersionConstraint,
    string? PreferredVersion,
    string? Source,
    CommandDefinition Detect,
    IReadOnlyList<CommandDefinition> Pre,
    CommandDefinition? Apply,
    IReadOnlyList<CommandDefinition> Post,
    string? Description = null,
    TaskWorkflowDefinition? Workflow = null);
