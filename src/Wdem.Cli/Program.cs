using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Core.Runs;
using Wdem.Windows.Configuration;
using Wdem.Windows.Logging;
using Wdem.Windows.Processes;
using Wdem.Windows.Runtime;
using Wdem.Windows.Security;

namespace Wdem.Cli;

public static class Program
{
  public static async Task<int> Main(string[] args)
  {
    if (!AdministratorRequirement.IsSatisfied())
    {
      Console.Error.WriteLine(
          "Administrator privileges are required. Reopen Command Prompt, PowerShell, or Windows Terminal as administrator and run WDEM again.");
      return AdministratorRequirement.AccessDeniedExitCode;
    }

    if (args.Length == 0 || args[0] is "-h" or "--help")
    {
      PrintHelp();
      return args.Length == 0 ? 2 : 0;
    }

    var command = args[0];
    if (!command.Equals("profiles", StringComparison.OrdinalIgnoreCase) &&
        !command.Equals("inspect", StringComparison.OrdinalIgnoreCase) &&
        !command.Equals("apply", StringComparison.OrdinalIgnoreCase))
    {
      Console.Error.WriteLine($"Unknown command '{command}'.");
      PrintHelp();
      return 2;
    }

    var argumentError = ValidateArguments(command, args);
    if (argumentError is not null)
    {
      Console.Error.WriteLine(argumentError);
      PrintHelp();
      return 2;
    }

    WdemUserSettingsStore settings;
    try
    {
      settings = WdemUserSettingsStore.OpenDefault();
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 2;
    }

    if (command.Equals("profiles", StringComparison.OrdinalIgnoreCase))
    {
      return await ListProfilesAsync(settings);
    }

    var source = settings.ProfileSource;
    var catalog = new ProfileCatalog(source, settings.CacheDirectory);

    var profileArg = GetOption(args, "--profile") ?? GetOption(args, "-p");
    if (string.IsNullOrWhiteSpace(profileArg))
    {
      Console.Error.WriteLine("Missing --profile <id>.");
      return 2;
    }

    LoadedProfile loaded;
    try
    {
      loaded = await catalog.LoadAsync(profileArg);
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 2;
    }

    var profile = loaded.Profile;
    Console.WriteLine($"Loaded {profile.DisplayName} {profile.Version} from {loaded.Location}");

    try
    {
      if (loaded.RequiresTrust && !settings.IsTrusted(loaded))
      {
        if (HasFlag(args, "--trust-profile"))
        {
          settings.Trust(loaded);
        }
        else
        {
          if (Console.IsInputRedirected)
          {
            Console.Error.WriteLine(
                "Remote Profile is not trusted. Review it, then run again with --trust-profile.");
            return 3;
          }

          Console.Write(
              $"Trust this exact Profile version and allow its commands to run? {loaded.Location} [y/N] ");
          var trustAnswer = Console.ReadLine();
          if (!string.Equals(trustAnswer, "y", StringComparison.OrdinalIgnoreCase))
          {
            Console.Error.WriteLine("Profile was not trusted; no Detect or Apply command was run.");
            return 3;
          }
          settings.Trust(loaded);
        }
      }
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine($"Unable to persist Profile trust: {exception.Message}");
      return 2;
    }

    var runtime = new WindowsTaskRuntime(new DefaultProcessRunner());
    using var log = JsonLineSessionLog.Create("cli");
    log.Write("profile", $"Loaded {profile.Id} {profile.Version} from {loaded.Location}");
    Console.WriteLine($"Log: {log.DisplayPath}");
    if (log.LastError is not null)
    {
      Console.Error.WriteLine($"Log warning: {log.LastError}");
    }
    var progress = new CompositeWorkflowProgress(new ConsoleWorkflowProgress(), log);

    using var inspectCancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler inspectCancelHandler = (_, e) =>
    {
      e.Cancel = true;
      log.Write("cancel", "Ctrl+C requested safe cancellation during Detect.");
      inspectCancellation.Cancel();
    };
    Console.CancelKeyPress += inspectCancelHandler;
    InspectReport inspect;
    try
    {
      inspect = await EnvironmentInspector.InspectAsync(
          profile,
          runtime,
          progress,
          inspectCancellation.Token);
    }
    catch (OperationCanceledException)
    {
      Console.Error.WriteLine("Inspection cancelled safely.");
      log.Write("cancelled", "Inspection cancelled safely.");
      return 130;
    }
    finally
    {
      Console.CancelKeyPress -= inspectCancelHandler;
    }
    PrintInspect(inspect);

    if (command.Equals("inspect", StringComparison.OrdinalIgnoreCase))
    {
      log.Write("inspect", $"Satisfied {inspect.Tasks.Values.Count(task => task.IsSatisfied)}/{inspect.Tasks.Count}");
      return inspect.Tasks.Values.All(task => task.IsSatisfied) ? 0 : 1;
    }

    var selected = ParseCsv(GetOption(args, "--select"));
    var singleTask = GetOption(args, "--task");
    TaskGraph graph;
    try
    {
      if (!string.IsNullOrWhiteSpace(singleTask) && selected.Count > 0)
      {
        throw new ArgumentException("Use either --task or --select, not both.");
      }

      graph = string.IsNullOrWhiteSpace(singleTask)
          ? TaskGraph.BuildForSelection(profile, selectedOptionalTaskIds: selected)
          : TaskGraph.Build(profile, [singleTask]);
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 2;
    }

    Console.WriteLine("Plan:");
    foreach (var taskId in graph.OrderedTaskIds)
    {
      var task = profile.Tasks[taskId];
      PrintTaskPlan(task);
    }
    log.Write("plan", string.Join(" -> ", graph.OrderedTaskIds));

    var yes = HasFlag(args, "--yes") || HasFlag(args, "-y");
    if (!yes)
    {
      Console.Write("Apply this plan? [y/N] ");
      var answer = Console.ReadLine();
      if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
      {
        return 0;
      }
    }

    int retries;
    try
    {
      retries = ParseNonNegativeInt(GetOption(args, "--retries"), "--retries");
    }
    catch (ArgumentException exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 2;
    }

    RunReport report = null!;
    var cancelRequested = false;
    for (var attempt = 0; attempt <= retries; attempt++)
    {
      if (attempt > 0)
      {
        Console.WriteLine($"Retry {attempt}/{retries}: re-detecting the plan.");
        log.Write("retry", $"Attempt {attempt}/{retries}");
      }

      var run = EnvironmentManager.StartApply(profile, graph, runtime, progress);
      ConsoleCancelEventHandler cancelHandler = (_, e) =>
      {
        e.Cancel = true;
        cancelRequested = true;
        log.Write("cancel", "Ctrl+C requested safe cancellation of the active workflow.");
        run.CancelAll();
      };
      Console.CancelKeyPress += cancelHandler;
      try
      {
        report = await run.Completion;
      }
      finally
      {
        Console.CancelKeyPress -= cancelHandler;
      }

      if (report.Tasks.Values.All(task =>
          task.Outcome is TaskOutcome.Succeeded or TaskOutcome.NotRequired) ||
          cancelRequested)
      {
        break;
      }
    }

    PrintApply(report);
    foreach (var task in report.Tasks.Values)
    {
      log.Write("result", $"{task.TaskId}: {task.Outcome} {task.Error}", task);
    }
    log.Write("run_summary", "Workflow completed.", report);

    return report.Tasks.Values.All(task => task.Outcome is TaskOutcome.Succeeded or TaskOutcome.NotRequired) ? 0 : 1;
  }

