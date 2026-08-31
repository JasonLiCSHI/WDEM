using System.Reflection;
using System.Text;
using System.Text.Json;
using Wdem.Core.Runs;

namespace Wdem.Windows.Logging;

/// <summary>
/// Writes one durable JSON object per line for a single WDEM process session.
/// Logging is best-effort: an unavailable log directory never prevents WDEM from starting.
/// </summary>
public sealed class JsonLineSessionLog : IProgress<WorkflowProgress>, IDisposable
{
  private readonly object _sync = new();
  private StreamWriter? _writer;
  private long _sequence;
  private bool _disposed;

  private JsonLineSessionLog(string component, string? path, StreamWriter? writer, string? error)
  {
    Component = component;
    Path = path;
    _writer = writer;
    LastError = error;
    SessionId = Guid.NewGuid().ToString("N");

    Write("session_start", "WDEM session started.", new
    {
      version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
      operatingSystem = Environment.OSVersion.VersionString,
      processId = Environment.ProcessId
    });
  }

  public string Component { get; }

  public string SessionId { get; }

  public string? Path { get; }

  public string DisplayPath => Path ?? "日志文件不可用";

  public bool IsEnabled => _writer is not null;

  public string? LastError { get; private set; }

  public static JsonLineSessionLog Create(string component)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(component);

    var primaryDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wdem",
        "logs");
    var primary = TryOpen(component, primaryDirectory);
    if (primary.Writer is not null)
    {
      return new JsonLineSessionLog(component, primary.Path, primary.Writer, null);
    }

    var fallbackDirectory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "Wdem",
        "logs");
    var fallback = TryOpen(component, fallbackDirectory);
    if (fallback.Writer is not null)
    {
      return new JsonLineSessionLog(
          component,
          fallback.Path,
          fallback.Writer,
          $"Primary log directory was unavailable: {primary.Error}");
    }

    return new JsonLineSessionLog(
        component,
        path: null,
        writer: null,
        $"Logging is unavailable. Primary: {primary.Error}; fallback: {fallback.Error}");
  }

  public static JsonLineSessionLog CreateInDirectory(string component, string directory)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(component);
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);

    var result = TryOpen(component, directory);
    return new JsonLineSessionLog(component, result.Path, result.Writer, result.Error);
  }

  public void Report(WorkflowProgress value) =>
      Write(
          "progress",
          value.Message ?? $"{value.TaskId} {value.State} {value.Stage} {value.Percent}%",
          value);

  public void Write(string category, string message, object? data = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(category);
    ArgumentNullException.ThrowIfNull(message);

    lock (_sync)
    {
      if (_writer is null || _disposed)
      {
        return;
      }

      try
      {
        var entry = new
        {
          timestamp = DateTimeOffset.UtcNow,
          sequence = ++_sequence,
          sessionId = SessionId,
          component = Component,
          category,
          message,
          data
        };
        _writer.WriteLine(JsonSerializer.Serialize(entry));
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
      {
        LastError = exception.Message;
        DisableWriter();
      }
    }
  }

  public void Dispose()
  {
    lock (_sync)
    {
      if (_disposed)
      {
        return;
      }

      Write("session_end", "WDEM session ended.");
      _disposed = true;
      DisableWriter();
    }
  }

  private static (string? Path, StreamWriter? Writer, string? Error) TryOpen(
      string component,
      string directory)
  {
    try
    {
      Directory.CreateDirectory(directory);
      var path = System.IO.Path.Combine(
          directory,
          $"wdem-{component}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.jsonl");
      var stream = new FileStream(
          path,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.Read,
          bufferSize: 4096,
          FileOptions.WriteThrough);
      var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
      {
        AutoFlush = true
      };
      return (path, writer, null);
    }
    catch (Exception exception) when (
        exception is IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        ArgumentException or
        System.Security.SecurityException)
    {
      return (null, null, exception.Message);
    }
  }

  private void DisableWriter()
  {
    try
    {
      _writer?.Dispose();
    }
    catch
    {
      // Logging must never make the application fail.
    }
    finally
    {
      _writer = null;
    }
  }
}
