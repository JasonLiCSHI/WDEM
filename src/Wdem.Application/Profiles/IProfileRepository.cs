using Wdem.Domain.Profiles;

namespace Wdem.Application.Profiles;

public interface IProfileRepository
{
  ProfileSourceDefinition Source { get; }

  Task<IReadOnlyList<ProfileCatalogEntry>> ListAsync(
      CancellationToken cancellationToken = default);

  Task<LoadedProfile> LoadAsync(
      string profileId,
      CancellationToken cancellationToken = default);
}
