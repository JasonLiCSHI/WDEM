namespace Wdem.Testing;

public abstract class TemporaryDirectoryTestBase : IDisposable
{
  private string? _rootDirectory;

  protected string RootDirectory
  {
    get
    {
      _rootDirectory ??= Path.Combine(
          Path.GetTempPath(),
          "Wdem.Tests",
          GetType().Name,
          Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(_rootDirectory);
      return _rootDirectory;
    }
  }

  protected string TestPath(params ReadOnlySpan<string> segments) =>
      Path.Combine([RootDirectory, .. segments]);

  public void Dispose()
  {
    Dispose(disposing: true);
    GC.SuppressFinalize(this);
  }

  protected virtual void Dispose(bool disposing)
  {
    if (disposing && _rootDirectory is not null && Directory.Exists(_rootDirectory))
    {
      Directory.Delete(_rootDirectory, recursive: true);
    }
  }
}
