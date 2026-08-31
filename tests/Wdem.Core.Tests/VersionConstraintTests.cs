using Wdem.Core.Versions;
using Xunit;

namespace Wdem.Core.Tests;

public sealed class VersionConstraintTests
{
  [Theory]
  [InlineData("= 18.3.2", "18.3.2", true)]
  [InlineData("= 18.3.2", "18.3.3", false)]
  [InlineData("18.3.x", "18.3.9", true)]
  [InlineData("18.3.x", "18.4.0", false)]
  [InlineData(">= 18.3 < 19.0", "18.9.1", true)]
  [InlineData(">= 18.3 < 19.0", "19.0.0", false)]
  [InlineData(">= 2.50", "2.50.1.windows.1", true)]
  [InlineData(">= 2.50", "2.49.9", false)]
  public void IsSatisfiedBy_SupportsMvpExpressions(
      string expression,
      string version,
      bool expected)
  {
    var constraint = VersionConstraint.Parse(expression);

    Assert.Equal(expected, constraint.IsSatisfiedBy(version));
  }

  [Theory]
  [InlineData(">= 2.50", "2.49.9", true)]
  [InlineData(">= 2.50", "2.50.0", false)]
  [InlineData("> 2.50", "2.50.0", true)]
  [InlineData(">= 18.3 < 19.0", "18.2.9", true)]
  [InlineData(">= 18.3 < 19.0", "19.0.0", false)]
  [InlineData("= 2.50", "2.49.0", false)]
  public void IsBelowMinimum_OnlyClassifiesFailedLowerBounds(
      string expression,
      string version,
      bool expected)
  {
    var constraint = VersionConstraint.Parse(expression);

    Assert.Equal(expected, constraint.IsBelowMinimum(version));
  }
}
