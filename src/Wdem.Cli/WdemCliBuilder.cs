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

  Task<int> InspectAsync(
      RunRequest request,
      bool json,
      string? reportFile,
      CancellationToken cancellationToken) => InspectAsync(request, json, cancellationToken);

  Task<int> ApplyAsync(
      RunRequest request,
      bool json,
      CancellationToken cancellationToken);

  Task<int> ApplyAsync(
      RunRequest request,
      bool json,
      string? reportFile,
      CancellationToken cancellationToken) => ApplyAsync(request, json, cancellationToken);

  Task<int> RetryAsync(
      Guid runId,
      IReadOnlySet<string> resourceIds,
      bool json,
      CancellationToken cancellationToken);

  Task<int> RetryAsync(
      Guid runId,
      IReadOnlySet<string> resourceIds,
      bool json,
      string? reportFile,
      CancellationToken cancellationToken) =>
      RetryAsync(runId, resourceIds, json, cancellationToken);

  Task<int> ResumeAsync(
      Guid runId,
      bool json,
      CancellationToken cancellationToken);

  Task<int> ResumeAsync(
      Guid runId,
      bool json,
      string? reportFile,
      CancellationToken cancellationToken) => ResumeAsync(runId, json, cancellationToken);

  Task<int> ListRunsAsync(bool json, CancellationToken cancellationToken);
}

public static class WdemCliBuilder
{
  public static RootCommand Build(
      IWdemCommandHandler handler,
      Func<Exception, bool, CancellationToken, Task<int>>? exceptionHandler = null)
  {
    ArgumentNullException.ThrowIfNull(handler);

    var root = new RootCommand("Manage a Windows developer environment from a WDEM profile.");
    root.Subcommands.Add(BuildInspect(handler, exceptionHandler));
    root.Subcommands.Add(BuildApply(handler, exceptionHandler));
    root.Subcommands.Add(BuildRetry(handler, exceptionHandler));
    root.Subcommands.Add(BuildResume(handler, exceptionHandler));
    root.Subcommands.Add(BuildRuns(handler, exceptionHandler));
    return root;
  }

  private static Command BuildInspect(
      IWdemCommandHandler handler,
      Func<Exception, bool, CancellationToken, Task<int>>? exceptionHandler)
  {
    var profile = RequiredOption<string>("--profile", "Path to a developer profile.");
    var select = MultipleOption("--select", "Optional resource id to include.");
    var json = JsonOption();
    var report = ReportOption();
    var command = new Command("inspect", "Inspect the environment without applying changes.");
    command.Options.Add(profile);
    command.Options.Add(select);
    command.Options.Add(json);
    command.Options.Add(report);
    command.SetAction((parseResult, cancellationToken) => InvokeAsync(
        () => handler.InspectAsync(
            CreateRunRequest(parseResult.GetValue(profile)!, parseResult.GetValue(select)),
            parseResult.GetValue(json),
            ReportPath(parseResult.GetValue(report)),
            cancellationToken),
        parseResult.GetValue(json),
        exceptionHandler,
        cancellationToken));
    return command;
  }

