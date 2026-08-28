using Wdem.Core.Execution;

namespace Wdem.Core.Graph;

public sealed record ResourceGraphLayer(int Index, IReadOnlyList<string> ResourceIds);

public sealed record ResourceGraph(
    IReadOnlyDictionary<string, ResolvedResource> Nodes,
    IReadOnlyList<ResourceGraphLayer> TopologicalLayers);

public sealed record ResourceGraphBuildResult(
    ResourceGraph? Graph,
    IReadOnlyList<StructuredError> Errors);
