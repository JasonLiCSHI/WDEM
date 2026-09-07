namespace Wdem.Testing;

public static class RepositoryLocator
{
  public static string FindRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      if (File.Exists(Path.Combine(directory.FullName, "Wdem.slnx")))
      {
        return directory.FullName;
      }

      directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Unable to locate the WDEM repository root.");
  }
}
