using System.Text.Json.Serialization;

namespace Wdem.LegacySource.Models
{
  /// <summary>
  /// Represents the complete system state tracked by Wdem.LegacySource, including applied items,
  /// original values of system settings, and step execution history.
  /// </summary>
  public class StateData
  {
    [JsonPropertyName("applied_items")]
    public HashSet<string> AppliedItems { get; set; } = new();

    [JsonPropertyName("system_setting_originals")]
    public Dictionary<string, object> SystemSettingOriginals { get; set; } = new();

    [JsonPropertyName("step_history")]
    public Dictionary<string, StepResult> StepHistory { get; set; } = new();
  }
}
