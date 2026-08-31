namespace Wdem.Core.Profiles;

public sealed record ProfileCatalogEntry(
    string Id,
    string Version,
    string DisplayName,
    string? Description,
    ProfileOrigin Origin,
    string Location,
    string SourceId,
    string SourceDisplayName)
{
  public bool RequiresTrust => Origin is ProfileOrigin.Remote or ProfileOrigin.Cache;
}
