namespace Wdem.Core.Profiles;

public sealed record LoadedProfile(
    EnvironmentProfile Profile,
    ProfileOrigin Origin,
    string Location,
    string ContentHash,
    string? SourceId = null)
{
  public bool RequiresTrust => Origin is ProfileOrigin.Remote or ProfileOrigin.Cache;

  public string TrustIdentity => $"{SourceId ?? "local"}:{ContentHash}";
}
