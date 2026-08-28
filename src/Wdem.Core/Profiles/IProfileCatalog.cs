namespace Wdem.Core.Profiles;

public interface IProfileCatalog
{
  Task<ProfileLoadResult> LoadAsync(
      string id,
      CancellationToken cancellationToken = default);

  Task<ProfileLoadResult> LoadFileAsync(
      string path,
      CancellationToken cancellationToken = default);

  Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
      CancellationToken cancellationToken = default);
}
