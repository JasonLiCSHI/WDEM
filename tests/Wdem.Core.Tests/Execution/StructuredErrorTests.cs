using System.Diagnostics;
using System.Text.Json;
using Wdem.Core.Execution;
using Xunit;

namespace Wdem.Core.Tests.Execution;

public sealed class StructuredErrorTests
{
  [Fact]
  public void StructuredError_PreservesRequiredDiagnosticFields()
  {
    var error = new StructuredError(
        WdemErrorCode.InstallationError,
        "Install failed",
        "winget returned 1603")
    {
      ResourceId = "git",
      StepId = "git:install",
      ProcessExitCode = 1603,
      LogLocation = @"C:\logs\run.ndjson",
      SuggestedAction = "Review the installer log and retry.",
      IsRetryable = true
    };

    Assert.Equal(WdemErrorCode.InstallationError, error.Code);
    Assert.Equal("Install failed", error.Summary);
    Assert.Equal("winget returned 1603", error.Detail);
    Assert.Equal("git", error.ResourceId);
    Assert.Equal("git:install", error.StepId);
    Assert.Equal(1603, error.ProcessExitCode);
    Assert.Equal(@"C:\logs\run.ndjson", error.LogLocation);
    Assert.Equal("Review the installer log and retry.", error.SuggestedAction);
    Assert.True(error.IsRetryable);
  }

  [Fact]
  public void Serialization_ExcludesRuntimeExceptionAndPersistsSanitizedDiagnostics()
  {
    var exception = new InvalidOperationException(
        "Installer failed; password=super-secret; token: abc123");
    var error = new StructuredError(
        WdemErrorCode.InstallationError,
        "Install failed",
        "The installer process failed.")
    {
      UnderlyingException = exception
    };

    var json = JsonSerializer.Serialize(error);
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;

    Assert.Same(exception, error.UnderlyingException);
    Assert.Equal(typeof(InvalidOperationException).FullName, error.UnderlyingExceptionType);
    Assert.DoesNotContain("super-secret", error.UnderlyingExceptionMessage, StringComparison.Ordinal);
    Assert.DoesNotContain("abc123", error.UnderlyingExceptionMessage, StringComparison.Ordinal);
    Assert.False(root.TryGetProperty("UnderlyingException", out _));
    Assert.Equal(
        typeof(InvalidOperationException).FullName,
        root.GetProperty("UnderlyingExceptionType").GetString());
    Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
    Assert.DoesNotContain("abc123", json, StringComparison.Ordinal);
  }

  [Fact]
  public void Serialization_RoundTripsPersistedErrorFields()
  {
    var original = new StructuredError(
        WdemErrorCode.ProviderError,
        "Provider failed",
        "The provider returned an error.")
    {
      ResourceId = "git",
      StepId = "git:detect",
      UnderlyingException = new InvalidOperationException("safe diagnostic")
    };

    var restored = JsonSerializer.Deserialize<StructuredError>(JsonSerializer.Serialize(original));

    Assert.NotNull(restored);
    Assert.Equal(original.Code, restored.Code);
    Assert.Equal(original.Summary, restored.Summary);
    Assert.Equal(original.Detail, restored.Detail);
    Assert.Equal(original.ResourceId, restored.ResourceId);
    Assert.Equal(original.StepId, restored.StepId);
    Assert.Equal(original.UnderlyingExceptionType, restored.UnderlyingExceptionType);
    Assert.Equal(original.UnderlyingExceptionMessage, restored.UnderlyingExceptionMessage);
    Assert.Null(restored.UnderlyingException);
  }

  [Fact]
  public void Serialization_RedactsBearerTokensFromExceptionMessages()
  {
    var error = new StructuredError(
        WdemErrorCode.DownloadError,
        "Download failed",
        "The download request failed.")
    {
      UnderlyingException = new InvalidOperationException(
          "Request failed with Authorization: Bearer abc.def.ghi")
    };

    var json = JsonSerializer.Serialize(error);

    Assert.DoesNotContain("abc.def.ghi", error.UnderlyingExceptionMessage, StringComparison.Ordinal);
    Assert.DoesNotContain("abc.def.ghi", json, StringComparison.Ordinal);
  }

