using System.Text.RegularExpressions;

namespace Wdem.Domain.Tasks;

public sealed record TaskId
{
  private static readonly Regex ValidId = new(
      "^[A-Za-z0-9._-]+$",
      RegexOptions.CultureInvariant);

  private TaskId(string value)
  {
    Value = value;
  }

  public string Value { get; }

  public static TaskId Parse(string value)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(value);
    return ValidId.IsMatch(value)
        ? new TaskId(value)
        : throw new FormatException($"Invalid task id '{value}'.");
  }

  public override string ToString() => Value;
}
