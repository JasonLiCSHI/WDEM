using System.Threading.Tasks;
using Wdem.LegacySource.Models;

namespace Wdem.LegacySource.Interfaces
{
  /// <summary>Main orchestrator for Wdem.LegacySource configuration application.</summary>
  public interface IEngine
  {
    Task RunAsync(
        Configuration config,
        bool dryRun,
        string? profileName = null,
        bool debug = false,
        bool diff = false,
        bool forceReapply = false,
        bool continueOnError = false,
        bool autoInstallApps = false,
        CancellationToken cancellationToken = default);
  }
}
