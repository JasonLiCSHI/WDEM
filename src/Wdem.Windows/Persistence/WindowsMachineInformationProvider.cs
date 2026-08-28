using System.Runtime.InteropServices;
using Wdem.Core.Runs;

namespace Wdem.Windows.Persistence;

public interface IMachineInformationProvider
{
  MachineInformation GetMachineInformation();
}

public interface IWindowsMachineInformationSource
{
  string OperatingSystem { get; }
  string Architecture { get; }
  string ComputerName { get; }
  string UserName { get; }
}

public sealed class WindowsMachineInformationProvider : IMachineInformationProvider
{
  private readonly IWindowsMachineInformationSource _source;

  public WindowsMachineInformationProvider()
      : this(new RuntimeMachineInformationSource())
  {
  }

  public WindowsMachineInformationProvider(IWindowsMachineInformationSource source)
  {
    _source = source ?? throw new ArgumentNullException(nameof(source));
  }

  public MachineInformation GetMachineInformation() => new(
      _source.OperatingSystem,
      _source.Architecture,
      _source.ComputerName,
      _source.UserName);

  private sealed class RuntimeMachineInformationSource : IWindowsMachineInformationSource
  {
    public string OperatingSystem => RuntimeInformation.OSDescription;
    public string Architecture => RuntimeInformation.OSArchitecture.ToString();
    public string ComputerName => Environment.MachineName;
    public string UserName => Environment.UserName;
  }
}
