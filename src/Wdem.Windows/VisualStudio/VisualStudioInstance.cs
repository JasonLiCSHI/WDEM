namespace Wdem.Windows.VisualStudio;

public sealed record VisualStudioInstance
{
  public required string InstanceId { get; init; }
  public required string InstallationPath { get; init; }
  public required string ProductId { get; init; }
  public required string ProductPath { get; init; }
  public required string ProductDisplayVersion { get; init; }
  public required string InstallationVersion { get; init; }
  public required string ChannelId { get; init; }
  public required string Edition { get; init; }
  public required bool IsComplete { get; init; }
  public required bool IsLaunchable { get; init; }
  public IReadOnlySet<string> Workloads { get; init; } = new HashSet<string>(
      StringComparer.OrdinalIgnoreCase);
  public IReadOnlySet<string> Components { get; init; } = new HashSet<string>(
      StringComparer.OrdinalIgnoreCase);
}
