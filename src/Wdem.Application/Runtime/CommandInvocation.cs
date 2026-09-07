using Wdem.Domain.Tasks;

namespace Wdem.Application.Runtime;

public sealed record CommandInvocation(
    string TaskId,
    string Phase,
    CommandDefinition Command,
    string? Source,
    string? PreferredVersion);
