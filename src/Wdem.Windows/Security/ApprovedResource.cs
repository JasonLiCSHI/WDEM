using Wdem.Core.Execution;
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

internal sealed class ApprovedResourceAccessException(
    StructuredError error,
    Exception innerException) : Exception(error.Summary, innerException)
{
  public StructuredError Error { get; } = error;
}
