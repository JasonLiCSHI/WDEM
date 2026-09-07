using System.Diagnostics;

namespace Wdem.Windows.Processes;

internal interface IProcessTreeTerminator
{
  Task TerminateAsync(Process process, TimeSpan timeout);
}
