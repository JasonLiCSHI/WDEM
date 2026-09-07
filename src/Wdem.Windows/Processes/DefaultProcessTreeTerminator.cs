using System.Diagnostics;

namespace Wdem.Windows.Processes;

internal sealed class DefaultProcessTreeTerminator : IProcessTreeTerminator
{
  public static DefaultProcessTreeTerminator Instance { get; } = new();

  private DefaultProcessTreeTerminator()
  {
  }

  public async Task TerminateAsync(Process process, TimeSpan timeout)
  {
    ArgumentNullException.ThrowIfNull(process);

    if (process.HasExited)
    {
      return;
    }

    try
    {
      process.Kill(entireProcessTree: true);
    }
    catch (Exception) when (process.HasExited)
    {
      return;
    }
    catch (Exception exception)
    {
      throw new ProcessTerminationException(
          process.Id,
          "The process tree could not be terminated after cancellation.",
          exception);
    }

    try
    {
      await process.WaitForExitAsync().WaitAsync(timeout);
    }
    catch (TimeoutException exception)
    {
      throw new ProcessTerminationException(
          process.Id,
          $"The process tree did not exit within {timeout.TotalSeconds:0} seconds after cancellation.",
          exception);
    }

    if (!process.HasExited)
    {
      throw new ProcessTerminationException(
          process.Id,
          "The process tree remained active after cancellation.");
    }
  }
}
