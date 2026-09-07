namespace Wdem.Application.Profiles;

public sealed class UntrustedProfileException : InvalidOperationException
{
  public UntrustedProfileException(LoadedProfile profile)
      : base(
          $"Profile '{profile.Profile.Id}' from '{profile.Origin}' must be trusted " +
          $"before its commands can run (SHA-256: {profile.ContentHash}).")
  {
  }
}
