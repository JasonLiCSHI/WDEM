using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wdem.LegacySource.Interfaces;

namespace Wdem.LegacySource.Services.Bootstrappers
{
  /// <summary>Bootstraps the Winget package manager by downloading from GitHub Releases.</summary>
  public class WingetBootstrapper : IPackageManagerBootstrapper
  {
    private readonly IProcessRunner _processRunner;
    private readonly ILogger _logger;
    public string Name => "Winget";

    /// <summary>Initializes a new instance of <see cref="WingetBootstrapper"/>.</summary>
    public WingetBootstrapper(IProcessRunner processRunner, ILogger logger)
    {
      _processRunner = processRunner;
      _logger = logger;
    }

    /// <summary>Returns <c>true</c> if winget is available (checks PATH and WindowsApps location).</summary>
    public bool IsInstalled()
    {
      if (_processRunner.RunCommand("winget", new[] { "--version" }, false)) return true;

      string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      string wingetPath = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
      bool exists = File.Exists(wingetPath);
      if (exists) _logger.LogInfo($"[Bootstrapper] Found winget.exe at {wingetPath} but it's not in PATH.");
      return exists;
    }

    /// <summary>Downloads and installs Winget from the latest GitHub release, including dependencies.</summary>
    public void Install(bool dryRun)
    {
      if (dryRun)
      {
        _logger.LogWarning($"[DryRun] Would install {Name} by downloading from GitHub.");
        return;
      }

      _logger.LogInfo($"[Bootstrapper] Installing {Name}...");

      try
      {
        // FIX: Use a randomized name with a "Wdem.LegacySource_" prefix for debuggability
        string tempDir = Path.Combine(Path.GetTempPath(), "Wdem.LegacySource_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        // FIX: Platform guard for Windows-only ACL APIs to prevent crashes on Linux CI
        if (OperatingSystem.IsWindows())
        {
          try
          {
            // Use normal initialization, DirectorySecurity is not IDisposable
            var security = new global::System.Security.AccessControl.DirectorySecurity();
            var currentUser = global::System.Security.Principal.WindowsIdentity.GetCurrent().Name;

            // Define inheritance flags so files/folders inside the temp dir inherit these permissions
            var inheritanceFlags = global::System.Security.AccessControl.InheritanceFlags.ContainerInherit | global::System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            var propagationFlags = global::System.Security.AccessControl.PropagationFlags.None;

            var systemSid = new global::System.Security.Principal.SecurityIdentifier(global::System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            var adminSid = new global::System.Security.Principal.SecurityIdentifier(global::System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);

            security.AddAccessRule(new global::System.Security.AccessControl.FileSystemAccessRule(currentUser, global::System.Security.AccessControl.FileSystemRights.FullControl, inheritanceFlags, propagationFlags, global::System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new global::System.Security.AccessControl.FileSystemAccessRule(systemSid, global::System.Security.AccessControl.FileSystemRights.FullControl, inheritanceFlags, propagationFlags, global::System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new global::System.Security.AccessControl.FileSystemAccessRule(adminSid, global::System.Security.AccessControl.FileSystemRights.FullControl, inheritanceFlags, propagationFlags, global::System.Security.AccessControl.AccessControlType.Allow));

#pragma warning disable CA1416
            var di = new global::System.IO.DirectoryInfo(tempDir);
            di.SetAccessControl(security);
#pragma warning restore CA1416
          }
          catch (Exception ex)
          {
            _logger.LogWarning($"[Bootstrapper] Could not set ACL: {ex.Message}.");
          }
        }

        string version = GetLatestVersion();
        _logger.LogInfo($"[Bootstrapper] Latest Winget version detected: {version}");

        string dependenciesUrl = $"https://github.com/microsoft/winget-cli/releases/download/{version}/DesktopAppInstaller_Dependencies.zip";
        string msixBundleUrl = $"https://github.com/microsoft/winget-cli/releases/download/{version}/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle";

        string dependenciesZip = Path.Combine(tempDir, "dependencies.zip");
        string msixBundle = Path.Combine(tempDir, "Microsoft.DesktopAppInstaller.msixbundle");

        DownloadFile(dependenciesUrl, dependenciesZip).GetAwaiter().GetResult();
        DownloadFile(msixBundleUrl, msixBundle).GetAwaiter().GetResult();

        string extractPath = Path.Combine(tempDir, "dependencies");
        ZipFile.ExtractToDirectory(dependenciesZip, extractPath);

        _logger.LogInfo("[Bootstrapper] Installing dependencies...");
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLower(); // x64, arm64, x86

        var files = Directory.GetFiles(extractPath, "*", SearchOption.AllDirectories);
        foreach (string file in files)
        {
          string fileName = Path.GetFileName(file).ToLower();
          if (fileName.EndsWith(".appx") || fileName.EndsWith(".msix") || fileName.EndsWith(".appxbundle") || fileName.EndsWith(".msixbundle"))
          {
            // Filter by architecture to avoid noise/failures
            if (fileName.Contains("arm64") && arch != "arm64") continue;
            if (fileName.Contains("x64") && arch != "x64") continue;
            if (fileName.Contains("x86") && arch != "x86" && arch != "x64") continue;

            _logger.LogInfo($"[Bootstrapper] Installing dependency: {Path.GetFileName(file)}");
            InstallAppPackage(file);
          }
        }

        _logger.LogInfo("[Bootstrapper] Installing Winget msixbundle...");
        InstallAppPackage(msixBundle);

        _logger.LogInfo($"[Bootstrapper] {Name} installation commands completed.");
        _logger.LogWarning("[Bootstrapper] Installation complete");

        // Cleanup
        try
        {
          _logger.LogInfo("[Bootstrapper] Cleaning up temporary files...");
          Directory.Delete(tempDir, true);
        }
        catch (Exception ex)
        {
          _logger.LogWarning($"[Bootstrapper] Warning: Could not clean up temp directory: {ex.Message}");
        }
      }
      catch (Exception ex)
      {
        throw new Exception($"Failed to install {Name}: {ex.Message}", ex);
      }
    }

    public async Task InstallAsync(bool dryRun, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (dryRun)
      {
        _logger.LogWarning($"[DryRun] Would install {Name} by downloading from GitHub.");
        return;
      }

      _logger.LogInfo($"[Bootstrapper] Installing {Name}...");
      string tempDir = Path.Combine(Path.GetTempPath(), "Wdem.LegacySource_" + Path.GetRandomFileName());
      Directory.CreateDirectory(tempDir);

      try
      {
        string version = await GetLatestVersionAsync(cancellationToken);
        _logger.LogInfo($"[Bootstrapper] Latest Winget version detected: {version}");

        string dependenciesUrl = $"https://github.com/microsoft/winget-cli/releases/download/{version}/DesktopAppInstaller_Dependencies.zip";
        string msixBundleUrl = $"https://github.com/microsoft/winget-cli/releases/download/{version}/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle";
        string dependenciesZip = Path.Combine(tempDir, "dependencies.zip");
        string msixBundle = Path.Combine(tempDir, "Microsoft.DesktopAppInstaller.msixbundle");

        await DownloadFile(dependenciesUrl, dependenciesZip, cancellationToken);
        await DownloadFile(msixBundleUrl, msixBundle, cancellationToken);

        string extractPath = Path.Combine(tempDir, "dependencies");
        ZipFile.ExtractToDirectory(dependenciesZip, extractPath);
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        foreach (string file in Directory.GetFiles(extractPath, "*", SearchOption.AllDirectories))
        {
          cancellationToken.ThrowIfCancellationRequested();
          string fileName = Path.GetFileName(file).ToLowerInvariant();
          if (!IsAppPackage(fileName) || !MatchesArchitecture(fileName, arch))
          {
            continue;
          }

          _logger.LogInfo($"[Bootstrapper] Installing dependency: {Path.GetFileName(file)}");
          await InstallAppPackageAsync(file, cancellationToken);
        }

        _logger.LogInfo("[Bootstrapper] Installing Winget msixbundle...");
        await InstallAppPackageAsync(msixBundle, cancellationToken);
        _logger.LogSuccess($"[Bootstrapper] {Name} installation completed.");
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception ex)
      {
        throw new Exception($"Failed to install {Name}: {ex.Message}", ex);
      }
      finally
      {
        try
        {
          Directory.Delete(tempDir, true);
        }
        catch (Exception ex)
        {
          _logger.LogWarning(
              $"[Bootstrapper] Warning: Could not clean up temp directory: {ex.Message}");
        }
      }
    }

    /// <summary>Gets the latest winget-cli version tag from GitHub Releases API.</summary>
    private string GetLatestVersion()
    {
      using var client = new HttpClient();
      client.Timeout = TimeSpan.FromMinutes(5);
      client.DefaultRequestHeaders.Add("User-Agent", "Wdem.LegacySource-Bootstrapper");
      var response = client.GetAsync("https://api.github.com/repos/microsoft/winget-cli/releases/latest").GetAwaiter().GetResult();
      response.EnsureSuccessStatusCode();
      var json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
      return json.RootElement.GetProperty("tag_name").GetString() ?? "v1.12.460";
    }

    /// <summary>Downloads a file from a URL to the specified path with a long timeout for large files.</summary>
    private async Task DownloadFile(
        string url,
        string path,
        CancellationToken cancellationToken = default)
    {
      _logger.LogInfo($"[Bootstrapper] Downloading {url}...");
      using var client = new HttpClient();
      client.Timeout = TimeSpan.FromMinutes(10); // Increase timeout for large files
      var response = await client.GetAsync(
          url,
          HttpCompletionOption.ResponseHeadersRead,
          cancellationToken);
      response.EnsureSuccessStatusCode();
      await using var fs = new FileStream(path, FileMode.Create);
      await response.Content.CopyToAsync(fs, cancellationToken);
    }

    /// <summary>Installs an .appx/.msix package via PowerShell's Add-AppxPackage.</summary>
    private void InstallAppPackage(string path)
    {
      // Use Add-AppxPackage via PowerShell
      string command = $"Add-AppxPackage -Path \"{path}\"";
      string output = "";
      if (!_processRunner.RunCommand("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", command }, false, line => output += line + "\n"))
      {
        _logger.LogWarning($"[Bootstrapper] Warning: Package {Path.GetFileName(path)} failed to install.");
        if (!string.IsNullOrWhiteSpace(output))
        {
          _logger.LogWarning($"[Bootstrapper:Error] {output.Trim()}");
        }
      }
    }

    private async Task<string> GetLatestVersionAsync(CancellationToken cancellationToken)
      {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Add("User-Agent", "Wdem.LegacySource-Bootstrapper");
        using var response = await client.GetAsync(
            "https://api.github.com/repos/microsoft/winget-cli/releases/latest",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("tag_name").GetString() ?? "v1.12.460";
      }

    private async Task InstallAppPackageAsync(
          string path,
          CancellationToken cancellationToken)
      {
        string command = $"Add-AppxPackage -Path \"{path}\"";
        var output = new List<string>();
        var success = await _processRunner.RunCommandAsync(
            "powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-Command", command },
            false,
            output.Add,
            cancellationToken);
        if (!success)
        {
          throw new Exception(
              $"Package {Path.GetFileName(path)} failed to install: " +
              string.Join(Environment.NewLine, output));
        }
      }

    private static bool IsAppPackage(string fileName) =>
          fileName.EndsWith(".appx", StringComparison.Ordinal) ||
          fileName.EndsWith(".msix", StringComparison.Ordinal) ||
          fileName.EndsWith(".appxbundle", StringComparison.Ordinal) ||
          fileName.EndsWith(".msixbundle", StringComparison.Ordinal);

    private static bool MatchesArchitecture(string fileName, string architecture)
      {
        if (fileName.Contains("arm64", StringComparison.Ordinal) && architecture != "arm64") return false;
        if (fileName.Contains("x64", StringComparison.Ordinal) && architecture != "x64") return false;
        if (fileName.Contains("x86", StringComparison.Ordinal) &&
            architecture != "x86" &&
            architecture != "x64") return false;
        return true;
    }
  }
}
