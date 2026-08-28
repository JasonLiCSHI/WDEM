using System.Diagnostics;
using global::System.IO;
using global::System.IO.Compression;
using global::System.Net;
using global::System.Net.Http;
using global::System.Net.Http.Headers;
using Wdem.LegacySource.Interfaces;
using Wdem.LegacySource.Models.Plugins;
using Wdem.LegacySource.Services.Bootstrappers;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Wdem.LegacySource.Services.Plugins
{
  /// <summary>Describes a failure while retrieving the private WDEM plugin archive.</summary>
  public sealed class PluginDownloadException : InvalidOperationException
  {
    public PluginDownloadException(string errorCode, HttpStatusCode? statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
      ErrorCode = errorCode;
      StatusCode = statusCode;
    }

    public string ErrorCode { get; }

    public HttpStatusCode? StatusCode { get; }
  }

  /// <summary>Discovers plugins from the plugins directory and ensures their runtimes are available.</summary>
  public class PluginManager : IPluginManager
  {
    private readonly UvBootstrapper _uvBootstrapper;
    private readonly BunBootstrapper _bunBootstrapper;
    private readonly ILogger _logger;
    private readonly string _pluginsDir;
    private readonly IRuntimeResolver? _runtimeResolver;
    private readonly HttpClient _httpClient;

    /// <summary>Initializes a new instance of <see cref="PluginManager"/>.</summary>
    public PluginManager(
        UvBootstrapper uvBootstrapper,
        BunBootstrapper bunBootstrapper,
        ILogger logger,
        string? pluginsDirectory = null,
        IRuntimeResolver? runtimeResolver = null,
        HttpClient? httpClient = null)
    {
      _uvBootstrapper = uvBootstrapper;
      _bunBootstrapper = bunBootstrapper;
      _logger = logger;
      _runtimeResolver = runtimeResolver;
      _httpClient = httpClient ?? new HttpClient();

      _pluginsDir = pluginsDirectory ?? Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
          "WDEM",
          "plugins");
    }

    /// <summary>Scans the plugins directory for plugin.yaml manifests and returns them.</summary>
    public IEnumerable<PluginManifest> DiscoverPlugins()
    {
      if (!Directory.Exists(_pluginsDir))
      {
        return Enumerable.Empty<PluginManifest>();
      }

      var plugins = new List<PluginManifest>();
      var deserializer = new DeserializerBuilder()
          .WithNamingConvention(CamelCaseNamingConvention.Instance)
          .IgnoreUnmatchedProperties()
          .Build();

      foreach (var dir in Directory.GetDirectories(_pluginsDir))
      {
        var manifestPath = Path.Combine(dir, "plugin.yaml");
        if (File.Exists(manifestPath))
        {
          try
          {
            var content = File.ReadAllText(manifestPath);
            var manifest = deserializer.Deserialize<PluginManifest>(content);
            manifest.DirectoryPath = dir;
            plugins.Add(manifest);
          }
          catch (Exception ex)
          {
            _logger.LogError($"[Plugin] Failed to load manifest in {dir}: {ex.Message}");
          }
        }
      }

      return plugins;
    }

    /// <summary>Ensures the runtime required by the plugin type (Python/uv, TypeScript/bun, PowerShell) is installed.</summary>
    public async Task EnsureRuntimeAsync(PluginManifest plugin)
    {
      switch (plugin.Type.ToLower())
      {
        case "python":
          if (!_uvBootstrapper.IsInstalled())
          {
            _logger.LogInfo($"[Plugin] {plugin.Name} requires 'uv'. Installing...");
            await Task.Run(() => _uvBootstrapper.Install(false));
          }
          break;

        case "typescript":
        case "javascript":
          if (!_bunBootstrapper.IsInstalled())
          {
            _logger.LogInfo($"[Plugin] {plugin.Name} requires 'bun'. Installing...");
            await Task.Run(() => _bunBootstrapper.Install(false));
          }
          break;

        case "powershell":
          string resolvedMessage = "Assuming system powershell is available.";
          if (_runtimeResolver != null)
          {
            try
            {
              var pwshResolved = _runtimeResolver.Resolve("pwsh");
              if (pwshResolved != "pwsh")
              {
                resolvedMessage = "Using pwsh (Core).";
              }
              else
              {
                resolvedMessage = "Falling back to Windows PowerShell.";
              }
            }
            catch
            {
              resolvedMessage = "Falling back to Windows PowerShell.";
            }
          }
          _logger.LogInfo($"[Plugin] {plugin.Name} requires 'powershell'. {resolvedMessage}");
          break;
      }
    }

    /// <summary>Downloads and installs missing plugins from the remote repository archive.</summary>
    public async Task EnsurePluginsInstalledAsync(IEnumerable<string> configuredPluginNames)
    {
      if (!Directory.Exists(_pluginsDir))
      {
        Directory.CreateDirectory(_pluginsDir);
      }

      var missingPlugins = configuredPluginNames.Where(name =>
      {
        var manifestPath = Path.Combine(_pluginsDir, name, "plugin.yaml");
        if (!File.Exists(manifestPath)) return true;
        try
        {
          var text = File.ReadAllText(manifestPath);
          return !text.Contains("install_info:");
        }
        catch
        {
          return true;
        }
      }).ToList();

      if (!missingPlugins.Any())
      {
        return;
      }

      _logger.LogInfo($"[PluginManager] Missing local plugins: {string.Join(", ", missingPlugins)}. Downloading fresh plugin pack from GitHub...");

      var tempZipPath = Path.Combine(Path.GetTempPath(), $"wdem-plugins-{Guid.NewGuid()}.zip");
      var tempExtractPath = Path.Combine(Path.GetTempPath(), $"wdem-extract-{Guid.NewGuid()}");
      try
      {
        var zipUrl = "https://codeload.github.com/JasonLiCSHI/WDEM/zip/refs/heads/main";
        var token = GetGitHubToken();

        using (var request = new HttpRequestMessage(HttpMethod.Get, zipUrl))
        using (var response = await SendArchiveRequestAsync(request, token))
        {
          if (!response.IsSuccessStatusCode)
          {
            throw CreateDownloadException(response.StatusCode, token is not null);
          }

          using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
          {
            await response.Content.CopyToAsync(fs);
          }
        }

        ZipFile.ExtractToDirectory(tempZipPath, tempExtractPath);

        var extractedPluginsDir = Path.Combine(tempExtractPath, "WDEM-main", "plugins");
        if (Directory.Exists(extractedPluginsDir))
        {
          foreach (var dir in Directory.GetDirectories(extractedPluginsDir))
          {
            var pluginName = Path.GetFileName(dir);
            var targetDir = Path.Combine(_pluginsDir, pluginName);
            if (Directory.Exists(targetDir))
            {
              Directory.Delete(targetDir, true);
            }
            CopyDirectory(dir, targetDir);
          }
          _logger.LogSuccess("[PluginManager] Plugin pack downloaded and extracted successfully.");
        }
        else
        {
          throw new PluginDownloadException(
              "github_plugin_archive_invalid",
              null,
              "The downloaded WDEM plugin archive does not contain a plugins directory.");
        }
      }
      catch (PluginDownloadException)
      {
        throw;
      }
      catch (Exception ex)
      {
        throw new PluginDownloadException(
            "github_plugin_download_failed",
            null,
            "Failed to download the WDEM plugin archive.",
            ex);
      }
      finally
      {
        TryDeleteTemporaryPluginFiles(tempZipPath, tempExtractPath);
      }
    }

    private async Task<HttpResponseMessage> SendArchiveRequestAsync(HttpRequestMessage request, string? token)
    {
      request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Wdem.LegacySource", "1.0"));
      if (token is not null)
      {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
      }

      return await _httpClient.SendAsync(request);
    }

    private static string? GetGitHubToken()
    {
      var wdemToken = Environment.GetEnvironmentVariable("WDEM_GITHUB_TOKEN");
      if (!string.IsNullOrWhiteSpace(wdemToken)) return wdemToken;

      var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
      return string.IsNullOrWhiteSpace(githubToken) ? null : githubToken;
    }

    private static PluginDownloadException CreateDownloadException(HttpStatusCode statusCode, bool hasToken)
    {
      if (!hasToken && (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.NotFound))
      {
        return new PluginDownloadException(
            "github_authentication_required",
            statusCode,
            $"The private WDEM plugin archive returned {(int)statusCode}. Set WDEM_GITHUB_TOKEN or GITHUB_TOKEN with repository read access.");
      }

      return new PluginDownloadException(
          "github_plugin_archive_request_failed",
          statusCode,
          $"The WDEM plugin archive request failed with HTTP {(int)statusCode}.");
    }

    private static void TryDeleteTemporaryPluginFiles(string zipPath, string extractPath)
    {
      try
      {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
      }
      catch (Exception)
      {
        // Temporary cleanup must not hide the retrieval error.
      }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
      Directory.CreateDirectory(destinationDir);
      foreach (var file in Directory.GetFiles(sourceDir))
      {
        File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true);
      }
      foreach (var subDir in Directory.GetDirectories(sourceDir))
      {
        CopyDirectory(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
      }
    }
  }
}
