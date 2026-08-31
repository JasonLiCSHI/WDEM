using System.Text.Json;
using System.Text.RegularExpressions;
using Wdem.Core.Tasks;
using Wdem.Core.Versions;

namespace Wdem.Core.Profiles;

public static class ProfileParser
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    AllowTrailingCommas = true
  };

  public static EnvironmentProfile Parse(string json)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(json);

    ProfileDto dto;
    try
    {
      dto = JsonSerializer.Deserialize<ProfileDto>(json, JsonOptions)
          ?? throw new FormatException("Profile JSON must contain an object.");
    }
    catch (JsonException exception)
    {
      throw new FormatException("Profile JSON is invalid.", exception);
    }

    var id = Required(dto.Id, "Profile id");
    var schemaVersion = dto.SchemaVersion ?? 1;
    if (schemaVersion != 1)
    {
      throw new FormatException($"Unsupported Profile schemaVersion '{schemaVersion}'.");
    }
    var version = Required(dto.Version, "Profile version");
    var displayName = Required(dto.DisplayName, "Profile displayName");
    if (dto.Tasks is null || dto.Tasks.Count == 0)
    {
      throw new FormatException("Profile must declare at least one task.");
    }

    var tasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal);
    foreach (var (taskIdValue, taskDto) in dto.Tasks)
    {
      var taskId = Required(taskIdValue, "Task id");
      if (taskDto is null)
      {
        throw new FormatException($"Task '{taskId}' must contain an object.");
      }

      var versionConstraint = Optional(taskDto.Version);
      if (versionConstraint is not null)
      {
        VersionConstraint.Parse(versionConstraint);
      }

      var task = new TaskDefinition(
          taskId,
          Required(taskDto.DisplayName, $"Task '{taskId}' displayName"),
          taskDto.Required,
          Strings(taskDto.DependsOn, $"Task '{taskId}' dependsOn"),
          versionConstraint,
          Optional(taskDto.PreferredVersion),
          Optional(taskDto.Source),
          Command(taskDto.Detect, $"Task '{taskId}' detect"),
          Commands(taskDto.Pre, $"Task '{taskId}' pre"),
          taskDto.Apply is null ? null : Command(taskDto.Apply, $"Task '{taskId}' apply"),
          Commands(taskDto.Post, $"Task '{taskId}' post"),
          Optional(taskDto.Description));

      tasks.Add(taskId, task);
    }

    foreach (var task in tasks.Values)
    {
      foreach (var dependencyId in task.DependsOn)
      {
        if (!tasks.ContainsKey(dependencyId))
        {
          throw new FormatException(
              $"Task '{task.Id}' depends on undeclared task '{dependencyId}'.");
        }
      }
    }

    return new EnvironmentProfile(
        id,
        version,
        displayName,
        Optional(dto.Description),
        tasks,
        schemaVersion);
  }

  private static IReadOnlyList<CommandDefinition> Commands(
      IReadOnlyList<CommandDto?>? commands,
      string field)
  {
    if (commands is null)
    {
      return Array.Empty<CommandDefinition>();
    }

    return commands
        .Select((command, index) => Command(command, $"{field}[{index}]"))
        .ToArray();
  }

  private static CommandDefinition Command(CommandDto? command, string field)
  {
    if (command is null)
    {
      throw new FormatException($"{field} must contain a command object.");
    }

    var executable = Required(command.Executable, $"{field} executable");
    var arguments = Strings(command.Arguments, $"{field} arguments");
    var versionPattern = Optional(command.VersionPattern);

    if (versionPattern is not null)
    {
      try
      {
        var regex = new Regex(versionPattern, RegexOptions.CultureInvariant);
        if (!regex.GetGroupNames().Contains("version", StringComparer.Ordinal))
        {
          throw new FormatException(
              $"{field} versionPattern must contain a named 'version' group.");
        }
      }
      catch (ArgumentException exception)
      {
        throw new FormatException($"{field} versionPattern is invalid.", exception);
      }
    }

    return new CommandDefinition(
        executable,
        arguments,
        versionPattern,
        Optional(command.DisplayName));
  }

  private static IReadOnlyList<string> Strings(IReadOnlyList<string?>? values, string field)
  {
    if (values is null)
    {
      return Array.Empty<string>();
    }

    if (values.Any(value => value is null))
    {
      throw new FormatException($"{field} cannot contain null values.");
    }

    return values.Cast<string>().ToArray();
  }

  private static string Required(string? value, string field) =>
      string.IsNullOrWhiteSpace(value)
          ? throw new FormatException($"{field} is required.")
          : value;

  private static string? Optional(string? value) =>
      string.IsNullOrWhiteSpace(value) ? null : value;

  private sealed class ProfileDto
  {
    public int? SchemaVersion { get; init; }

    public string? Id { get; init; }

    public string? Version { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public Dictionary<string, TaskDto?>? Tasks { get; init; }
  }

  private sealed class TaskDto
  {
    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public bool Required { get; init; }

    public List<string?>? DependsOn { get; init; }

    public string? Version { get; init; }

    public string? PreferredVersion { get; init; }

    public string? Source { get; init; }

    public CommandDto? Detect { get; init; }

    public List<CommandDto?>? Pre { get; init; }

    public CommandDto? Apply { get; init; }

    public List<CommandDto?>? Post { get; init; }
  }

  private sealed class CommandDto
  {
    public string? DisplayName { get; init; }

    public string? Executable { get; init; }

    public List<string?>? Arguments { get; init; }

    public string? VersionPattern { get; init; }
  }
}
