using Wdem.Core.Runs;

namespace Wdem.Core.Resources;

public static class ResourceDefinitionPresentationRedactor
{
  public static ResourceDefinition Redact(
      ResourceDefinition definition,
      LogRedactor redactor)
  {
    ArgumentNullException.ThrowIfNull(definition);
    ArgumentNullException.ThrowIfNull(redactor);
    return definition with
    {
      DisplayName = RedactNullable(definition.DisplayName, redactor),
      Description = RedactNullable(definition.Description, redactor)
    };
  }

  private static string? RedactNullable(string? value, LogRedactor redactor) =>
      value is null ? null : redactor.Redact(value);
}
