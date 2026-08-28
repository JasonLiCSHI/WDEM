using Wdem.Core.Execution;

namespace Wdem.Core.Profiles;

public sealed record ProfileLoadResult
{
  public DeveloperProfile? Profile { get; init; }
  public IReadOnlyList<StructuredError> Errors { get; init; } = Array.Empty<StructuredError>();
  public required string SourcePath { get; init; }
  public bool IsValid => Profile is not null && Errors.Count == 0;
}

public sealed record ProfileExpansionResult
{
  public DeveloperProfile? Profile { get; init; }
  public IReadOnlyList<StructuredError> Errors { get; init; } = Array.Empty<StructuredError>();
  public bool IsValid => Profile is not null && Errors.Count == 0;
}
