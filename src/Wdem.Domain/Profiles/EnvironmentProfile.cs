using Wdem.Domain.Tasks;

namespace Wdem.Domain.Profiles;

public sealed record EnvironmentProfile(
    string Id,
    string Version,
    string DisplayName,
    string? Description,
    IReadOnlyDictionary<string, TaskDefinition> Tasks,
    int SchemaVersion = 1);
