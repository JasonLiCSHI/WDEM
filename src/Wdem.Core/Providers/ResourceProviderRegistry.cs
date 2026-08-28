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

  public ResourceProviderRegistry(IEnumerable<IResourceProvider> providers)
  {
    ArgumentNullException.ThrowIfNull(providers);

    _providers = new Dictionary<string, IResourceProvider>(StringComparer.OrdinalIgnoreCase);
    foreach (var provider in providers)
    {
      ArgumentNullException.ThrowIfNull(provider);

      var key = CreateKey(provider.ResourceType, provider.ProviderName);
      if (!_providers.TryAdd(key, provider))
      {
        throw new InvalidOperationException(
            $"Provider '{provider.ProviderName}' is already registered for resource type '{provider.ResourceType}'.");
      }
    }
  }

  public IReadOnlyCollection<IResourceProvider> Providers => _providers.Values;

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
