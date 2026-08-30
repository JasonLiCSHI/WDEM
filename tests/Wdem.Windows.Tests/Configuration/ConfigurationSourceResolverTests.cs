using System.Diagnostics;
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
  public async Task ResolveAsync_LocalFileUriReturnsVerifiedImmutableSnapshot()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);
    var source = Path.Combine(_root, "external", "team.DotSettings");
    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
    var contents = Encoding.UTF8.GetBytes("file uri settings");
    await File.WriteAllBytesAsync(source, contents);
    var resolver = new ConfigurationSourceResolver(_root, profiles);

    var result = await resolver.ResolveAsync(
        new Uri(source).AbsoluteUri,
        Convert.ToHexString(SHA256.HashData(contents)),
        CancellationToken.None);

    Assert.True(result.IsValid, result.Error?.Detail);
    Assert.Equal(Path.GetFullPath(source), result.Source!.Path);
    Assert.Equal(contents, result.Source.Contents.ToArray());
  }

  [Fact]
  public async Task ResolveAsync_NonFileUriIsRejected()
  {
    var profiles = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profiles);

    var result = await new ConfigurationSourceResolver(_root, profiles).ResolveAsync(
        "https://example.test/team.DotSettings",
        new string('A', 64),
        CancellationToken.None);

    Assert.False(result.IsValid);
    Assert.Contains("file", result.Error!.Detail, StringComparison.OrdinalIgnoreCase);
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
  public async Task ResolveAsync_HoldsSourceHierarchyAgainstJunctionSwapUntilReadCompletes()
  {
    var profiles = Path.Combine(_root, "profiles");
    var trusted = Path.Combine(_root, "trusted", "nested");
    var moved = Path.Combine(_root, "moved");
    var outside = Path.Combine(_root, "outside");
    Directory.CreateDirectory(profiles);
    Directory.CreateDirectory(trusted);
    Directory.CreateDirectory(outside);
    var source = Path.Combine(trusted, "team.DotSettings");
    var outsideSource = Path.Combine(outside, "team.DotSettings");
    var contents = Encoding.UTF8.GetBytes("trusted settings");
    await File.WriteAllBytesAsync(source, contents);
    await File.WriteAllTextAsync(outsideSource, "outside settings");
    var swapRejected = false;
    var resolver = new ConfigurationSourceResolver(
        _root,
        profiles,
        directory =>
        {
          try
          {
            Directory.Move(directory, moved);
            CreateJunction(directory, outside);
          }
          catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
          {
            swapRejected = true;
          }
        });

    var result = await resolver.ResolveAsync(
        source,
        Convert.ToHexString(SHA256.HashData(contents)),
        CancellationToken.None);

    Assert.True(result.IsValid, result.Error?.UnderlyingException?.ToString() ?? result.Error?.Detail);
    Assert.True(swapRejected);
    Assert.Equal(contents, result.Source!.Contents.ToArray());
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

  [Fact]
  public async Task CopyAtomicallyAsync_ReparseAncestorIsRejectedBeforeCreatingExternalDirectories()
  {
    var outside = Path.Combine(_root, "outside");
    var link = Path.Combine(_root, "linked");
    Directory.CreateDirectory(outside);
    CreateJunction(link, outside);

    var contents = Encoding.UTF8.GetBytes("replacement");
    var source = new ResolvedConfigurationSource(
        Path.Combine(_root, "source.DotSettings"),
        Convert.ToHexString(SHA256.HashData(contents)),
        contents);
    var destination = Path.Combine(link, "must-not-exist", "nested", "settings.DotSettings");

    try
    {
      var result = await new ConfigurationImporter().CopyAtomicallyAsync(
          source,
          destination,
          CancellationToken.None);

      Assert.False(result.Succeeded);
      Assert.Contains("reparse", result.Error!.Detail, StringComparison.OrdinalIgnoreCase);
      Assert.False(Directory.Exists(Path.Combine(outside, "must-not-exist")));
    }
    finally
    {
      Directory.Delete(link);
    }
  }

  [Fact]
  public async Task CopyAtomicallyAsync_HoldsDestinationHierarchyAgainstJunctionSwap()
  {
    var destinationDirectory = Path.Combine(_root, "trusted", "nested");
    var movedDirectory = Path.Combine(_root, "moved");
    var outside = Path.Combine(_root, "outside");
    Directory.CreateDirectory(destinationDirectory);
    Directory.CreateDirectory(outside);
    var destination = Path.Combine(destinationDirectory, "settings.DotSettings");
    var outsideDestination = Path.Combine(outside, "settings.DotSettings");
    var contents = Encoding.UTF8.GetBytes("verified replacement");
    var source = new ResolvedConfigurationSource(
        Path.Combine(_root, "source.DotSettings"),
        Convert.ToHexString(SHA256.HashData(contents)),
        contents);
    var swapRejected = false;
    var importer = new ConfigurationImporter(
        afterDestinationMove: null,
        afterDestinationDirectoryLeased: directory =>
        {
          try
          {
            Directory.Move(directory, movedDirectory);
            CreateJunction(directory, outside);
          }
          catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
          {
            swapRejected = true;
          }
        });

    var result = await importer.CopyAtomicallyAsync(
        source,
        destination,
        CancellationToken.None);

    Assert.True(result.Succeeded, result.Error?.UnderlyingException?.ToString() ?? result.Error?.Detail);
    Assert.True(swapRejected);
    Assert.Equal(contents, await File.ReadAllBytesAsync(destination));
    Assert.False(File.Exists(outsideDestination));
  }

  [Fact]
  public async Task CopyAtomicallyAsync_NonAsciiDestinationUsesUtf16DirectoryLease()
  {
    var destination = Path.Combine(_root, "trusted-测试-δ", "settings.DotSettings");
    var contents = Encoding.UTF8.GetBytes("verified replacement");
    var source = new ResolvedConfigurationSource(
        Path.Combine(_root, "source.DotSettings"),
        Convert.ToHexString(SHA256.HashData(contents)),
        contents);

    var result = await new ConfigurationImporter().CopyAtomicallyAsync(
        source,
        destination,
        CancellationToken.None);

    Assert.True(result.Succeeded, result.Error?.Detail);
    Assert.Equal(contents, await File.ReadAllBytesAsync(destination));
  }

  [Fact]
  public async Task CopyAtomicallyAsync_FinalDestinationHashMismatchRestoresPreviousDestination()
  {
    Directory.CreateDirectory(_root);
    var destination = Path.Combine(_root, "destination.DotSettings");
    await File.WriteAllTextAsync(destination, "original destination");
    var contents = Encoding.UTF8.GetBytes("verified replacement");
    var source = new ResolvedConfigurationSource(
        Path.Combine(_root, "source.DotSettings"),
        Convert.ToHexString(SHA256.HashData(contents)),
        contents);
    var importer = new ConfigurationImporter(
        committedPath => File.WriteAllText(committedPath, "tampered destination"));

    var result = await importer.CopyAtomicallyAsync(
        source,
        destination,
        CancellationToken.None);

    Assert.False(result.Succeeded);
    Assert.Contains("final destination", result.Error!.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Equal("original destination", await File.ReadAllTextAsync(destination));
  }

  public void Dispose()
  {
    if (Directory.Exists(_root))
    {
      Directory.Delete(_root, recursive: true);
    }
  }

  private static void CreateJunction(string path, string target)
  {
    var startInfo = new ProcessStartInfo("cmd.exe")
    {
      RedirectStandardError = true,
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("/d");
    startInfo.ArgumentList.Add("/c");
    startInfo.ArgumentList.Add("mklink");
    startInfo.ArgumentList.Add("/J");
    startInfo.ArgumentList.Add(path);
    startInfo.ArgumentList.Add(target);
    using var process = Process.Start(startInfo)!;
    process.WaitForExit();
    Assert.Equal(0, process.ExitCode);
  }
}
