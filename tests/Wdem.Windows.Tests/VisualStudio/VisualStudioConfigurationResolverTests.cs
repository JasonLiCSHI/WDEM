using System.Security.Cryptography;
using Wdem.Core.Execution;
using Wdem.Windows.VisualStudio;
using Xunit;

namespace Wdem.Windows.Tests.VisualStudio;

public sealed class VisualStudioConfigurationResolverTests : IDisposable
{
  private readonly string _root = Path.Combine(
      Path.GetTempPath(),
      $"wdem-vsconfig-resolver-{Guid.NewGuid():N}");

  [Fact]
  public async Task ResolveAsync_ParsesTheSameImmutableSnapshotThatWasHashed()
  {
    Directory.CreateDirectory(_root);
    var path = Path.Combine(_root, "profile.vsconfig");
    var trusted = Config("Microsoft.VisualStudio.Component.Git");
    await File.WriteAllTextAsync(path, trusted);
    var expectedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
    var resolver = new VisualStudioConfigurationResolver(afterSnapshot: _ =>
    {
      File.WriteAllText(path, Config("Microsoft.VisualStudio.Component.Replacement"));
      return Task.CompletedTask;
    });

    var result = await resolver.ResolveAsync(
        Options(path),
        expectedHash,
        CancellationToken.None);

    Assert.Null(result.Error);
    Assert.Equal(expectedHash, result.Sha256);
    Assert.Contains("Microsoft.VisualStudio.Component.Git", result.Options.Components);
    Assert.DoesNotContain(
        "Microsoft.VisualStudio.Component.Replacement",
        result.Options.Components);
  }

  [Fact]
  public async Task ResolveAsync_OrdinaryOpenFailureReturnsSanitizedConfigurationError()
  {
    Directory.CreateDirectory(_root);
    var secretPath = Path.Combine(_root, "secret-token-directory");
    Directory.CreateDirectory(secretPath);
    var resolver = new VisualStudioConfigurationResolver();

    var result = await resolver.ResolveAsync(
        Options(secretPath),
        new string('A', 64),
        CancellationToken.None);

    Assert.Equal(WdemErrorCode.ConfigurationError, result.Error!.Code);
    Assert.DoesNotContain("secret-token", result.Error.Detail, StringComparison.Ordinal);
    Assert.DoesNotContain(
        "secret-token",
        result.Error.UnderlyingExceptionMessage ?? string.Empty,
        StringComparison.Ordinal);
  }

  public void Dispose()
  {
    if (Directory.Exists(_root))
    {
      Directory.Delete(_root, recursive: true);
    }
  }

  private static VisualStudioResourceOptions Options(string path) => new()
  {
    ProductId = "Microsoft.VisualStudio.Product.Community",
    Edition = "Community",
    ChannelId = "VisualStudio.18.Release",
    Workloads = ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
    Components = [],
    VsConfigPath = path
  };

  private static string Config(string component) =>
      $$"""{ "version": "1.0", "components": ["{{component}}"] }""";
}
