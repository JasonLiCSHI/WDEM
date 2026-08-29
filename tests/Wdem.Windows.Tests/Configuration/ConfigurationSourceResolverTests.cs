using System.Security.Cryptography;
using System.Text;
using Wdem.Windows.Configuration;
using Xunit;

namespace Wdem.Windows.Tests.Configuration;

public sealed class ConfigurationSourceResolverTests : IDisposable
{
  private readonly string _root = Path.Combine(
      Path.GetTempPath(),
      $"wdem-configuration-{Guid.NewGuid():N}");

  [Fact]
  public async Task ResolveAsync_ProfileRelativeSourceReturnsVerifiedImmutableSnapshot()
  {
    var profiles = Path.Combine(_root, "profiles");
    var source = Path.Combine(profiles, "settings", "team.DotSettings");
    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
    var contents = Encoding.UTF8.GetBytes("<wpf:ResourceDictionary />");
    await File.WriteAllBytesAsync(source, contents);
    var expectedHash = Convert.ToHexString(SHA256.HashData(contents));
    var resolver = new ConfigurationSourceResolver(_root, profiles);

    var result = await resolver.ResolveAsync(
        Path.Combine("settings", "team.DotSettings"),
        expectedHash.ToLowerInvariant(),
        CancellationToken.None);

    Assert.True(result.IsValid, result.Error?.Detail);
    Assert.Equal(Path.GetFullPath(source), result.Source!.Path);
    Assert.Equal(expectedHash, result.Source.Sha256);
    Assert.True(contents.AsSpan().SequenceEqual(result.Source.Contents.Span));
  }

  [Fact]
  public async Task ResolveAsync_AbsolutePathThroughReparseDirectoryIsRejected()
  {
    var profiles = Path.Combine(_root, "profiles");
    var outside = Path.Combine(_root, "outside");
    var link = Path.Combine(_root, "linked");
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(outside);
    var target = Path.Combine(outside, "team.DotSettings");
    var contents = Encoding.UTF8.GetBytes("settings");
    await File.WriteAllBytesAsync(target, contents);
    try
    {
      Directory.CreateSymbolicLink(link, outside);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
    {
      return;
    }

    var result = await new ConfigurationSourceResolver(_root, profiles).ResolveAsync(
        Path.Combine(link, "team.DotSettings"),
        Convert.ToHexString(SHA256.HashData(contents)),
        CancellationToken.None);

    Assert.False(result.IsValid);
    Assert.Contains("reparse", result.Error!.Detail, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task ResolveAsync_AlternateDataStreamIsRejected()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var host = Path.Combine(_root, "host.txt");
    await File.WriteAllTextAsync(host, "host");
    var streamPath = $"{host}:team.DotSettings";
    var contents = Encoding.UTF8.GetBytes("stream settings");
    try
    {
      await File.WriteAllBytesAsync(streamPath, contents);
    }
    catch (Exception exception) when (exception is IOException or NotSupportedException or UnauthorizedAccessException)
    {
      return;
    }

    var result = await new ConfigurationSourceResolver(_root, profiles).ResolveAsync(
        streamPath,
        Convert.ToHexString(SHA256.HashData(contents)),
        CancellationToken.None);

    Assert.False(result.IsValid);
    Assert.Contains("alternate data stream", result.Error!.Detail, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task CopyAtomicallyAsync_ExistingDestinationSymlinkIsRejectedWithoutChangingTarget()
  {
    Directory.CreateDirectory(_root);
    var target = Path.Combine(_root, "target.DotSettings");
    var destination = Path.Combine(_root, "destination.DotSettings");
    await File.WriteAllTextAsync(target, "original target");
    try
    {
      File.CreateSymbolicLink(destination, target);
    }
    catch (Exception exception) when (exception is IOException or NotSupportedException or UnauthorizedAccessException)
    {
      return;
    }

    var contents = Encoding.UTF8.GetBytes("replacement");
    var source = new ResolvedConfigurationSource(
        Path.Combine(_root, "source.DotSettings"),
        Convert.ToHexString(SHA256.HashData(contents)),
        contents);

    var result = await new ConfigurationImporter().CopyAtomicallyAsync(
        source,
        destination,
        CancellationToken.None);

    Assert.False(result.Succeeded);
    Assert.Equal("original target", await File.ReadAllTextAsync(target));
  }

  public void Dispose()
  {
    if (Directory.Exists(_root))
    {
      Directory.Delete(_root, recursive: true);
    }
  }
}
