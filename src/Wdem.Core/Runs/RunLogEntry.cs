using Wdem.Core.Execution;
using Wdem.Core.Providers;

namespace Wdem.Core.Runs;

public sealed record RunLogEntry(
    long Sequence,
    DateTimeOffset TimestampUtc,
    ProviderLogLevel Level,
    string? ResourceId,
    string? StepId,
    string Message,
    StructuredError? Error = null);
