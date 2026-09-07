using System.Text.RegularExpressions;

namespace Wdem.Domain.Versions;

public sealed class VersionRequirement
{
  private static readonly Regex TermPattern = new(
      @"(?<operator>>=|<=|>|<|=)?\s*(?<version>\d+(?:\.(?:\d+|[xX*]))*)",
      RegexOptions.CultureInvariant);

  private readonly IReadOnlyList<RequirementTerm> _terms;

  private VersionRequirement(string expression, IReadOnlyList<RequirementTerm> terms)
  {
    Expression = expression;
    _terms = terms;
  }

  public string Expression { get; }

  public static VersionRequirement Parse(string expression)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(expression);

    var terms = new List<RequirementTerm>();
    var position = 0;
    foreach (Match match in TermPattern.Matches(expression))
    {
      if (!string.IsNullOrWhiteSpace(expression[position..match.Index]))
      {
        throw Invalid(expression);
      }

      terms.Add(CreateTerm(
          match.Groups["operator"].Value,
          match.Groups["version"].Value,
          expression));
      position = match.Index + match.Length;
    }

    if (terms.Count == 0 || !string.IsNullOrWhiteSpace(expression[position..]))
    {
      throw Invalid(expression);
    }

    return new VersionRequirement(expression, terms);
  }

  public ComplianceResult Evaluate(string installedVersion)
  {
    if (!SoftwareVersion.TryParse(installedVersion, out var candidate))
    {
      return new ComplianceResult(ComplianceStatus.VersionMismatch, null, this);
    }

    if (_terms.All(term => term.IsSatisfiedBy(candidate)))
    {
      return new ComplianceResult(ComplianceStatus.Satisfied, candidate, this);
    }

    var status = _terms.Any(term => term.IsBelowMinimum(candidate))
        ? ComplianceStatus.UpgradeRequired
        : ComplianceStatus.VersionMismatch;
    return new ComplianceResult(status, candidate, this);
  }

  public bool IsSatisfiedBy(string version) =>
      Evaluate(version).Status == ComplianceStatus.Satisfied;

  public bool IsBelowMinimum(string version) =>
      Evaluate(version).Status == ComplianceStatus.UpgradeRequired;

  public override string ToString() => Expression;

  private static RequirementTerm CreateTerm(string op, string value, string expression)
  {
    var parts = value.Split('.');
    var wildcardIndex = Array.FindIndex(
        parts,
        part => part.Equals("x", StringComparison.OrdinalIgnoreCase) || part == "*");

    if (wildcardIndex >= 0)
    {
      if (!string.IsNullOrEmpty(op) || wildcardIndex == 0 ||
          parts.Skip(wildcardIndex).Any(part =>
              !part.Equals("x", StringComparison.OrdinalIgnoreCase) && part != "*"))
      {
        throw new FormatException($"Invalid wildcard version requirement '{expression}'.");
      }

      var prefix = parts.Take(wildcardIndex).Select(ParsePart).ToArray();
      return new RequirementTerm(ComparisonOperator.Prefix, SoftwareVersion.Parse(string.Join('.', prefix)));
    }

    var target = SoftwareVersion.Parse(value);
    var comparison = op switch
    {
      "" or "=" => ComparisonOperator.Equal,
      ">=" => ComparisonOperator.GreaterThanOrEqual,
      ">" => ComparisonOperator.GreaterThan,
      "<=" => ComparisonOperator.LessThanOrEqual,
      "<" => ComparisonOperator.LessThan,
      _ => throw new FormatException($"Invalid version operator '{op}'.")
    };

    return new RequirementTerm(comparison, target);
  }

  private static int ParsePart(string value) =>
      int.TryParse(value, out var parsed)
          ? parsed
          : throw new FormatException($"Invalid version part '{value}'.");

  private static FormatException Invalid(string expression) =>
      new($"Invalid version requirement '{expression}'.");

  private enum ComparisonOperator
  {
    Equal,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Prefix
  }

  private sealed record RequirementTerm(ComparisonOperator Operator, SoftwareVersion Target)
  {
    public bool IsSatisfiedBy(SoftwareVersion candidate)
    {
      if (Operator == ComparisonOperator.Prefix)
      {
        return Enumerable.Range(0, TargetPartCount)
            .All(index => candidate.PartAt(index) == Target.PartAt(index));
      }

      var comparison = candidate.CompareTo(Target);
      return Operator switch
      {
        ComparisonOperator.Equal => comparison == 0,
        ComparisonOperator.GreaterThan => comparison > 0,
        ComparisonOperator.GreaterThanOrEqual => comparison >= 0,
        ComparisonOperator.LessThan => comparison < 0,
        ComparisonOperator.LessThanOrEqual => comparison <= 0,
        _ => false
      };
    }

    public bool IsBelowMinimum(SoftwareVersion candidate)
    {
      var comparison = candidate.CompareTo(Target);
      return Operator switch
      {
        ComparisonOperator.GreaterThan => comparison <= 0,
        ComparisonOperator.GreaterThanOrEqual => comparison < 0,
        _ => false
      };
    }

    private int TargetPartCount => Target.ToString().Split('.').Length;
  }
}
