using Wdem.Windows.Persistence;
using Xunit;

namespace Wdem.Windows.Tests.Persistence;

public sealed class WdemDataPathsTests
{
  [Fact]
  public void DefaultRoot_UsesWdemLocalAppDataDirectory()
  {
    var paths = new WdemDataPaths(@"C:\Users\Test\AppData\Local");

    Assert.Equal(@"C:\Users\Test\AppData\Local\WDEM", paths.Root);
    Assert.Equal(@"C:\Users\Test\AppData\Local\WDEM\runs", paths.RunsDirectory);
  }

  [Fact]
  public void Constructor_RejectsMissingLocalApplicationDataPath()
  {
    Assert.Throws<ArgumentException>(() => new WdemDataPaths("  "));
  }
}
