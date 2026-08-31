namespace Wdem.Core.Runs;

public sealed record TaskCapabilities(
    bool CanStart,
    bool CanCancel,
    bool CanSelect);
