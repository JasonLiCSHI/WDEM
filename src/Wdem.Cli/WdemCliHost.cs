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

    var lazyHandler = new LazyCommandHandler(handlerFactory);
    async Task<int> HandleExceptionAsync(
        Exception exception,
        bool json,
        CancellationToken actionToken)
    {
      var cancelled = exception is OperationCanceledException &&
          actionToken.IsCancellationRequested;
      await WdemCommandHandler.WriteExceptionEventAsync(
          exception,
          json,
          cancelled,
          error,
          redactor).ConfigureAwait(false);
      return cancelled ? 130 : 1;
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
          args.Contains("--json", StringComparer.Ordinal),
          cancelled: false,
          error,
          redactor,
          WdemErrorCode.ProfileError).ConfigureAwait(false);
      return 1;
    }

    if (parseResult.Errors.Count > 0)
    {
      var parseException = new ArgumentException(string.Join(
          Environment.NewLine,
          parseResult.Errors.Select(parseError => parseError.Message)));
      await WdemCommandHandler.WriteExceptionEventAsync(
          parseException,
          args.Contains("--json", StringComparer.Ordinal),
          cancelled: false,
          error,
          redactor,
          WdemErrorCode.ProfileError).ConfigureAwait(false);
      return 1;
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
          args.Contains("--json", StringComparer.Ordinal),
          cancellationToken).ConfigureAwait(false);
    }
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

    public async Task<int> ApplyAsync(
        RunRequest request,
        bool json,
        CancellationToken cancellationToken) =>
        await (await GetAsync(cancellationToken).ConfigureAwait(false))
            .ApplyAsync(request, json, cancellationToken).ConfigureAwait(false);

    public async Task<int> RetryAsync(
        Guid runId,
        IReadOnlySet<string> resourceIds,
        bool json,
        CancellationToken cancellationToken) =>
        await (await GetAsync(cancellationToken).ConfigureAwait(false))
            .RetryAsync(runId, resourceIds, json, cancellationToken).ConfigureAwait(false);

    public async Task<int> ResumeAsync(
        Guid runId,
        bool json,
        CancellationToken cancellationToken) =>
        await (await GetAsync(cancellationToken).ConfigureAwait(false))
            .ResumeAsync(runId, json, cancellationToken).ConfigureAwait(false);

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
