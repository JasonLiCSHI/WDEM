namespace Wdem.Domain.Tasks;

public sealed record CommandDefinition
{
  public CommandDefinition(
      string Executable,
      IReadOnlyList<string> Arguments,
      string? VersionPattern = null,
      string? DisplayName = null,
      IReadOnlyList<int>? MissingExitCodes = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(Executable);
    ArgumentNullException.ThrowIfNull(Arguments);

    this.Executable = Executable;
    this.Arguments = Array.AsReadOnly(Arguments.ToArray());
    this.VersionPattern = VersionPattern;
    this.DisplayName = DisplayName;
    this.MissingExitCodes = MissingExitCodes is null
        ? null
        : Array.AsReadOnly(MissingExitCodes.ToArray());
  }

  public string Executable { get; }

  public IReadOnlyList<string> Arguments { get; }

  public string? VersionPattern { get; }

  public string? DisplayName { get; }

  public IReadOnlyList<int>? MissingExitCodes { get; }
}
