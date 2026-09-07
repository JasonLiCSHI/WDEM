namespace Wdem.Core.Tasks;

using Wdem.Core.Workflows;
using Wdem.Domain.Tasks;
using Wdem.Domain.Versions;

public sealed record TaskDefinition(
    string Id,
    string DisplayName,
    bool Required,
    IReadOnlyList<string> DependsOn,
    VersionRequirement? VersionRequirement,
    string? PreferredVersion,
    string? Source,
    CommandDefinition Detect,
    IReadOnlyList<CommandDefinition> Pre,
    CommandDefinition? Apply,
    IReadOnlyList<CommandDefinition> Post,
    string? Description = null,
    TaskWorkflowDefinition? Workflow = null);
