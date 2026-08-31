using System.Net;
using Wdem.Core.Profiles;
using Xunit;

namespace Wdem.Core.Tests;

public sealed class ProfileCatalogTests
{
  private static readonly ProfileSourceDefinition Source = new(
      "official",
      "WDEM Official",
      "https://raw.githubusercontent.com/example/WDEM/main/profiles/");

  [Fact]
  public async Task RemoteCatalog_DownloadsProfilesAndWritesLastKnownGoodCache()
  {
    var cache = CreateTempDirectory();
    try
    {
      using var client = CreateClient(request => request.RequestUri!.AbsolutePath.EndsWith("index.json")
          ? CatalogJson()
          : ProfileJson("Remote C#", "2.0.0"));
      var catalog = new ProfileCatalog(Source, cache, client);

      var entries = await catalog.ListAsync();
      var loaded = await catalog.LoadAsync("csharp-developer");

      var entry = Assert.Single(entries);
      Assert.Equal(ProfileOrigin.Remote, entry.Origin);
      Assert.Equal("official", entry.SourceId);
      Assert.Equal("Remote C#", loaded.Profile.DisplayName);
      Assert.Equal(ProfileOrigin.Remote, loaded.Origin);
      Assert.True(loaded.RequiresTrust);
      Assert.True(File.Exists(Path.Combine(cache, "official", "index.json")));
      Assert.True(File.Exists(Path.Combine(cache, "official", "csharp-developer.json")));
    }
    finally
    {
      Directory.Delete(cache, recursive: true);
    }
  }

  [Fact]
  public async Task Catalog_WhenRemoteIsOffline_UsesCachedIndexAndProfile()
  {
    var cache = CreateTempDirectory();
    try
    {
      using (var onlineClient = CreateClient(request => request.RequestUri!.AbsolutePath.EndsWith("index.json")
                 ? CatalogJson()
                 : ProfileJson("Cached C#", "1.0.0")))
      {
        var online = new ProfileCatalog(Source, cache, onlineClient);
        await online.ListAsync();
        await online.LoadAsync("csharp-developer");
      }

      using var offlineClient = new HttpClient(new StubHttpMessageHandler(_ =>
          throw new HttpRequestException("offline")));
      var offline = new ProfileCatalog(Source, cache, offlineClient);

      var entries = await offline.ListAsync();
      var loaded = await offline.LoadAsync("csharp-developer");

      Assert.Equal(ProfileOrigin.Cache, Assert.Single(entries).Origin);
      Assert.Equal(ProfileOrigin.Cache, loaded.Origin);
      Assert.Equal("Cached C#", loaded.Profile.DisplayName);
      Assert.True(loaded.RequiresTrust);
    }
    finally
    {
      Directory.Delete(cache, recursive: true);
    }
  }

  [Fact]
  public async Task Catalog_WhenRemoteAndCacheAreUnavailable_ReportsClearError()
  {
    var cache = CreateTempDirectory();
    try
    {
      using var client = new HttpClient(new StubHttpMessageHandler(_ =>
          throw new HttpRequestException("offline")));
      var catalog = new ProfileCatalog(Source, cache, client);

      var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ListAsync());

      Assert.Contains("no local cache", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
      Directory.Delete(cache, recursive: true);
    }
  }

  [Fact]
  public async Task Catalog_FreshRemoteProfileReplacesStaleCache()
  {
    var cache = CreateTempDirectory();
    try
    {
      Directory.CreateDirectory(Path.Combine(cache, "official"));
      await File.WriteAllTextAsync(
          Path.Combine(cache, "official", "csharp-developer.json"),
          ProfileJson("Stale", "1.0.0"));
      using var client = CreateClient(_ => ProfileJson("Fresh", "2.0.0"));
      var catalog = new ProfileCatalog(Source, cache, client);

      var loaded = await catalog.LoadAsync("csharp-developer");

      Assert.Equal(ProfileOrigin.Remote, loaded.Origin);
      Assert.Equal("Fresh", loaded.Profile.DisplayName);
      Assert.Contains(
          "Fresh",
          await File.ReadAllTextAsync(Path.Combine(cache, "official", "csharp-developer.json")));
    }
    finally
    {
      Directory.Delete(cache, recursive: true);
    }
  }

  [Fact]
  public async Task Catalog_RemoteNotFoundDoesNotHideBehindStaleCache()
  {
    var cache = CreateTempDirectory();
    try
    {
      Directory.CreateDirectory(Path.Combine(cache, "official"));
      await File.WriteAllTextAsync(Path.Combine(cache, "official", "index.json"), CatalogJson());
      using var client = new HttpClient(new StubHttpMessageHandler(_ =>
          new HttpResponseMessage(HttpStatusCode.NotFound)));
      var catalog = new ProfileCatalog(Source, cache, client);

      await Assert.ThrowsAsync<HttpRequestException>(() => catalog.ListAsync());
    }
    finally
    {
      Directory.Delete(cache, recursive: true);
    }
  }

  [Fact]
  public void ProfileSourceDefinition_RejectsNonHttpsSources()
  {
    var exception = Assert.Throws<ArgumentException>(() =>
        new ProfileSourceDefinition("insecure", "Insecure", "http://example.test/profiles/"));

    Assert.Contains("HTTPS", exception.Message);
  }

  private static HttpClient CreateClient(Func<HttpRequestMessage, string> response) =>
      new(new StubHttpMessageHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(response(request))
      }));

  private static string CreateTempDirectory()
  {
    var path = Path.Combine(Path.GetTempPath(), $"wdem-{Guid.NewGuid():N}");
    Directory.CreateDirectory(path);
    return path;
  }

  private static string ProfileJson(string displayName, string version) => $$"""
    {
      "schemaVersion": 1,
      "id": "csharp-developer",
      "version": "{{version}}",
      "displayName": "{{displayName}}",
      "tasks": {
        "git": {
          "displayName": "Git",
          "required": true,
          "detect": { "executable": "git", "arguments": ["--version"] }
        }
      }
    }
    """;

  private static string CatalogJson() => """
    {
      "profiles": [
        {
          "id": "csharp-developer",
          "version": "1.0.0",
          "displayName": "C# Developer"
        }
      ]
    }
    """;

  private sealed class StubHttpMessageHandler(
      Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(handler(request));
  }
}
