using Wdem.Core.Execution;
using Wdem.Core.Runs;
using Xunit;

namespace Wdem.Core.Tests.Runs;

public sealed class LogRedactorTests
{
  private readonly LogRedactor _redactor = new();

  [Theory]
  [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: Bearer ***")]
  [InlineData("password=correct-horse", "password=***")]
  [InlineData("thumbprint=0123456789ABCDEF", "thumbprint=***")]
  [InlineData("API_KEY: abc123", "API_KEY: ***")]
  [InlineData("ToKeN = \"secret-value\"", "ToKeN = \"***\"")]
  [InlineData("{\"token\":\"json-secret\"}", "{\"token\":\"***\"}")]
  public void Redact_RemovesCommonSecrets(string input, string expected)
  {
    Assert.Equal(expected, _redactor.Redact(input));
  }

  [Theory]
  [InlineData("Bearer abc.def.ghi", "Bearer ***")]
  [InlineData("using bEaReR ABC123-token", "using bEaReR ***")]
  [InlineData("Bearer abc.def.ghi, retry scheduled", "Bearer ***, retry scheduled")]
  [InlineData("Bearer abc.def.ghi; provider healthy", "Bearer ***; provider healthy")]
  [InlineData("(Bearer abc.def.ghi) completed", "(Bearer ***) completed")]
  [InlineData("Bearer abc.def.ghi.", "Bearer ***.")]
  [InlineData("Bearer abcDEFghi+/=", "Bearer ***")]
  [InlineData("Bearer abcdefghijklmnop", "Bearer ***")]
  [InlineData("Bearer abcdefghijklmnopqrstuvw", "Bearer ***")]
  public void Redact_RemovesStandaloneBearerTokensWithoutConsumingFollowingText(
      string input,
      string expected)
  {
    Assert.Equal(expected, _redactor.Redact(input));
  }

  [Theory]
  [InlineData("Bearer OAuth2 authentication enabled")]
  [InlineData("Bearer RFC6750 support enabled")]
  public void Redact_PreservesBearerProtocolDiagnostics(string input)
  {
    Assert.Equal(input, _redactor.Redact(input));
  }

  [Fact]
  public void Redact_RemovesPrivateKeyAndCertificateBodiesAcrossLines()
  {
    const string input = """
        provider diagnostic before
        -----BEGIN PRIVATE KEY-----
        private-line-one
        private-line-two
        -----END PRIVATE KEY-----
        -----BEGIN CERTIFICATE-----
        certificate-body
        -----END CERTIFICATE-----
        provider diagnostic after
        """;

    var result = _redactor.Redact(input);

    Assert.DoesNotContain("private-line-one", result, StringComparison.Ordinal);
    Assert.DoesNotContain("certificate-body", result, StringComparison.Ordinal);
    Assert.Contains("provider diagnostic before", result, StringComparison.Ordinal);
    Assert.Contains("provider diagnostic after", result, StringComparison.Ordinal);
  }

  [Fact]
  public void RegisterSensitiveValues_RedactsProfileValuesIncludingMultilineValues()
  {
    _redactor.RegisterSensitiveValues(["profile-secret", "line-one\nline-two"]);

    var result = _redactor.Redact("value=profile-secret\nnotes=line-one\nline-two");

    Assert.Equal("value=***\nnotes=***", result);
  }

  [Fact]
  public void RegisterSensitiveValues_IgnoresEmptyAndWhitespaceOnlyValues()
  {
    _redactor.RegisterSensitiveValues([string.Empty, " ", "\t"]);

    Assert.Equal("provider diagnostic remains", _redactor.Redact("provider diagnostic remains"));
  }

  [Fact]
  public void Redact_DoesNotRemoveDiagnosticsThatOnlyMentionSensitiveKeyNames()
  {
    const string input = "Token cache refreshed; password policy loaded; api-key provider healthy.";

    Assert.Equal(input, _redactor.Redact(input));
  }

  [Fact]
  public void Redact_UsesAssignmentBoundariesAndDoesNotMatchKeyNameSuffixes()
  {
    const string input = "monkey=value; mypasswordish=value; bearer service ready";

    Assert.Equal(input, _redactor.Redact(input));
  }

  [Fact]
  public void Redact_SanitizesEveryVisibleStructuredErrorField()
  {
    var error = new StructuredError(
        WdemErrorCode.ProviderError,
        "password=summary-secret",
        "Authorization: Bearer detail.secret.token")
    {
      LogLocation = "token=log-secret",
      SuggestedAction = "Use api-key=action-secret",
      UnderlyingException = new InvalidOperationException("password=exception-secret")
    };

    var result = _redactor.Redact(error);

    Assert.Equal("password=[REDACTED]", result.Summary);
    Assert.Equal("Authorization=[REDACTED]", result.Detail);
    Assert.Equal("token=***", result.LogLocation);
    Assert.Equal("Use api-key=***", result.SuggestedAction);
    Assert.Equal("password=[REDACTED]", result.UnderlyingExceptionMessage);
    Assert.Null(result.UnderlyingException);
  }
}