  private static void PrintHelp()
  {
    Console.WriteLine("wdem profiles");
    Console.WriteLine("wdem inspect  --profile <id> [--trust-profile]");
    Console.WriteLine("wdem apply    --profile <id> [--select task1,task2 | --task task1] [--yes] [--retries N] [--trust-profile]");
  }

  private static string? ValidateArguments(string command, string[] args)
  {
    var valueOptions = command.Equals("profiles", StringComparison.OrdinalIgnoreCase)
        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(["--profile", "-p"], StringComparer.OrdinalIgnoreCase);
    var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (!command.Equals("profiles", StringComparison.OrdinalIgnoreCase))
    {
      flags.Add("--trust-profile");
    }
    if (command.Equals("apply", StringComparison.OrdinalIgnoreCase))
    {
      valueOptions.UnionWith(["--select", "--task", "--retries"]);
      flags.UnionWith(["--yes", "-y"]);
    }

    for (var index = 1; index < args.Length; index++)
    {
      var argument = args[index];
      if (flags.Contains(argument))
      {
        continue;
      }
      if (!valueOptions.Contains(argument))
      {
        return $"Unknown option '{argument}' for command '{command}'.";
      }
      if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
      {
        return $"Option '{argument}' requires a value.";
      }
      index++;
    }

    return null;
  }

  private static void PrintInspect(InspectReport report)
  {
    Console.WriteLine($"Profile: {report.Tasks.Count} task(s)");
    foreach (var task in report.Tasks.Values.OrderBy(value => value.TaskId, StringComparer.Ordinal))
    {
      var status = task.Compliance switch
      {
        TaskComplianceState.Satisfied => "OK",
        TaskComplianceState.Missing => "MISSING",
        TaskComplianceState.UpgradeRequired => "UPGRADE",
        TaskComplianceState.VersionMismatch => "MISMATCH",
        _ => "UNKNOWN"
      };
      var version = string.IsNullOrWhiteSpace(task.DetectedVersion) ? "" : $" ({task.DetectedVersion})";
      var requirement = task.Compliance == TaskComplianceState.UpgradeRequired &&
                        !string.IsNullOrWhiteSpace(task.VersionRequirement)
          ? $" -> requires {task.VersionRequirement}"
          : "";
      Console.WriteLine($"{status} {task.TaskId}{version}{requirement}");
    }
  }

