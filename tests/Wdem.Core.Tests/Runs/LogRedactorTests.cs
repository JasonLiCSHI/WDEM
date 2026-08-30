using System.Text.Json;
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
  [InlineData("clientSecret=hunter2", "clientSecret=***")]
  [InlineData("access_token=abc123", "access_token=***")]
  [InlineData("refresh-token=refresh-value", "refresh-token=***")]
  [InlineData("passwd: legacy-value", "passwd: ***")]
  [InlineData("pwd='short-value'", "pwd='***'")]
  [InlineData("secret=generic-value", "secret=***")]
  [InlineData("githubToken=github-value", "githubToken=***")]
  [InlineData("databasePassword=db-value", "databasePassword=***")]
  [InlineData("serviceSecret=service-value", "serviceSecret=***")]
  [InlineData("api-key=api-value", "api-key=***")]
  [InlineData("thumb_print=thumb-value", "thumb_print=***")]
  public void Redact_UsesDiagnosticSecretKeySemantics(string input, string expected)
  {
    Assert.Equal(expected, _redactor.Redact(input));
  }

  [Theory]
  [InlineData("githubToken")]
  [InlineData("databasePassword")]
  [InlineData("serviceSecret")]
  [InlineData("api-key")]
  [InlineData("thumb_print")]
  public void RedactNamedValue_RedactsNormalizedSensitiveKeys(string key)
  {
    Assert.Equal("***", _redactor.RedactNamedValue(key, "credential-value"));
  }

  [Theory]
  [InlineData("tokenizer")]
  [InlineData("passwordPolicy")]
  [InlineData("secretary")]
  [InlineData("monkey")]
  public void RedactNamedValue_PreservesOrdinaryKeys(string key)
  {
    Assert.Equal("ordinary-value", _redactor.RedactNamedValue(key, "ordinary-value"));
  }

  [Theory]
  [InlineData("Bearer abc.def.ghi", "Bearer ***")]
  [InlineData("using bEaReR ABC123-token", "using bEaReR ***")]
  [InlineData("Bearer abc.def.ghi, retry scheduled", "Bearer ***, retry scheduled")]
  [InlineData("Bearer abc.def.ghi; provider healthy", "Bearer ***; provider healthy")]
  [InlineData("(Bearer abc.def.ghi) completed", "(Bearer ***) completed")]
  [InlineData("Bearer abc.def.ghi.", "Bearer ***.")]
  [InlineData("Bearer abcDEFghi+/=", "Bearer ***")]
  [InlineData("Bearer abc123", "Bearer ***")]
  [InlineData("Bearer hunter2", "Bearer ***")]
  [InlineData("Bearer credential", "Bearer ***")]
  [InlineData("Bearer abc123 expired", "Bearer *** expired")]
  [InlineData("Bearer hunter2 rejected", "Bearer *** rejected")]
  [InlineData("Bearer abc-123 unavailable", "Bearer *** unavailable")]
  [InlineData("Bearer secret expired", "Bearer *** expired")]
  [InlineData("Bearer hunter rejected", "Bearer *** rejected")]
  [InlineData(
      "Bearer OAuth2 authentication enabled unexpectedly",
      "Bearer *** authentication enabled unexpectedly")]
  [InlineData("Bearer abcdefghijklmnop", "Bearer ***")]
  [InlineData("Bearer abcdefghijklmnopqrstuvw", "Bearer ***")]
  public void Redact_RemovesStandaloneBearerTokensWithoutConsumingFollowingText(
      string input,
      string expected)
  {
    Assert.Equal(expected, _redactor.Redact(input));
  }

  [Fact]
  public void Redact_RemovesBearerCredentialFromEqualsAuthorizationHeader()
  {
    Assert.Equal("Authorization=Bearer ***", _redactor.Redact("Authorization=Bearer short"));
  }

  [Theory]
  [InlineData("Bearer OAuth2 authentication enabled")]
  [InlineData("Bearer RFC6750 support enabled")]
  [InlineData("Bearer authentication failed")]
  [InlineData("Bearer token unavailable")]
  [InlineData("Bearer authorization required")]
  [InlineData("bearer service ready")]
  public void Redact_PreservesBearerProtocolDiagnostics(string input)
  {
    Assert.Equal(input, _redactor.Redact(input));
  }

  [Fact]
  public void Redact_SanitizesShortBearerTokensInStructuredErrorFields()
  {
    var error = new StructuredError(
        WdemErrorCode.ProviderError,
        "Bearer abc123",
        "Bearer hunter2")
    {
      ResourceId = "Bearer resource1",
      StepId = "Bearer step123",
      LogLocation = "Bearer log123",
      SuggestedAction = "Bearer action1",
      UnderlyingException = new InvalidOperationException("Bearer failure1")
    };

    var result = _redactor.Redact(error);

    var visibleFields = new[]
    {
      (result.Summary, "abc123"),
      (result.Detail, "hunter2"),
      (result.ResourceId, "resource1"),
      (result.StepId, "step123"),
      (result.LogLocation, "log123"),
      (result.SuggestedAction, "action1"),
      (result.UnderlyingExceptionMessage, "failure1")
    };
    Assert.All(visibleFields, field =>
        Assert.DoesNotContain(field.Item2, field.Item1, StringComparison.Ordinal));
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
  public void Redact_BoundsRepeatedUnterminatedPemBlocksAndRemovesTheirPayloads()
  {
    var input = string.Concat(Enumerable.Repeat(
        "-----BEGIN PRIVATE KEY-----\nprivate-line\n",
        1024));

    var result = _redactor.Redact(input);

    Assert.Equal("-----BEGIN PRIVATE KEY-----\n***", result);
    Assert.DoesNotContain("private-line", result, StringComparison.Ordinal);
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

  [Fact]
  public void Redact_SanitizesStructuredErrorUnderlyingExceptionTypeSnapshot()
  {
    const string secret = "secret-exception-type";
    var redactor = new LogRedactor([secret]);
    string snapshot = JsonSerializer.Serialize(new
    {
      Code = WdemErrorCode.ProviderError,
      Summary = "Provider failed.",
      Detail = "Inspect the exception type.",
      UnderlyingExceptionType = $"Vendor.{secret}.Exception"
    });
    StructuredError error = JsonSerializer.Deserialize<StructuredError>(snapshot)!;

    StructuredError result = redactor.Redact(error);

    Assert.Equal("Vendor.***.Exception", result.UnderlyingExceptionType);
  }
}
