using Wdem.LegacySource.Models;

namespace Wdem.LegacySource.Interfaces;

public interface IConfigBackupService
{
  Task BackupAsync(
      string provider,
      Configuration config,
      string sourcePath,
      string output);

  Task<(string Provider, object? Settings)> RestoreAsync(
      string input);
}
