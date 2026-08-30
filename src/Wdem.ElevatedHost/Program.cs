using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Windows.Composition;
using Wdem.Windows.Persistence;
using Wdem.Windows.Security;

namespace Wdem.ElevatedHost;

internal static class Program
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
  };

  public static async Task<int> Main(string[] args)
  {
    ElevatedHostBootstrapOptions options;
    try
    {
      options = ElevatedHostBootstrapOptions.Parse(args);
    }
    catch (ArgumentException exception)
    {
      await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
      return 2;
    }

    try
    {
      await RunAsync(options).ConfigureAwait(false);
      return 0;
    }
    catch (Exception exception) when (
        exception is IOException or JsonException or InvalidOperationException)
    {
      return 1;
    }
  }

  private static async Task RunAsync(ElevatedHostBootstrapOptions options)
  {
    ElevatedHostProcessJob.JoinCurrentProcess(options.JobName);
    using var pipe = new NamedPipeClientStream(
        ".",
        options.PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous);
    await pipe.ConnectAsync().ConfigureAwait(false);

    var composition = WdemElevatedHostFactory.Create(
        new WdemDataPaths(options.LocalApplicationData),
        options.ApplicationRoot);
    var worker = new ElevatedResourceWorker(
        composition.RunStore,
        composition.Providers,
        composition.Redactor);

    using var reader = new StreamReader(
        pipe,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        detectEncodingFromByteOrderMarks: false,
        leaveOpen: true);
    await using var writer = new StreamWriter(
        pipe,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        leaveOpen: true)
    {
      AutoFlush = true
    };

    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
    {
      ElevatedResourceRequest? request;
      try
      {
        request = JsonSerializer.Deserialize<ElevatedResourceRequest>(line, JsonOptions);
      }
      catch (JsonException)
      {
        await WriteResultAsync(writer, Refused()).ConfigureAwait(false);
        continue;
      }

      if (request is null || request.RunId != options.RunId)
      {
        await WriteResultAsync(writer, Refused()).ConfigureAwait(false);
        continue;
      }

      var progressChannel = Channel.CreateUnbounded<ProviderProgress>(
          new UnboundedChannelOptions
          {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
          });
      var progressPump = PumpProgressAsync(progressChannel.Reader, writer);
      ResourceApplyResult result;
      try
      {
        result = await worker.ApplyAsync(
            request,
            new ChannelProgress(progressChannel.Writer),
            CancellationToken.None).ConfigureAwait(false);
      }
      finally
      {
        progressChannel.Writer.TryComplete();
      }

      await progressPump.ConfigureAwait(false);
      await WriteResultAsync(writer, result).ConfigureAwait(false);
    }
  }

  private static async Task PumpProgressAsync(
      ChannelReader<ProviderProgress> progress,
      StreamWriter writer)
  {
    await foreach (var update in progress.ReadAllAsync().ConfigureAwait(false))
    {
      await WriteAsync(
          writer,
          new ElevatedHostResponse("progress", Progress: update)).ConfigureAwait(false);
    }
  }

  private static Task WriteResultAsync(
      StreamWriter writer,
      ResourceApplyResult result) => WriteAsync(
          writer,
          new ElevatedHostResponse("result", Result: result));

  private static Task WriteAsync(StreamWriter writer, ElevatedHostResponse response) =>
      writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));

  private static ResourceApplyResult Refused() => new()
  {
    ResourceId = "unknown",
    Outcome = ApplyOutcome.Failed,
    Error = new StructuredError(
        WdemErrorCode.PermissionError,
        "Elevated resource request was refused.",
        "The request did not match the approved elevated host session.")
    {
      ResourceId = "unknown",
      IsRetryable = false
    }
  };

  private sealed class ChannelProgress(
      ChannelWriter<ProviderProgress> writer) : IProgress<ProviderProgress>
  {
    public void Report(ProviderProgress value) => writer.TryWrite(value);
  }
}
