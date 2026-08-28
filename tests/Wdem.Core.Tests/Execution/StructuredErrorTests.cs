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
}
