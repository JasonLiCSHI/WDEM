using System.CommandLine;
using System.CommandLine.Parsing;
using Wdem.Core.Execution;

namespace Wdem.Cli;

public interface IWdemCommandHandler
{
  Task<int> InspectAsync(
      RunRequest request,
      bool json,
      CancellationToken cancellationToken);

  Task<int> ApplyAsync(
      RunRequest request,
      bool json,
      CancellationToken cancellationToken);

  Task<int> RetryAsync(
      Guid runId,
      IReadOnlySet<string> resourceIds,
      bool json,
      CancellationToken cancellationToken);

  Task<int> ResumeAsync(
      Guid runId,
      bool json,
      CancellationToken cancellationToken);

  Task<int> ListRunsAsync(bool json, CancellationToken cancellationToken);
}

public static class WdemCliBuilder
{
  public static RootCommand Build(IWdemCommandHandler handler)
  {
    ArgumentNullException.ThrowIfNull(handler);

    var root = new RootCommand("Manage a Windows developer environment from a WDEM profile.");
    root.Subcommands.Add(BuildInspect(handler));
    root.Subcommands.Add(BuildApply(handler));
    root.Subcommands.Add(BuildRetry(handler));
    root.Subcommands.Add(BuildResume(handler));
    root.Subcommands.Add(BuildRuns(handler));
    return root;
  }

  private static Command BuildInspect(IWdemCommandHandler handler)
  {
    var profile = RequiredOption<string>("--profile", "Path to a developer profile.");
    var select = MultipleOption("--select", "Optional resource id to include.");
    var json = JsonOption();
    var command = new Command("inspect", "Inspect the environment without applying changes.");
    command.Options.Add(profile);
    command.Options.Add(select);
    command.Options.Add(json);
    command.SetAction((parseResult, cancellationToken) => handler.InspectAsync(
        CreateRunRequest(parseResult.GetValue(profile)!, parseResult.GetValue(select)),
        parseResult.GetValue(json),
        cancellationToken));
    return command;
  }

  private static Command BuildApply(IWdemCommandHandler handler)
  {
    var profile = RequiredOption<string>("--profile", "Path to a developer profile.");
    var select = MultipleOption("--select", "Optional resource id to include.");
    var maximumConcurrency = new Option<int>("--max-concurrency")
    {
      Description = "Maximum number of resources to apply concurrently (1-32).",
      DefaultValueFactory = _ => 4
    };
    maximumConcurrency.Validators.Add(result =>
    {
      var value = result.GetValueOrDefault<int>();
      if (value is < 1 or > 32)
      {
        result.AddError("--max-concurrency must be between 1 and 32.");
      }
    });
    var json = JsonOption();
    var command = new Command("apply", "Apply the selected developer profile.");
    command.Options.Add(profile);
    command.Options.Add(select);
    command.Options.Add(maximumConcurrency);
    command.Options.Add(json);
    command.SetAction((parseResult, cancellationToken) => handler.ApplyAsync(
        CreateRunRequest(
            parseResult.GetValue(profile)!,
            parseResult.GetValue(select),
            parseResult.GetValue(maximumConcurrency)),
        parseResult.GetValue(json),
        cancellationToken));
    return command;
  }

  private static Command BuildRetry(IWdemCommandHandler handler)
  {
    var run = RequiredOption<Guid>("--run", "Run id to retry.");
    var resource = MultipleOption("--resource", "Failed or blocked resource id to retry.");
    resource.Required = true;
    resource.Arity = ArgumentArity.OneOrMore;
    var json = JsonOption();
    var command = new Command("retry", "Retry failed or blocked resources from a run.");
    command.Options.Add(run);
    command.Options.Add(resource);
    command.Options.Add(json);
    command.SetAction((parseResult, cancellationToken) => handler.RetryAsync(
        parseResult.GetValue(run),
        ResourceIds(parseResult.GetValue(resource)),
        parseResult.GetValue(json),
        cancellationToken));
    return command;
  }

  private static Command BuildResume(IWdemCommandHandler handler)
  {
    var run = RequiredOption<Guid>("--run", "Run id to resume.");
    var json = JsonOption();
    var command = new Command("resume", "Resume an interrupted run.");
    command.Options.Add(run);
    command.Options.Add(json);
    command.SetAction((parseResult, cancellationToken) => handler.ResumeAsync(
        parseResult.GetValue(run),
        parseResult.GetValue(json),
        cancellationToken));
    return command;
  }

  private static Command BuildRuns(IWdemCommandHandler handler)
  {
    var json = JsonOption();
    var list = new Command("list", "List persisted environment runs.");
    list.Options.Add(json);
    list.SetAction((parseResult, cancellationToken) => handler.ListRunsAsync(
        parseResult.GetValue(json),
        cancellationToken));

    var runs = new Command("runs", "Inspect persisted environment runs.");
    runs.Subcommands.Add(list);
    return runs;
  }

  private static RunRequest CreateRunRequest(
      string profilePath,
      IEnumerable<string>? selectedResourceIds,
      int maximumConcurrency = 4) => new(
          Path.GetFullPath(profilePath),
          ResourceIds(selectedResourceIds),
          maximumConcurrency);

  private static IReadOnlySet<string> ResourceIds(IEnumerable<string>? values) =>
      (values ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

  private static Option<T> RequiredOption<T>(string name, string description) => new(name)
  {
    Description = description,
    Required = true
  };

  private static Option<string[]> MultipleOption(string name, string description) => new(name)
  {
    Description = description,
    Arity = ArgumentArity.OneOrMore
  };

  private static Option<bool> JsonOption() => new("--json")
  {
    Description = "Write newline-delimited JSON output."
  };
}
