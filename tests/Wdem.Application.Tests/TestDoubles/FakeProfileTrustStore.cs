using Wdem.Application.Profiles;

namespace Wdem.Application.Tests.TestDoubles;

public sealed class FakeProfileTrustStore(bool trusted = true) : IProfileTrustStore
{
  private readonly HashSet<string> _trustedProfiles = [];

  public bool IsTrusted(LoadedProfile profile) =>
      !profile.RequiresTrust || trusted || _trustedProfiles.Contains(profile.TrustIdentity);

  public void Trust(LoadedProfile profile) => _trustedProfiles.Add(profile.TrustIdentity);
}