  [Fact]
  public void Deserialization_SanitizesPersistedExceptionMessage()
  {
    const string json =
        """{"Code":6,"Summary":"Install failed","Detail":"Provider error","UnderlyingExceptionMessage":"token=raw-secret"}""";

    var restored = JsonSerializer.Deserialize<StructuredError>(json);

    Assert.NotNull(restored);
    Assert.DoesNotContain("raw-secret", restored.UnderlyingExceptionMessage, StringComparison.Ordinal);
    Assert.DoesNotContain("raw-secret", JsonSerializer.Serialize(restored), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("OAuth failed: client_secret=hunter2", "hunter2")]
  [InlineData("Process inherited GITHUB_TOKEN=ghp_abcdef", "ghp_abcdef")]
  [InlineData("Database rejected DB_PASSWORD: p@ssw0rd", "p@ssw0rd")]
  [InlineData("Vault returned MY_SECRET='top-secret'", "top-secret")]
  [InlineData("Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
  [InlineData("Authorization: Negotiate abc123", "abc123")]
  [InlineData(@"Could not read C:\Users\Alice\AppData\Local\WDEM", "Alice")]
  [InlineData(@"Could not read \\server\Users\Bob\profile.json", "Bob")]
  [InlineData(@"Could not read \\server\home\Carol\profile.json", "Carol")]
  [InlineData("Could not read /home/dave/.config/wdem", "dave")]
  public void ExceptionPersistence_RedactsCredentialsAndUserPaths(
      string diagnostic,
      string sensitiveValue)
  {
    var error = new StructuredError(
        WdemErrorCode.ProviderError,
        "Provider failed",
        "See persisted diagnostics.")
    {
      UnderlyingException = new InvalidOperationException(diagnostic)
    };

    Assert.DoesNotContain(
        sensitiveValue,
        error.UnderlyingExceptionMessage,
        StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ExceptionPersistence_PreservesOrdinaryDiagnostics()
  {
    const string diagnostic =
        @"Git exited while reading C:\Program Files\Git\config at revision tokenization.";
    var error = new StructuredError(
        WdemErrorCode.ProviderError,
        "Provider failed",
        "See persisted diagnostics.")
    {
      UnderlyingException = new InvalidOperationException(diagnostic)
    };

    Assert.Equal(diagnostic, error.UnderlyingExceptionMessage);
  }

  [Fact]
  public void ClearingUnderlyingException_ClearsPersistedDiagnostics()
  {
    var original = new StructuredError(
        WdemErrorCode.ProviderError,
        "Provider failed",
        "See persisted diagnostics.")
    {
      UnderlyingException = new InvalidOperationException("safe diagnostic")
    };

    var cleared = original with { UnderlyingException = null };

    Assert.Null(cleared.UnderlyingException);
    Assert.Null(cleared.UnderlyingExceptionType);
    Assert.Null(cleared.UnderlyingExceptionMessage);
  }

  [Fact]
  public void Constructor_RejectsNullSummaryOrDetail()
  {
    Assert.Throws<ArgumentNullException>(() => new StructuredError(
        WdemErrorCode.ProviderError,
        null!,
        "detail"));
    Assert.Throws<ArgumentNullException>(() => new StructuredError(
        WdemErrorCode.ProviderError,
        "summary",
        null!));
  }

  [Fact]
  public void ConstructorAndInit_SanitizePersistedSummaryAndDetail()
  {
    var error = new StructuredError(
        WdemErrorCode.ProviderError,
        "Provider failed with token=summary-secret",
        @"Bearer detail-secret at C:\Users\Alice\profile.yaml") with
    {
      Detail = "Authorization: Basic replacement-secret"
    };

    var serialized = JsonSerializer.Serialize(error);

    Assert.DoesNotContain("summary-secret", error.Summary, StringComparison.Ordinal);
    Assert.DoesNotContain("replacement-secret", error.Detail, StringComparison.Ordinal);
    Assert.DoesNotContain("Alice", serialized, StringComparison.Ordinal);
    Assert.DoesNotContain("detail-secret", serialized, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("{}")]
  [InlineData("{\"Code\":9,\"Summary\":null,\"Detail\":\"detail\"}")]
  [InlineData("{\"Code\":9,\"Summary\":\"summary\",\"Detail\":null}")]
  public void Deserialization_RejectsMissingOrNullRequiredText(string json)
  {
    var exception = Record.Exception(() => JsonSerializer.Deserialize<StructuredError>(json));

    Assert.True(
        exception is JsonException or ArgumentNullException,
        $"Expected JsonException or ArgumentNullException, got {exception?.GetType().FullName ?? "no exception"}.");
  }

  [Fact]
  public void ExceptionPersistence_RedactsCompleteDigestAuthorizationLine()
  {
    const string diagnostic =
        "Authorization: Digest username=\"Mufasa\", realm=\"testrealm@host.com\", " +
        "nonce=\"abc\", response=\"deadbeef\"\r\nRetry remains available.";
    var error = new StructuredError(
        WdemErrorCode.ProviderError,
        "Provider failed",
        "See persisted diagnostics.")
    {
      UnderlyingException = new InvalidOperationException(diagnostic)
    };

    Assert.DoesNotContain("Mufasa", error.UnderlyingExceptionMessage, StringComparison.Ordinal);
    Assert.DoesNotContain("testrealm", error.UnderlyingExceptionMessage, StringComparison.Ordinal);
    Assert.DoesNotContain("deadbeef", error.UnderlyingExceptionMessage, StringComparison.Ordinal);
    Assert.Contains("\r\nRetry remains available.", error.UnderlyingExceptionMessage, StringComparison.Ordinal);
  }

  [Fact]
  public void Deserialization_RedactsCompleteDigestAuthorizationLine()
  {
    const string diagnostic =
        "Authorization: Digest username=\"Mufasa\", realm=\"testrealm@host.com\", " +
        "nonce=\"abc\", response=\"deadbeef\"\nOrdinary follow-up diagnostic.";
    var json = JsonSerializer.Serialize(new
    {
      Code = WdemErrorCode.ProviderError,
      Summary = "Provider failed",
      Detail = "See persisted diagnostics.",
      UnderlyingExceptionMessage = diagnostic
    });

    var restored = JsonSerializer.Deserialize<StructuredError>(json);

    Assert.NotNull(restored);
    Assert.DoesNotContain("Mufasa", restored.UnderlyingExceptionMessage, StringComparison.Ordinal);
    Assert.DoesNotContain("deadbeef", restored.UnderlyingExceptionMessage, StringComparison.Ordinal);
    Assert.Contains("\nOrdinary follow-up diagnostic.", restored.UnderlyingExceptionMessage, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("accessToken=at_123", "at_123")]
  [InlineData("refreshToken=rt_123", "rt_123")]
  [InlineData("clientSecret=hunter2", "hunter2")]
  [InlineData("apiKey=key_123", "key_123")]
  [InlineData("AccessToken=pascal_at", "pascal_at")]
  [InlineData("ClientSecret=pascal_secret", "pascal_secret")]
  public void ExceptionPersistence_RedactsCamelCaseOAuthKeys(
      string diagnostic,
      string sensitiveValue)
  {
    var error = new StructuredError(
        WdemErrorCode.ProviderError,
        "Provider failed",
        "See persisted diagnostics.")
    {
      UnderlyingException = new InvalidOperationException(diagnostic)
    };

    Assert.DoesNotContain(sensitiveValue, error.UnderlyingExceptionMessage, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("accessToken=at_123", "at_123")]
  [InlineData("refreshToken=rt_123", "rt_123")]
  [InlineData("clientSecret=hunter2", "hunter2")]
  [InlineData("apiKey=key_123", "key_123")]
  public void Deserialization_RedactsCamelCaseOAuthKeys(
      string diagnostic,
      string sensitiveValue)
  {
    var json = JsonSerializer.Serialize(new
    {
      Code = WdemErrorCode.ProviderError,
      Summary = "Provider failed",
      Detail = "See persisted diagnostics.",
      UnderlyingExceptionMessage = diagnostic
    });

    var restored = JsonSerializer.Deserialize<StructuredError>(json);

    Assert.NotNull(restored);
    Assert.DoesNotContain(sensitiveValue, restored.UnderlyingExceptionMessage, StringComparison.Ordinal);
  }

  [Fact]
  public void ExceptionPersistence_HandlesLongDiagnosticsInBoundedTime()
  {
    var diagnostic = new string('a', 250_000) + " clientSecret=tail-secret";
    var stopwatch = Stopwatch.StartNew();

    var error = new StructuredError(
        WdemErrorCode.ProviderError,
        "Provider failed",
        "See persisted diagnostics.")
    {
      UnderlyingException = new InvalidOperationException(diagnostic)
    };

    stopwatch.Stop();
    Assert.DoesNotContain("tail-secret", error.UnderlyingExceptionMessage, StringComparison.Ordinal);
    Assert.StartsWith(new string('a', 128), error.UnderlyingExceptionMessage, StringComparison.Ordinal);
    Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Sanitization took {stopwatch.Elapsed}.");
  }
}
