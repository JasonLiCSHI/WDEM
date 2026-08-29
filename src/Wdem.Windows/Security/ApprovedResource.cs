using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Windows.Security;

public sealed record ApprovedResource(
    ResourceDefinition Definition,
    ResourcePlan Plan,
    string Fingerprint);

public interface IApprovedResourceStore
{
  Task<ApprovedResource?> GetApprovedResourceAsync(
      Guid runId,
      string resourceId,
      CancellationToken cancellationToken);
}

public interface IApprovedResourceProtector
{
  byte[] Protect(byte[] plaintext, byte[] entropy);
  byte[] Unprotect(byte[] protectedData, byte[] entropy);
}
