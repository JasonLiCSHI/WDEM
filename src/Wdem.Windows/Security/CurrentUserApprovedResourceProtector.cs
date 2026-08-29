using System.Security.Cryptography;

namespace Wdem.Windows.Security;

public sealed class CurrentUserApprovedResourceProtector : IApprovedResourceProtector
{
  public byte[] Protect(byte[] plaintext, byte[] entropy)
  {
    ArgumentNullException.ThrowIfNull(plaintext);
    ArgumentNullException.ThrowIfNull(entropy);
    return ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
  }

  public byte[] Unprotect(byte[] protectedData, byte[] entropy)
  {
    ArgumentNullException.ThrowIfNull(protectedData);
    ArgumentNullException.ThrowIfNull(entropy);
    return ProtectedData.Unprotect(protectedData, entropy, DataProtectionScope.CurrentUser);
  }
}
