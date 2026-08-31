using Wdem.Core.Tasks;

namespace Wdem.Core.Runtime;

public sealed record CommandInvocation(
    string TaskId,
    string Phase,
    CommandDefinition Command,
    string? Source,
    string? PreferredVersion);
