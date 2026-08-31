using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wdem.Core.Profiles;

namespace Wdem.Windows.Configuration;

/// <summary>
/// Owns the release Profile Source definition and persisted content-trust decisions.
/// </summary>
public sealed class WdemUserSettingsStore
{
  public const string OfficialSourceId = "official";
  public const string OfficialSourceUrl =
      "https://raw.githubusercontent.com/JasonLiCSHI/WDEM/main/profiles/";

  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    WriteIndented = true
  };

  private readonly object _sync = new();
  private readonly HashSet<string> _trustedProfiles;
  private SettingsDocument _document;

  private WdemUserSettingsStore(string settingsPath, string cacheDirectory, SettingsDocument document)
  {
    SettingsPath = settingsPath;
    CacheDirectory = cacheDirectory;
    _document = document;
    _trustedProfiles = new HashSet<string>(
        document.TrustedProfiles ?? [],
        StringComparer.Ordinal);
    ProfileSource = new ProfileSourceDefinition(
        OfficialSourceId,
        "WDEM Official",
        OfficialSourceUrl);
  }

  public string SettingsPath { get; }

  public string CacheDirectory { get; }

  public ProfileSourceDefinition ProfileSource { get; }

  public static WdemUserSettingsStore OpenDefault()
  {
    var applicationData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wdem");
    return Open(
        Path.Combine(applicationData, "settings.json"),
        Path.Combine(applicationData, "cache", "profiles"));
  }

  public static WdemUserSettingsStore Open(string settingsPath, string cacheDirectory)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
    ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
    settingsPath = Path.GetFullPath(settingsPath);
    cacheDirectory = Path.GetFullPath(cacheDirectory);

    SettingsDocument document;
    if (File.Exists(settingsPath))
    {
      try
      {
        var json = File.ReadAllText(settingsPath, Encoding.UTF8);
        document = JsonSerializer.Deserialize<SettingsDocument>(json, JsonOptions)
            ?? throw new FormatException("WDEM settings must contain a JSON object.");
      }
      catch (JsonException exception)
      {
        throw new FormatException($"WDEM settings JSON is invalid: {settingsPath}", exception);
      }
    }
    else
    {
      document = CreateDefaultDocument();
      WriteDocument(settingsPath, document);
    }

    if (document.SchemaVersion != 1)
    {
      throw new FormatException(
          $"Unsupported WDEM settings schemaVersion '{document.SchemaVersion}'.");
    }

    return new WdemUserSettingsStore(settingsPath, cacheDirectory, document);
  }

  public bool IsTrusted(LoadedProfile profile)
  {
    ArgumentNullException.ThrowIfNull(profile);
    if (!profile.RequiresTrust)
    {
      return true;
    }

    lock (_sync)
    {
      return _trustedProfiles.Contains(profile.TrustIdentity);
    }
  }

  public void Trust(LoadedProfile profile)
  {
    ArgumentNullException.ThrowIfNull(profile);
    if (!profile.RequiresTrust)
    {
      return;
    }

    lock (_sync)
    {
      if (!_trustedProfiles.Add(profile.TrustIdentity))
      {
        return;
      }

      _document.TrustedProfiles = _trustedProfiles.Order(StringComparer.Ordinal).ToList();
      WriteDocument(SettingsPath, _document);
    }
  }

  private static SettingsDocument CreateDefaultDocument() => new()
  {
    SchemaVersion = 1,
    TrustedProfiles = []
  };

  private static void WriteDocument(string settingsPath, SettingsDocument document)
  {
    var directory = Path.GetDirectoryName(settingsPath)!;
    Directory.CreateDirectory(directory);
    var temporary = settingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
      File.WriteAllText(
          temporary,
          JsonSerializer.Serialize(document, JsonOptions),
          new UTF8Encoding(false));
      File.Move(temporary, settingsPath, overwrite: true);
    }
    finally
    {
      try
      {
        File.Delete(temporary);
      }
      catch
      {
        // Best-effort cleanup of WDEM's own temporary settings file.
      }
    }
  }

  private sealed class SettingsDocument
  {
    public int SchemaVersion { get; set; } = 1;

    public List<string>? TrustedProfiles { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
  }

}
