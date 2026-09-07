using Wdem.Domain.Versions;
using Xunit;

namespace Wdem.Domain.Tests;

public sealed class VersionRequirementTests
{
  [Theory]
  [InlineData("= 18.3.2", "18.3.2", ComplianceStatus.Satisfied)]
  [InlineData("= 18.3.2", "18.3.3", ComplianceStatus.VersionMismatch)]
  [InlineData("18.3.x", "18.3.9", ComplianceStatus.Satisfied)]
  [InlineData("18.3.x", "18.4.0", ComplianceStatus.VersionMismatch)]
  [InlineData(">= 18.3 < 19.0", "18.9.1", ComplianceStatus.Satisfied)]
  [InlineData(">= 18.3 < 19.0", "19.0.0", ComplianceStatus.VersionMismatch)]
  [InlineData(">= 2.50", "2.49.9", ComplianceStatus.UpgradeRequired)]
  [InlineData("> 2.50", "2.50.0", ComplianceStatus.UpgradeRequired)]
  [InlineData("<= 3.0", "3.1", ComplianceStatus.VersionMismatch)]
  public void EvaluateClassifiesInstalledVersions(
      string expression,
      string installedVersion,
      ComplianceStatus expected)
  {
    var requirement = VersionRequirement.Parse(expression);

    var result = requirement.Evaluate(installedVersion);

    Assert.Equal(expected, result.Status);
    Assert.Equal(expression, result.Requirement.Expression);
  }

  [Fact]
  public void EvaluateMarksUnparseableInstalledVersionAsMismatch()
  {
    var requirement = VersionRequirement.Parse(">= 2.50");

    var result = requirement.Evaluate("unknown");

    Assert.Equal(ComplianceStatus.VersionMismatch, result.Status);
    Assert.Null(result.InstalledVersion);
  }

  [Fact]
  public void MissingCreatesAnExplicitComplianceResult()
  {
    var requirement = VersionRequirement.Parse(">= 2.50");

    var result = ComplianceResult.Missing(requirement);

    Assert.Equal(ComplianceStatus.Missing, result.Status);
    Assert.Null(result.InstalledVersion);
    Assert.Same(requirement, result.Requirement);
  }

  [Fact]
  public void ParseRejectsInvalidExpressions()
  {
    Assert.Throws<FormatException>(() => VersionRequirement.Parse("latest"));
  }
}
