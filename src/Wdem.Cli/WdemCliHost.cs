using System.CommandLine;
using System.CommandLine.Parsing;
using Wdem.Core.Execution;
using Wdem.Core.Runs;

namespace Wdem.Cli;

public static class WdemCliHost
{
  public static async Task<int> RunAsync(
      string[] args,
      Func<CancellationToken, Task<IWdemCommandHandler>> handlerFactory,
      TextWriter output,
      TextWriter error,
      CancellationToken cancellationToken = default,
      LogRedactor? redactor = null)
  {
    ArgumentNullException.ThrowIfNull(args);
    ArgumentNullException.ThrowIfNull(handlerFactory);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(error);
    redactor ??= new LogRedactor();
    var jsonRequested = RequestsJson(args);

    var lazyHandler = new LazyCommandHandler(handlerFactory);
    async Task<int> HandleExceptionAsync(
        Exception exception,
        bool json,
        CancellationToken actionToken)
    {
      var cancelled = exception is OperationCanceledException &&
          actionToken.IsCancellationRequested;
      var validationFailure = exception is ArgumentException;
      await WdemCommandHandler.WriteExceptionEventAsync(
          exception,
          json,
          cancelled,
          error,
          redactor,
          validationFailure ? WdemErrorCode.ProfileError : null).ConfigureAwait(false);
      return cancelled ? 130 : validationFailure ? 2 : 1;
    }

    ParseResult parseResult;
    try
    {
      var root = WdemCliBuilder.Build(lazyHandler, HandleExceptionAsync);
      parseResult = root.Parse(args);
    }
    catch (Exception exception)
    {
      await WdemCommandHandler.WriteExceptionEventAsync(
          exception,
          jsonRequested,
          cancelled: false,
          error,
          redactor,
          WdemErrorCode.ProfileError).ConfigureAwait(false);
      return 2;
    }

    if (parseResult.Errors.Count > 0)
    {
      var parseException = new ArgumentException(string.Join(
          Environment.NewLine,
          parseResult.Errors.Select(parseError => parseError.Message)));
      await WdemCommandHandler.WriteExceptionEventAsync(
          parseException,
          jsonRequested,
          cancelled: false,
          error,
          redactor,
          WdemErrorCode.ProfileError).ConfigureAwait(false);
      return 2;
    }

    var configuration = new InvocationConfiguration
    {
      EnableDefaultExceptionHandler = false,
      Output = output,
      Error = error
    };
    try
    {
      return await parseResult.InvokeAsync(configuration, cancellationToken)
          .ConfigureAwait(false);
    }
    catch (Exception exception)
    {
      return await HandleExceptionAsync(
          exception,
          jsonRequested,
          cancellationToken).ConfigureAwait(false);
    }
  }

  private static bool RequestsJson(IReadOnlyList<string> args)
  {
    for (var index = 0; index < args.Count; index++)
    {
      var argument = args[index];
      if (argument.Equals("--json", StringComparison.Ordinal))
      {
        return index + 1 >= args.Count || !bool.TryParse(args[index + 1], out var value)
            ? true
            : value;
      }

      foreach (var separator in new[] { "--json=", "--json:" })
      {
        if (argument.StartsWith(separator, StringComparison.Ordinal))
        {
          return !bool.TryParse(argument[separator.Length..], out var value) || value;
        }
      }
    }

    return false;
  }

  private sealed class LazyCommandHandler(
      Func<CancellationToken, Task<IWdemCommandHandler>> factory) : IWdemCommandHandler
  {
    private readonly object _gate = new();
    private Task<IWdemCommandHandler>? _handler;

    public async Task<int> InspectAsync(
        RunRequest request,
        bool json,
        CancellationToken cancellationToken) =>
        await (await GetAsync(cancellationToken).ConfigureAwait(false))
            .InspectAsync(request, json, cancellationToken).ConfigureAwait(false);

    public async Task<int> InspectAsync(
        RunRequest request,
        bool json,
        string? reportFile,
        CancellationToken cancellationToken) =>
        await (await GetAsync(cancellationToken).ConfigureAwait(false))
            .InspectAsync(request, json, reportFile, cancellationToken).ConfigureAwait(false);

    public async Task<int> ApplyAsync(
        RunRequest request,
        bool json,
        CancellationToken cancellationToken) =>
        await (await GetAsync(cancellationToken).ConfigureAwait(false))
            .ApplyAsync(request, json, cancellationToken).ConfigureAwait(false);

    public async Task<int> ApplyAsync(
        RunRequest request,
        bool json,
        string? reportFile,
        CancellationToken cancellationToken) =>
        await (await GetAsync(cancellationToken).ConfigureAwait(false))
            .ApplyAsync(request, json, reportFile, cancellationToken).ConfigureAwait(false);

    public async Task<int> RetryAsync(
        Guid runId,
        IReadOnlySet<string> resourceIds,
        bool json,
        CancellationToken cancellationToken) =>
        await (await GetAsync(cancellationToken).ConfigureAwait(false))
            .RetryAsync(runId, resourceIds, json, cancellationToken).ConfigureAwait(false);

    public async Task<int> RetryAsync(
        Guid runId,
        IReadOnlySet<string> resourceIds,
        bool json,
        string? reportFile,
        CancellationToken cancellationToken) =>
        await (await GetAsync(cancellationToken).ConfigureAwait(false))
            .RetryAsync(runId, resourceIds, json, reportFile, cancellationToken)
            .ConfigureAwait(false);

    public async Task<int> ResumeAsync(
        Guid runId,
        bool json,
        CancellationToken cancellationToken) =>
        await (await GetAsync(cancellationToken).ConfigureAwait(false))
            .ResumeAsync(runId, json, cancellationToken).ConfigureAwait(false);

    public async Task<int> ResumeAsync(
        Guid runId,
        bool json,
        string? reportFile,
        CancellationToken cancellationToken) =>
        await (await GetAsync(cancellationToken).ConfigureAwait(false))
            .ResumeAsync(runId, json, reportFile, cancellationToken).ConfigureAwait(false);

    public async Task<int> ListRunsAsync(
        bool json,
        CancellationToken cancellationToken) =>
        await (await GetAsync(cancellationToken).ConfigureAwait(false))
            .ListRunsAsync(json, cancellationToken).ConfigureAwait(false);

    private Task<IWdemCommandHandler> GetAsync(CancellationToken cancellationToken)
    {
      lock (_gate)
      {
        return _handler ??= factory(cancellationToken);
      }
    }
  }
}
