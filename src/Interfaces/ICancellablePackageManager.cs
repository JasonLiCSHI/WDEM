using WinHome.Models;

namespace WinHome.Interfaces;

public interface ICancellablePackageManager : IPackageManager
{
  Task InstallAsync(
      AppConfig app,
      IProgress<string>? progress,
      CancellationToken cancellationToken);
}
