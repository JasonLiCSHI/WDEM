using Wdem.Core.Profiles;
using Wdem.Windows.Configuration;
using Xunit;

namespace Wdem.Windows.Tests;

public sealed class WdemUserSettingsStoreTests
{
  [Fact]
  public void Open_WhenSettingsAreMissing_UsesReleaseProfileSource()
  {
    var directory = CreateTempDirectory();
    try
    {
      var path = Path.Combine(directory, "settings.json");
      var store = WdemUserSettingsStore.Open(path, Path.Combine(directory, "cache"));

      var source = store.ProfileSource;
      Assert.Equal(WdemUserSettingsStore.OfficialSourceId, source.Id);
      Assert.Equal(WdemUserSettingsStore.OfficialSourceUrl, source.BaseUrl);
      Assert.True(File.Exists(path));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void Open_UserSettingsCannotOverrideReleaseProfileSource()
  {
    var directory = CreateTempDirectory();
    try
    {
      var path = Path.Combine(directory, "settings.json");
      File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "profileSources": [
            {
              "id": "unexpected",
              "displayName": "Unexpected",
              "baseUrl": "https://example.test/profiles/"
            }
          ],
          "trustedProfiles": []
        }
        """);

      var store = WdemUserSettingsStore.Open(path, Path.Combine(directory, "cache"));

      Assert.Equal(WdemUserSettingsStore.OfficialSourceId, store.ProfileSource.Id);
      Assert.Equal(WdemUserSettingsStore.OfficialSourceUrl, store.ProfileSource.BaseUrl);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void Trust_PersistsExactRemoteContentIdentity()
  {
    var directory = CreateTempDirectory();
    try
    {
      var path = Path.Combine(directory, "settings.json");
      var cache = Path.Combine(directory, "cache");
      var store = WdemUserSettingsStore.Open(path, cache);
      var profile = ProfileParser.Parse(ProfileJson);
      var loaded = new LoadedProfile(
          profile,
          ProfileOrigin.Remote,
          "https://example.test/profile.json",
          "ABC123",
          "official");

      Assert.False(store.IsTrusted(loaded));
      store.Trust(loaded);

      var reopened = WdemUserSettingsStore.Open(path, cache);
      Assert.True(reopened.IsTrusted(loaded));
      Assert.False(reopened.IsTrusted(loaded with { ContentHash = "CHANGED" }));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  private static string CreateTempDirectory()
  {
    var path = Path.Combine(Path.GetTempPath(), $"wdem-settings-{Guid.NewGuid():N}");
    Directory.CreateDirectory(path);
    return path;
  }

  private const string ProfileJson = """
    {
      "id": "test",
      "version": "1.0.0",
      "displayName": "Test",
      "tasks": {
        "task": {
          "displayName": "Task",
          "required": true,
          "detect": { "executable": "test.exe", "arguments": [] }
        }
      }
    }
    """;
}
