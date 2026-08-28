using Wdem.LegacySource.Models;

namespace Wdem.LegacySource.Interfaces;

public interface ICancellablePackageManager : IPackageManager
{
  Task InstallAsync(
      AppConfig app,
      IProgress<string>? progress,
      CancellationToken cancellationToken);
}
