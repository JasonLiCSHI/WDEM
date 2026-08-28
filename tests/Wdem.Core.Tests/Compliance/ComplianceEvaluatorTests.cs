using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Versions;
using Xunit;

namespace Wdem.Core.Tests.Compliance;

public sealed class ComplianceEvaluatorTests
{
  private readonly IComplianceEvaluator _evaluator = new ComplianceEvaluator();

  [Theory]
  [InlineData(false, null, ComplianceStatus.Missing)]
  [InlineData(true, "2.49.9", ComplianceStatus.VersionMismatch)]
  [InlineData(true, "not-a-version", ComplianceStatus.VersionMismatch)]
  [InlineData(true, "2.50.1", ComplianceStatus.Satisfied)]
  public void Evaluate_MapsExistenceAndVersionEvidence(
      bool exists,
      string? version,
      ComplianceStatus expected)
  {
    var state = State(exists, version);

    Assert.Equal(expected, _evaluator.Evaluate(GitResource(), state).Status);
  }

  [Fact]
  public void Evaluate_DetectionFailure_IsNeverClassifiedAsMissing()
  {
    var result = _evaluator.Evaluate(GitResource(), new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Failed,
      Exists = false,
      Error = "access denied"
    });

    Assert.Equal(ComplianceStatus.DetectionFailed, result.Status);
    Assert.Equal(WdemErrorCode.DetectionError, result.Error!.Code);
    Assert.Contains("access denied", result.Error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public void Evaluate_CancelledDetection_ReturnsStructuredCancellationFailure()
  {
    var result = _evaluator.Evaluate(GitResource(), new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Cancelled,
      Error = "cancelled by user"
    });

    Assert.Equal(ComplianceStatus.DetectionFailed, result.Status);
    Assert.Equal(WdemErrorCode.CancellationError, result.Error!.Code);
  }

  [Fact]
  public void Evaluate_UnsupportedDetection_RemainsUnsupported()
  {
    var result = _evaluator.Evaluate(GitResource(), new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Unsupported,
      Error = "platform is unsupported"
    });

    Assert.Equal(ComplianceStatus.Unsupported, result.Status);
    Assert.Equal(WdemErrorCode.ProviderError, result.Error!.Code);
  }

  [Fact]
  public void Evaluate_AnyInstalledSdkVersionMaySatisfyConstraint()
  {
    var state = State(exists: true, version: "not-a-version") with
    {
      InstalledVersions =
      [
        new SemanticVersion(9, 0, 4),
        new SemanticVersion(10, 0, 105)
      ]
    };
    var resource = GitResource() with { VersionConstraint = "10.0.x" };

    Assert.Equal(ComplianceStatus.Satisfied, _evaluator.Evaluate(resource, state).Status);
  }

  [Fact]
  public void Evaluate_NoInstalledVersionMatches_ReturnsVersionMismatch()
  {
    var state = State(exists: true, version: null) with
    {
      InstalledVersions =
      [
        new SemanticVersion(8, 0, 412),
        new SemanticVersion(9, 0, 4)
      ]
    };
    var resource = GitResource() with { VersionConstraint = "10.0.x" };

    var result = _evaluator.Evaluate(resource, state);

    Assert.Equal(ComplianceStatus.VersionMismatch, result.Status);
    Assert.Equal(WdemErrorCode.VersionError, result.Error!.Code);
  }

  [Fact]
  public void Evaluate_MalformedDesiredConstraint_ReturnsVersionMismatchWithoutThrowing()
  {
    var resource = GitResource() with { VersionConstraint = ">= absolutely-not-a-version" };

    var result = _evaluator.Evaluate(resource, State(exists: true, version: "2.52.1"));

    Assert.Equal(ComplianceStatus.VersionMismatch, result.Status);
    Assert.Equal(WdemErrorCode.VersionError, result.Error!.Code);
  }

  [Fact]
  public void Evaluate_WithoutVersionConstraint_DoesNotRequireParseableVersion()
  {
    var resource = GitResource() with { VersionConstraint = null };

    var result = _evaluator.Evaluate(resource, State(exists: true, version: "vendor build"));

    Assert.Equal(ComplianceStatus.Satisfied, result.Status);
  }

  [Theory]
  [InlineData("ABCDEF012345", ComplianceStatus.Satisfied)]
  [InlineData("different", ComplianceStatus.ConfigurationMismatch)]
  [InlineData(null, ComplianceStatus.ConfigurationMismatch)]
  public void Evaluate_MapsExpectedConfigurationHash(
      string? actualHash,
      ComplianceStatus expected)
  {
    var resource = GitResource() with
    {
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["expectedSha256"] = "abcdef012345"
      }
    };
    var state = State(exists: true, version: "2.52.1") with
    {
      ConfigurationHash = actualHash
    };

    Assert.Equal(expected, _evaluator.Evaluate(resource, state).Status);
  }

  [Fact]
  public void Evaluate_PreservesProviderStructuredFailure()
  {
    var providerError = new StructuredError(
        WdemErrorCode.PermissionError,
        "Detection denied",
        "Administrator access is required.")
    {
      ResourceId = "git"
    };
    var state = new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Failed,
      StructuredError = providerError
    };

    var result = _evaluator.Evaluate(GitResource(), state);

    Assert.Same(providerError, result.Error);
  }

  [Fact]
  public void Evaluate_LegacyFailureText_IsSanitizedInStructuredDiagnostic()
  {
    var state = new DetectedState
    {
      ResourceId = "git",
      Outcome = DetectionOutcome.Failed,
      Error = "request token=super-secret failed"
    };

    var result = _evaluator.Evaluate(GitResource(), state);

    Assert.DoesNotContain("super-secret", result.Error!.Detail, StringComparison.Ordinal);
    Assert.Contains("[REDACTED]", result.Error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public void Evaluate_InstallerExitCodeEvidence_NeverOverridesMissingState()
  {
    var state = State(exists: false, version: "2.52.1") with
    {
      Evidence = new Dictionary<string, string> { ["installerExitCode"] = "0" }
    };

    Assert.Equal(ComplianceStatus.Missing, _evaluator.Evaluate(GitResource(), state).Status);
  }

  [Fact]
  public void Evaluate_StateForDifferentResource_IsDetectionFailure()
  {
    var state = State(exists: true, version: "2.52.1") with
    {
      ResourceId = "different-resource"
    };

    var result = _evaluator.Evaluate(GitResource(), state);

    Assert.Equal(ComplianceStatus.DetectionFailed, result.Status);
    Assert.Equal(WdemErrorCode.DetectionError, result.Error!.Code);
  }

  [Fact]
  public void Evaluate_NullArguments_AreRejected()
  {
    Assert.Throws<ArgumentNullException>(() => _evaluator.Evaluate(null!, State(true, "2.52.1")));
    Assert.Throws<ArgumentNullException>(() => _evaluator.Evaluate(GitResource(), null!));
  }

  private static ResourceDefinition GitResource() => new()
  {
    Id = "git",
    Type = "package",
    Provider = "winget",
    VersionConstraint = ">=2.50 <3.0"
  };

  private static DetectedState State(bool exists, string? version) => new()
  {
    ResourceId = "git",
    Outcome = DetectionOutcome.Succeeded,
    Exists = exists,
    Version = version
  };
}
