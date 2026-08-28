using Wdem.Core.Resources;

namespace Wdem.Core.Graph;

public sealed record ResolvedResource(
    ResourceDefinition Definition,
    ResourceOrigin Origin,
    IReadOnlySet<string> RequiredBy);
