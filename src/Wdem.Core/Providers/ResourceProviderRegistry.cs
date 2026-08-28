namespace Wdem.Core.Providers;

public interface IResourceProviderRegistry
{
  IReadOnlyCollection<IResourceProvider> Providers { get; }

  bool TryGet(string resourceType, string providerName, out IResourceProvider? provider);

  IResourceProvider GetRequired(string resourceType, string providerName);
}

public sealed class ResourceProviderRegistry : IResourceProviderRegistry
{
  private readonly Dictionary<string, IResourceProvider> _providers;
  private readonly IReadOnlyCollection<IResourceProvider> _providerSnapshot;

  public ResourceProviderRegistry(IEnumerable<IResourceProvider> providers)
  {
    ArgumentNullException.ThrowIfNull(providers);

    _providers = new Dictionary<string, IResourceProvider>(StringComparer.OrdinalIgnoreCase);
    foreach (var provider in providers)
    {
      ArgumentNullException.ThrowIfNull(provider);

      var resourceType = provider.ResourceType;
      var providerName = provider.ProviderName;
      var capabilities = provider.Capabilities;
      var key = CreateKey(resourceType, providerName);
      ArgumentNullException.ThrowIfNull(capabilities);
      if (capabilities.MaxConcurrentOperations <= 0)
      {
        throw new ArgumentOutOfRangeException(
            nameof(provider),
            capabilities.MaxConcurrentOperations,
            "Provider maximum concurrency must be greater than zero.");
      }

      if (!_providers.TryAdd(key, provider))
      {
        throw new InvalidOperationException(
            $"Provider '{providerName}' is already registered for resource type '{resourceType}'.");
      }
    }

    _providerSnapshot = Array.AsReadOnly(_providers.Values.ToArray());
  }

  public IReadOnlyCollection<IResourceProvider> Providers => _providerSnapshot;

  public bool TryGet(string resourceType, string providerName, out IResourceProvider? provider) =>
      _providers.TryGetValue(CreateKey(resourceType, providerName), out provider);

  public IResourceProvider GetRequired(string resourceType, string providerName)
  {
    if (TryGet(resourceType, providerName, out var provider) && provider is not null)
    {
      return provider;
    }

    throw new KeyNotFoundException(
        $"No provider named '{providerName}' is registered for resource type '{resourceType}'.");
  }

  private static string CreateKey(string resourceType, string providerName)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
    ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
    return $"{resourceType}\0{providerName}";
  }
}
