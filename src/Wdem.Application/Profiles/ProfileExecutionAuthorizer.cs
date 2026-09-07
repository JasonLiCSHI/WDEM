namespace Wdem.Application.Profiles;

public sealed class ProfileExecutionAuthorizer(IProfileTrustStore trustStore)
{
  public void EnsureTrusted(LoadedProfile profile)
  {
    ArgumentNullException.ThrowIfNull(profile);
    if (!trustStore.IsTrusted(profile))
    {
      throw new UntrustedProfileException(profile);
    }
  }
}
