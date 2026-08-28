using Wdem.Windows;
using Xunit;

namespace Wdem.Windows.Tests;

public sealed class ProjectBoundaryTests
{
    [Fact]
    public void WindowsAssemblyIsAvailable()
    {
        Assert.NotNull(typeof(WdemWindowsAssemblyMarker).Assembly);
    }
}
