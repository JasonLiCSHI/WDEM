namespace Wdem.Application.Profiles;

public interface IProfileTrustStore
{
  bool IsTrusted(LoadedProfile profile);

  void Trust(LoadedProfile profile);
}
