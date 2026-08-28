using Wdem.LegacySource.Models;

namespace Wdem.LegacySource.Interfaces
{
  public interface IConfigDriftService
  {
    Task<List<ConfigDriftResult>> DetectDriftAsync(string backupFile);
  }
}