  private static void PrintApply(RunReport report)
  {
    Console.WriteLine("Result:");
    foreach (var task in report.Tasks.Values.OrderBy(value => value.TaskId, StringComparer.Ordinal))
    {
      Console.WriteLine($"{task.TaskId}: {task.Outcome}");
    }
  }

  private static void PrintTaskPlan(Wdem.Core.Tasks.TaskDefinition task)
  {
    Console.WriteLine($"- {task.Id}: {task.DisplayName} [{(task.Required ? "required" : "optional")}]");
    if (!string.IsNullOrWhiteSpace(task.Description))
    {
      Console.WriteLine($"  description: {task.Description}");
    }
    Console.WriteLine($"  depends-on: {(task.DependsOn.Count == 0 ? "none" : string.Join(", ", task.DependsOn))}");
    Console.WriteLine($"  source: {task.Source ?? "none"}");
    Console.WriteLine($"  version: {task.VersionConstraint ?? "any"}");
    Console.WriteLine($"  preferred-version: {task.PreferredVersion ?? "none"}");
    PrintPhase("detect", [task.Detect]);
    PrintPhase("pre", task.Pre);
    PrintPhase("apply", task.Apply is null ? [] : [task.Apply]);
    PrintPhase("post", task.Post);
    Console.WriteLine("  verify: run detect again");
  }

  private static void PrintPhase(
      string phase,
      IReadOnlyList<Wdem.Core.Tasks.CommandDefinition> commands)
  {
    if (commands.Count == 0)
    {
      Console.WriteLine($"  {phase}: none");
      return;
    }

    for (var index = 0; index < commands.Count; index++)
    {
      var command = commands[index];
      var suffix = commands.Count == 1 ? string.Empty : $"[{index + 1}]";
      var displayName = string.IsNullOrWhiteSpace(command.DisplayName)
          ? string.Empty
          : $"{command.DisplayName} — ";
      Console.WriteLine(
          $"  {phase}{suffix}: {displayName}{FormatCommand(command.Executable, command.Arguments)}");
    }
  }

  private static string? GetOption(string[] args, string name)
  {
    for (var index = 0; index < args.Length - 1; index++)
    {
      if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
      {
        return args[index + 1];
      }
    }
    return null;
  }

  private static bool HasFlag(string[] args, string name) =>
      args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

  private static IReadOnlyCollection<string> ParseCsv(string? value) =>
      string.IsNullOrWhiteSpace(value)
          ? Array.Empty<string>()
          : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

  private static int ParseNonNegativeInt(string? value, string option)
  {
    if (value is null)
    {
      return 0;
    }

    if (!int.TryParse(value, out var parsed) || parsed < 0)
    {
      throw new ArgumentException($"{option} must be a non-negative integer.");
    }
    return parsed;
  }

  private static string FormatCommand(string executable, IReadOnlyList<string> arguments) =>
      string.Join(" ", new[] { executable }.Concat(arguments.Select(QuoteArgument)));

  private static string QuoteArgument(string value) =>
      value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;

  private static async Task<int> ListProfilesAsync(WdemUserSettingsStore settings)
  {
    try
    {
      var count = 0;
      var source = settings.ProfileSource;
      var catalog = new ProfileCatalog(source, settings.CacheDirectory);
      var entries = await catalog.ListAsync();
      foreach (var entry in entries)
      {
        Console.WriteLine(
            $"{entry.Id}\t{entry.Version}\t{entry.Origin}\t{entry.DisplayName}");
        count++;
      }

      if (count == 0)
      {
        Console.WriteLine("No Profiles are available.");
        return 1;
      }
      return 0;
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 2;
    }
  }

  private sealed class ConsoleWorkflowProgress : IProgress<WorkflowProgress>
  {
    public void Report(WorkflowProgress value)
    {
      if (value.Message is not null)
      {
        var writer = value.OutputStream == WorkflowOutputStream.StandardError
            ? Console.Error
            : Console.Out;
        writer.WriteLine($"[{value.TaskId}:{value.Stage}] {value.Message}");
        return;
      }

      if (value.State is not (
          TaskExecutionState.NotSelected or
          TaskExecutionState.Pending or
          TaskExecutionState.Ready))
      {
        Console.WriteLine($"[{value.TaskId}] {value.State} {value.Stage} {value.Percent}%");
      }
    }
  }

  private sealed class CompositeWorkflowProgress(params IProgress<WorkflowProgress>[] targets)
      : IProgress<WorkflowProgress>
  {
    public void Report(WorkflowProgress value)
    {
      foreach (var target in targets)
      {
        target.Report(value);
      }
    }
  }

}
