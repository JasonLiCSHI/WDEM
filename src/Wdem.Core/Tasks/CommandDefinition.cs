namespace Wdem.Core.Tasks;

public sealed record CommandDefinition(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? VersionPattern = null,
    string? DisplayName = null);
