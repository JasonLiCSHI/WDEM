using Wdem.Core.Providers;

namespace Wdem.Windows.Security;

public sealed record ElevatedHostResponse(
    string Type,
    ProviderProgress? Progress = null,
    ResourceApplyResult? Result = null);
