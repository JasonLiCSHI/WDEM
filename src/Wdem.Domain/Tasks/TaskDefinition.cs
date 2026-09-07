using Wdem.Domain.Versions;

namespace Wdem.Domain.Tasks;

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
    string? Description = null);
