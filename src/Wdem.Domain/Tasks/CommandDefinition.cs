namespace Wdem.Domain.Tasks;

public sealed record CommandDefinition(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? VersionPattern = null,
    string? DisplayName = null,
    IReadOnlyList<int>? MissingExitCodes = null);
