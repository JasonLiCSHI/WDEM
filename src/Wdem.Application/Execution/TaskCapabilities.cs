namespace Wdem.Application.Execution;

public sealed record TaskCapabilities(
    bool CanStart,
    bool CanCancel,
    bool CanSelect);
