namespace Wdem.Core.Profiles;

public sealed record ProfileSelection(
    IReadOnlySet<string>? SelectedOptionalResourceIds = null);
