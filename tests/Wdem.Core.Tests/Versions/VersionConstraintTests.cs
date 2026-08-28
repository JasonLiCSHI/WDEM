using System.Globalization;
using Wdem.Core.Versions;
using Xunit;

namespace Wdem.Core.Tests.Versions;

public sealed class VersionConstraintTests
{
  [Theory]
  [InlineData("18.3.2", "= 18.3.2", true)]
  [InlineData("18.3.5", "18.3.x", true)]
  [InlineData("18.4.0", "18.3.x", false)]
  [InlineData("18.5.0", ">= 18.3 < 19.0", true)]
  [InlineData("2.50.0", ">= 2.50", true)]
  [InlineData("2.49.9", ">= 2.50", false)]
  [InlineData("10.0.7", "10.0.x", true)]
  public void IsSatisfiedBy_EvaluatesSupportedExpressions(
      string installed,
      string expression,
      bool expected)
  {
    Assert.True(SemanticVersion.TryParse(installed, out var version));

    Assert.Equal(expected, VersionConstraint.Parse(expression).IsSatisfiedBy(version));
  }

  [Theory]
  [InlineData("0", 0, 0, 0, 0)]
  [InlineData("18.3", 18, 3, 0, 0)]
  [InlineData("18.3.2", 18, 3, 2, 0)]
  [InlineData("18.3.2.7", 18, 3, 2, 7)]
  public void TryParse_NormalizesOneToFourNumericSegments(
      string text,
      int major,
      int minor,
      int patch,
      int revision)
  {
    Assert.True(SemanticVersion.TryParse(text, out var version));
    Assert.Equal(new SemanticVersion(major, minor, patch, revision), version);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("release-candidate")]
  [InlineData("1..2")]
  [InlineData("-1.2.3")]
  [InlineData("1.2.3.4.5")]
  [InlineData("2147483648.0")]
  [InlineData(" 1.2.3")]
  [InlineData("1.2.3 ")]
  [InlineData("1e1.2")]
  public void TryParse_RejectsMalformedOrOverflowingVersions(string? text)
  {
    Assert.False(SemanticVersion.TryParse(text, out _));
  }

  [Fact]
  public void CompareTo_UsesAllFourSegmentsInOrder()
  {
    Assert.True(new SemanticVersion(2, 0, 0, 1).CompareTo(new SemanticVersion(2, 0, 0)) > 0);
    Assert.True(new SemanticVersion(2, 1, 0).CompareTo(new SemanticVersion(2, 0, 99, 99)) > 0);
  }

  [Theory]
  [InlineData("")]
  [InlineData(" ")]
  [InlineData("18.3.*")]
  [InlineData("18.x.2")]
  [InlineData("x.3.2")]
  [InlineData("18.3.x.1")]
  [InlineData("= release-candidate")]
  [InlineData("> 18.3")]
  [InlineData(">= 18.3 garbage")]
  [InlineData(">= 18.3 <= 19.0")]
  [InlineData(">= 18.3 <")]
  [InlineData(">= 18.3 < 19.0 ignored")]
  [InlineData("18.3.2 || >= 0")]
  [InlineData("= 18.3.2\n")]
  [InlineData("= 18.3.2\r\n")]
  [InlineData("18.3.x\n")]
  [InlineData("18.3.x\r\n")]
  [InlineData(">= 18.3 < 19.0\n")]
  [InlineData(">= 18.3 < 19.0\r\n")]
  [InlineData("\n= 18.3.2")]
  public void Parse_RejectsMalformedExpressions(string expression)
  {
    Assert.Throws<FormatException>(() => VersionConstraint.Parse(expression));
  }

  [Fact]
  public void Parsing_IsIndependentOfCurrentCulture()
  {
    var originalCulture = CultureInfo.CurrentCulture;
    try
    {
      CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

      Assert.True(SemanticVersion.TryParse("2.50.1", out var version));
      Assert.True(VersionConstraint.Parse(">= 2.50 < 3.0").IsSatisfiedBy(version));
    }
    finally
    {
      CultureInfo.CurrentCulture = originalCulture;
    }
  }
}
