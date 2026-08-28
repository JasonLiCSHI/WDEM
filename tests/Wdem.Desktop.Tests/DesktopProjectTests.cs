using Wdem.Desktop;
using Xunit;

namespace Wdem.Desktop.Tests;

public sealed class DesktopProjectTests
{
    [Fact]
    public void DesktopAssemblyIsAvailable()
    {
        Assert.NotNull(typeof(App).Assembly);
    }
}
