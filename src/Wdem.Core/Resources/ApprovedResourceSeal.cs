using Wdem.Core.Providers;

namespace Wdem.Core.Resources;

public sealed record ApprovedResourceSeal(
    ResourceDefinition Definition,
    ResourcePlan Plan);
