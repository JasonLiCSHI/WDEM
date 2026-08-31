using System.Text.RegularExpressions;

namespace Wdem.Core.Versions;

public sealed class VersionConstraint
{
  private static readonly Regex TermPattern = new(
      @"(?<operator>>=|<=|>|<|=)?\s*(?<version>\d+(?:\.(?:\d+|[xX*]))*)",
      RegexOptions.CultureInvariant);

  private readonly IReadOnlyList<ConstraintTerm> _terms;

  private VersionConstraint(string expression, IReadOnlyList<ConstraintTerm> terms)
  {
    Expression = expression;
    _terms = terms;
  }

  public string Expression { get; }

  public static VersionConstraint Parse(string expression)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(expression);

    var terms = new List<ConstraintTerm>();
    var position = 0;
    foreach (Match match in TermPattern.Matches(expression))
    {
      if (!string.IsNullOrWhiteSpace(expression[position..match.Index]))
      {
        throw new FormatException($"Invalid version constraint '{expression}'.");
      }

      var value = match.Groups["version"].Value;
      var op = match.Groups["operator"].Value;
      terms.Add(CreateTerm(op, value, expression));
      position = match.Index + match.Length;
    }

    if (terms.Count == 0 || !string.IsNullOrWhiteSpace(expression[position..]))
    {
      throw new FormatException($"Invalid version constraint '{expression}'.");
    }

    return new VersionConstraint(expression, terms);
  }

  public bool IsSatisfiedBy(string version)
  {
    if (!ProductVersion.TryParse(version, out var candidate))
    {
      return false;
    }

    return _terms.All(term => term.IsSatisfiedBy(candidate));
  }

  public bool IsBelowMinimum(string version)
  {
    if (!ProductVersion.TryParse(version, out var candidate))
    {
      return false;
    }

    return _terms.Any(term => term.IsBelowMinimum(candidate));
  }

  private static ConstraintTerm CreateTerm(string op, string value, string expression)
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
        throw new FormatException($"Invalid wildcard version constraint '{expression}'.");
      }

      var prefix = parts.Take(wildcardIndex).Select(ParsePart).ToArray();
      return new ConstraintTerm(ComparisonOperator.Prefix, new ProductVersion(prefix));
    }

    var target = new ProductVersion(parts.Select(ParsePart).ToArray());
    var comparison = op switch
    {
      "" or "=" => ComparisonOperator.Equal,
      ">=" => ComparisonOperator.GreaterThanOrEqual,
      ">" => ComparisonOperator.GreaterThan,
      "<=" => ComparisonOperator.LessThanOrEqual,
      "<" => ComparisonOperator.LessThan,
      _ => throw new FormatException($"Invalid version operator '{op}'.")
    };

    return new ConstraintTerm(comparison, target);
  }

  private static int ParsePart(string value) =>
      int.TryParse(value, out var parsed)
          ? parsed
          : throw new FormatException($"Invalid version part '{value}'.");

  private enum ComparisonOperator
  {
    Equal,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Prefix
  }

  private sealed record ConstraintTerm(ComparisonOperator Operator, ProductVersion Target)
  {
    public bool IsSatisfiedBy(ProductVersion candidate)
    {
      if (Operator == ComparisonOperator.Prefix)
      {
        return Target.Parts
            .Select((part, index) => candidate.PartAt(index) == part)
            .All(matches => matches);
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

    public bool IsBelowMinimum(ProductVersion candidate)
    {
      var comparison = candidate.CompareTo(Target);
      return Operator switch
      {
        ComparisonOperator.GreaterThan => comparison <= 0,
        ComparisonOperator.GreaterThanOrEqual => comparison < 0,
        _ => false
      };
    }
  }

  private readonly record struct ProductVersion(IReadOnlyList<int> Parts) : IComparable<ProductVersion>
  {
    public static bool TryParse(string value, out ProductVersion version)
    {
      var match = Regex.Match(value ?? string.Empty, @"\d+(?:\.\d+)*");
      if (!match.Success)
      {
        version = default;
        return false;
      }

      var parts = new List<int>();
      foreach (var part in match.Value.Split('.'))
      {
        if (!int.TryParse(part, out var parsed))
        {
          version = default;
          return false;
        }
        parts.Add(parsed);
      }

      version = new ProductVersion(parts);
      return true;
    }

    public int PartAt(int index) => index < Parts.Count ? Parts[index] : 0;

    public int CompareTo(ProductVersion other)
    {
      for (var index = 0; index < Math.Max(Parts.Count, other.Parts.Count); index++)
      {
        var comparison = PartAt(index).CompareTo(other.PartAt(index));
        if (comparison != 0)
        {
          return comparison;
        }
      }
      return 0;
    }
  }
}