  private static Command BuildApply(
      IWdemCommandHandler handler,
      Func<Exception, bool, CancellationToken, Task<int>>? exceptionHandler)
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
    var report = ReportOption();
    var command = new Command("apply", "Apply the selected developer profile.");
    command.Options.Add(profile);
    command.Options.Add(select);
    command.Options.Add(maximumConcurrency);
    command.Options.Add(json);
    command.Options.Add(report);
    command.SetAction((parseResult, cancellationToken) => InvokeAsync(
        () => handler.ApplyAsync(
            CreateRunRequest(
                parseResult.GetValue(profile)!,
                parseResult.GetValue(select),
                parseResult.GetValue(maximumConcurrency)),
            parseResult.GetValue(json),
            ReportPath(parseResult.GetValue(report)),
            cancellationToken),
        parseResult.GetValue(json),
        exceptionHandler,
        cancellationToken));
    return command;
  }

  private static Command BuildRetry(
      IWdemCommandHandler handler,
      Func<Exception, bool, CancellationToken, Task<int>>? exceptionHandler)
  {
    var run = RequiredOption<Guid>("--run", "Run id to retry.");
    var resource = MultipleOption("--resource", "Failed or blocked resource id to retry.");
    resource.Required = true;
    resource.Arity = ArgumentArity.OneOrMore;
    var json = JsonOption();
    var report = ReportOption();
    var command = new Command("retry", "Retry failed or blocked resources from a run.");
    command.Options.Add(run);
    command.Options.Add(resource);
    command.Options.Add(json);
    command.Options.Add(report);
    command.SetAction((parseResult, cancellationToken) => InvokeAsync(
        () => handler.RetryAsync(
            parseResult.GetValue(run),
            ResourceIds(parseResult.GetValue(resource)),
            parseResult.GetValue(json),
            ReportPath(parseResult.GetValue(report)),
            cancellationToken),
        parseResult.GetValue(json),
        exceptionHandler,
        cancellationToken));
    return command;
  }

  private static Command BuildResume(
      IWdemCommandHandler handler,
      Func<Exception, bool, CancellationToken, Task<int>>? exceptionHandler)
  {
    var run = RequiredOption<Guid>("--run", "Run id to resume.");
    var json = JsonOption();
    var report = ReportOption();
    var command = new Command("resume", "Resume an interrupted run.");
    command.Options.Add(run);
    command.Options.Add(json);
    command.Options.Add(report);
    command.SetAction((parseResult, cancellationToken) => InvokeAsync(
        () => handler.ResumeAsync(
            parseResult.GetValue(run),
            parseResult.GetValue(json),
            ReportPath(parseResult.GetValue(report)),
            cancellationToken),
        parseResult.GetValue(json),
        exceptionHandler,
        cancellationToken));
    return command;
  }

  private static Command BuildRuns(
      IWdemCommandHandler handler,
      Func<Exception, bool, CancellationToken, Task<int>>? exceptionHandler)
  {
    var json = JsonOption();
    var list = new Command("list", "List persisted environment runs.");
    list.Options.Add(json);
    list.SetAction((parseResult, cancellationToken) => InvokeAsync(
        () => handler.ListRunsAsync(
            parseResult.GetValue(json),
            cancellationToken),
        parseResult.GetValue(json),
        exceptionHandler,
        cancellationToken));

    var runs = new Command("runs", "Inspect persisted environment runs.");
    runs.Subcommands.Add(list);
    return runs;
  }

  private static RunRequest CreateRunRequest(
      string profilePath,
      IEnumerable<string>? selectedResourceIds,
      int maximumConcurrency = 4)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
    return new RunRequest(
        Path.GetFullPath(profilePath),
        ResourceIds(selectedResourceIds),
        maximumConcurrency);
  }

  private static async Task<int> InvokeAsync(
      Func<Task<int>> action,
      bool json,
      Func<Exception, bool, CancellationToken, Task<int>>? exceptionHandler,
      CancellationToken cancellationToken)
  {
    try
    {
      return await action().ConfigureAwait(false);
    }
    catch (Exception exception) when (exceptionHandler is not null)
    {
      return await exceptionHandler(exception, json, cancellationToken).ConfigureAwait(false);
    }
  }

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
    Arity = ArgumentArity.OneOrMore,
    AllowMultipleArgumentsPerToken = true
  };

  private static Option<bool> JsonOption() => new("--json")
  {
    Description = "Write newline-delimited JSON output."
  };

  private static Option<string?> ReportOption() => new("--report")
  {
    Description = "Write the completed run report to a .json or .md file."
  };

  private static string? ReportPath(string? value) => string.IsNullOrWhiteSpace(value)
      ? null
      : Path.GetFullPath(value);
}
