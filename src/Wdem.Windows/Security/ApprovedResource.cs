using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Windows.Security;

public sealed record ApprovedResource(
    ResourceDefinition Definition,
    ResourcePlan Plan,
    string Fingerprint);

public sealed record ApprovedResourceClaim(
    ResourceDefinition Definition,
    ResourcePlan Plan,
    ResourcePlan Segment,
    string Fingerprint);

public interface IApprovedResourceStore
{
  Task<ApprovedResourceClaim?> ClaimApprovedResourceAsync(
      Guid runId,
      string resourceId,
      string planFingerprint,
      CancellationToken cancellationToken);

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

internal sealed class ApprovedResourceStoreException(
    StructuredError error,
    Exception innerException) : Exception(error.Summary, innerException)
{
  public StructuredError Error { get; } = error;
}
