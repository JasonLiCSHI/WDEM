namespace Wdem.Windows.Persistence;

public sealed class WdemDataPaths
{
  public WdemDataPaths()
      : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
  {
  }

  public WdemDataPaths(string localApplicationData)
  {
    if (string.IsNullOrWhiteSpace(localApplicationData))
    {
      throw new ArgumentException(
          "The local application data directory is required.",
          nameof(localApplicationData));
    }

    Root = Path.Combine(localApplicationData, "WDEM");
    RunsDirectory = Path.Combine(Root, "runs");
  }

  public string Root { get; }
  public string RunsDirectory { get; }
}
