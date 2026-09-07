using System.Text.Json;

namespace Wdem.Infrastructure.Profiles;

internal static class ProfileDocumentDeserializer
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    AllowTrailingCommas = true
  };

  public static ProfileDocument Deserialize(string json)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(json);

    try
    {
      return JsonSerializer.Deserialize<ProfileDocument>(json, JsonOptions)
          ?? throw new FormatException("Profile JSON must contain an object.");
    }
    catch (JsonException exception)
    {
      throw new FormatException("Profile JSON is invalid.", exception);
    }
  }
}
