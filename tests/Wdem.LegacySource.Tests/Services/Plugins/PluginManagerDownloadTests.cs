using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using Moq;
using Wdem.LegacySource.Interfaces;
using Wdem.LegacySource.Services.Bootstrappers;
using Wdem.LegacySource.Services.Plugins;

namespace Wdem.LegacySource.Tests.Services.Plugins
{
  public class PluginManagerDownloadTests : IDisposable
  {
    private readonly string _pluginsDirectory = Path.Combine(Path.GetTempPath(), $"Wdem.PluginManagerTests_{Guid.NewGuid():N}");
    private readonly string? _originalWdemToken = Environment.GetEnvironmentVariable("WDEM_GITHUB_TOKEN");
    private readonly string? _originalGitHubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

    public void Dispose()
    {
      Environment.SetEnvironmentVariable("WDEM_GITHUB_TOKEN", _originalWdemToken);
      Environment.SetEnvironmentVariable("GITHUB_TOKEN", _originalGitHubToken);
      if (Directory.Exists(_pluginsDirectory)) Directory.Delete(_pluginsDirectory, true);
    }

    [Fact]
    public async Task EnsurePluginsInstalledAsync_RequiresAuthenticationForPrivateArchive()
    {
      Environment.SetEnvironmentVariable("WDEM_GITHUB_TOKEN", null);
      Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
      var handler = new RecordingHandler(HttpStatusCode.NotFound);
      using var client = new HttpClient(handler);
      var manager = CreateManager(client, out _);

      var exception = await Assert.ThrowsAsync<PluginDownloadException>(
          () => manager.EnsurePluginsInstalledAsync(["missing-plugin"]));

      Assert.Equal("github_authentication_required", exception.ErrorCode);
      Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
      Assert.Contains("WDEM_GITHUB_TOKEN", exception.Message);
      Assert.Null(handler.Authorization);
      Assert.True(handler.HasUserAgent);
    }

    [Fact]
    public async Task EnsurePluginsInstalledAsync_PrefersWdemTokenAndDoesNotLogIt()
    {
      const string wdemToken = "wdem-secret-token";
      Environment.SetEnvironmentVariable("WDEM_GITHUB_TOKEN", wdemToken);
      Environment.SetEnvironmentVariable("GITHUB_TOKEN", "github-fallback-token");
      var handler = new RecordingHandler(HttpStatusCode.Unauthorized);
      using var client = new HttpClient(handler);
      var manager = CreateManager(client, out var logger);

      var exception = await Assert.ThrowsAsync<PluginDownloadException>(
          () => manager.EnsurePluginsInstalledAsync(["missing-plugin"]));

      Assert.Equal("github_plugin_archive_request_failed", exception.ErrorCode);
      Assert.Equal("Bearer", handler.Authorization?.Scheme);
      Assert.Equal(wdemToken, handler.Authorization?.Parameter);
      logger.Verify(l => l.LogInfo(It.Is<string>(message => message.Contains(wdemToken))), Times.Never);
      logger.Verify(l => l.LogWarning(It.Is<string>(message => message.Contains(wdemToken))), Times.Never);
      logger.Verify(l => l.LogError(It.Is<string>(message => message.Contains(wdemToken))), Times.Never);
    }

    [Fact]
    public async Task EnsurePluginsInstalledAsync_UsesGitHubTokenWhenWdemTokenIsUnavailable()
    {
      const string githubToken = "github-fallback-token";
      Environment.SetEnvironmentVariable("WDEM_GITHUB_TOKEN", null);
      Environment.SetEnvironmentVariable("GITHUB_TOKEN", githubToken);
      var handler = new RecordingHandler(HttpStatusCode.Unauthorized);
      using var client = new HttpClient(handler);
      var manager = CreateManager(client, out _);

      await Assert.ThrowsAsync<PluginDownloadException>(
          () => manager.EnsurePluginsInstalledAsync(["missing-plugin"]));

      Assert.Equal("Bearer", handler.Authorization?.Scheme);
      Assert.Equal(githubToken, handler.Authorization?.Parameter);
      Assert.True(handler.HasUserAgent);
    }

    [Fact]
    public async Task EnsurePluginsInstalledAsync_ExtractsAuthenticatedArchiveAndCleansTemporaryFiles()
    {
      const string token = "private-repository-token";
      Environment.SetEnvironmentVariable("WDEM_GITHUB_TOKEN", token);
      Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
      var temporaryEntriesBefore = GetTemporaryPluginEntries();
      var handler = new RecordingHandler(HttpStatusCode.OK, CreatePluginArchive());
      using var client = new HttpClient(handler);
      var manager = CreateManager(client, out _);

      await manager.EnsurePluginsInstalledAsync(["sample-plugin"]);

      var installedManifest = Path.Combine(_pluginsDirectory, "sample-plugin", "plugin.yaml");
      Assert.True(File.Exists(installedManifest));
      Assert.Contains("install_info:", File.ReadAllText(installedManifest));
      Assert.Equal("Bearer", handler.Authorization?.Scheme);
      Assert.Equal(token, handler.Authorization?.Parameter);
      Assert.Equal(temporaryEntriesBefore, GetTemporaryPluginEntries());
    }

    private static byte[] CreatePluginArchive()
    {
      using var stream = new MemoryStream();
      using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
      {
        var manifest = archive.CreateEntry("WDEM-main/plugins/sample-plugin/plugin.yaml");
        using var writer = new StreamWriter(manifest.Open());
        writer.Write("name: sample-plugin\ntype: powershell\ninstall_info:\n  check_command: test\n");
      }

      return stream.ToArray();
    }

    private static HashSet<string> GetTemporaryPluginEntries()
    {
      var temporaryDirectory = Path.GetTempPath();
      return Directory.EnumerateFileSystemEntries(temporaryDirectory, "wdem-plugins-*.zip")
          .Concat(Directory.EnumerateFileSystemEntries(temporaryDirectory, "wdem-extract-*"))
          .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private PluginManager CreateManager(HttpClient client, out Mock<ILogger> logger)
    {
      logger = new Mock<ILogger>();
      var runner = new Mock<IProcessRunner>();
      return new PluginManager(new UvBootstrapper(runner.Object), new BunBootstrapper(runner.Object), logger.Object, _pluginsDirectory, httpClient: client);
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, byte[]? content = null) : HttpMessageHandler
    {
      public AuthenticationHeaderValue? Authorization { get; private set; }
      public bool HasUserAgent { get; private set; }

      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
        Authorization = request.Headers.Authorization;
        HasUserAgent = request.Headers.UserAgent.Any();
        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
          Content = new ByteArrayContent(content ?? [])
        });
      }
    }
  }
}
