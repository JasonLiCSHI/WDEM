using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wdem.Cli;

internal static class WdemJson
{
  public static JsonSerializerOptions Options { get; } = CreateOptions();

  private static JsonSerializerOptions CreateOptions()
  {
    var options = new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    options.Converters.Add(new JsonStringEnumConverter(
        JsonNamingPolicy.CamelCase,
        allowIntegerValues: false));
    return options;
  }
}
