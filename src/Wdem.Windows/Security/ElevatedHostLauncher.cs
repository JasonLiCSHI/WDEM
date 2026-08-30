using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Wdem.Core.Execution;
using Wdem.Core.Providers;

namespace Wdem.Windows.Security;

public sealed class ElevatedHostLauncher : IElevatedHostLauncher
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
  private static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromMinutes(2);
  private readonly string _hostPath;
  private readonly string _localApplicationData;
  private readonly string _applicationRoot;
  private readonly TimeSpan _connectionTimeout;

  public ElevatedHostLauncher(
      string hostPath,
      string localApplicationData,
      string applicationRoot,
      TimeSpan? connectionTimeout = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
    ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
    ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
    _hostPath = Path.GetFullPath(hostPath);
    _localApplicationData = Path.GetFullPath(localApplicationData);
    _applicationRoot = Path.GetFullPath(applicationRoot);
    _connectionTimeout = connectionTimeout ?? DefaultConnectionTimeout;
    if (_connectionTimeout <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(connectionTimeout));
    }
  }

  public async Task<IElevatedHostSession> StartAsync(
      Guid runId,
      string pipeName,
      CancellationToken cancellationToken)
  {
    if (runId == Guid.Empty)
    {
      throw new ArgumentException("An execution run identifier is required.", nameof(runId));
    }

    ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
    var pipe = new NamedPipeServerStream(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    Process? process = null;
    ElevatedHostProcessJob? job = null;
    try
    {
      job = ElevatedHostProcessJob.Create(ElevatedHostProcessJob.NameForPipe(pipeName));
      process = Process.Start(CreateStartInfo(
          _hostPath,
          pipeName,
          runId,
          _localApplicationData,
          _applicationRoot)) ?? throw new InvalidOperationException(
          "The elevated host process could not be started.");
      job.Track(process);
      using var timeout = new CancellationTokenSource(_connectionTimeout);
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(
          cancellationToken,
          timeout.Token);
      await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
      return new NamedPipeElevatedHostSession(pipe, job);
    }
    catch
    {
      pipe.Dispose();
      job?.Dispose();
      if (job is null)
      {
        process?.Dispose();
      }

      throw;
    }
  }

  internal static ProcessStartInfo CreateStartInfo(
      string hostPath,
      string pipeName,
      Guid runId,
      string localApplicationData,
      string applicationRoot)
  {
    var startInfo = new ProcessStartInfo
    {
      FileName = hostPath,
      UseShellExecute = true,
      Verb = "runas",
      WorkingDirectory = Path.GetDirectoryName(hostPath) ?? AppContext.BaseDirectory,
      WindowStyle = ProcessWindowStyle.Hidden
    };
    startInfo.ArgumentList.Add("--pipe");
    startInfo.ArgumentList.Add(pipeName);
    startInfo.ArgumentList.Add("--job");
    startInfo.ArgumentList.Add(ElevatedHostProcessJob.NameForPipe(pipeName));
    startInfo.ArgumentList.Add("--run-id");
    startInfo.ArgumentList.Add(runId.ToString("D"));
    startInfo.ArgumentList.Add("--local-app-data");
    startInfo.ArgumentList.Add(localApplicationData);
    startInfo.ArgumentList.Add("--application-root");
    startInfo.ArgumentList.Add(applicationRoot);
    return startInfo;
  }

  private sealed class NamedPipeElevatedHostSession : IElevatedHostSession
  {
    private readonly NamedPipeServerStream _pipe;
    private readonly ElevatedHostProcessJob _job;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private int _terminated;
    private int _disposed;

    public NamedPipeElevatedHostSession(
        NamedPipeServerStream pipe,
        ElevatedHostProcessJob job)
    {
      _pipe = pipe;
      _job = job;
      _reader = new StreamReader(
          pipe,
          new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
          detectEncodingFromByteOrderMarks: false,
          leaveOpen: true);
      _writer = new StreamWriter(
          pipe,
          new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
          leaveOpen: true)
      {
        AutoFlush = true
      };
    }

    public async Task<ResourceApplyResult> ApplyAsync(
        ElevatedResourceRequest request,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      ObjectDisposedException.ThrowIf(Volatile.Read(ref _terminated) != 0, this);
      var json = JsonSerializer.Serialize(request, JsonOptions);
      await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
      while (true)
      {
        var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
          throw new IOException("The elevated host disconnected before returning a result.");
        }

        var response = JsonSerializer.Deserialize<ElevatedHostResponse>(line, JsonOptions) ??
            throw new InvalidDataException("The elevated host returned an empty response.");
        if (string.Equals(response.Type, "progress", StringComparison.Ordinal))
        {
          if (response.Progress is null)
          {
            throw new InvalidDataException("The elevated host returned invalid progress.");
          }

          progress?.Report(response.Progress);
          continue;
        }

        if (string.Equals(response.Type, "result", StringComparison.Ordinal) &&
            response.Result is not null)
        {
          return response.Result;
        }

        throw new InvalidDataException("The elevated host returned an unknown response type.");
      }
    }

    public Task TerminateAsync(CancellationToken cancellationToken)
    {
      if (Interlocked.Exchange(ref _terminated, 1) != 0)
      {
        return Task.CompletedTask;
      }

      _job.Terminate();
      return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
      if (Interlocked.Exchange(ref _disposed, 1) != 0)
      {
        return;
      }

      await TerminateAsync(CancellationToken.None).ConfigureAwait(false);
      _reader.Dispose();
      try
      {
        await _writer.DisposeAsync().ConfigureAwait(false);
      }
      catch (Exception exception) when (exception is IOException or ObjectDisposedException)
      {
        // The terminated peer may close the pipe before the leave-open writer flushes.
      }

      _pipe.Dispose();
      _job.Dispose();
    }
  }
}
