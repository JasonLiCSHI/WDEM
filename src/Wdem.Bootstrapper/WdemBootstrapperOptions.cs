namespace Wdem.Bootstrapper;

public sealed record WdemBootstrapperOptions
{
  public WdemBootstrapperOptions(string component)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(component);
    Component = component;
  }

  public string Component { get; }

  public string? ApplicationDirectory { get; init; }

  public string? SettingsPath { get; init; }

  public string? CacheDirectory { get; init; }

  public string? LogDirectory { get; init; }
}
