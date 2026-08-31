using Wdem.Core.Runs;

namespace Wdem.Core.Runtime;

public sealed record CommandOutput(
    WorkflowOutputStream Stream,
    string Message);
