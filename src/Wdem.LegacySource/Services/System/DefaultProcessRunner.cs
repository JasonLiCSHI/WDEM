using System.Diagnostics;
using Wdem.LegacySource.Interfaces;
using Wdem.LegacySource.Models;

namespace Wdem.LegacySource.Services.System
{
  /// <summary>Default implementation of <see cref="IProcessRunner"/> that spawns real OS processes.</summary>
  public class DefaultProcessRunner : IProcessRunner
  {
    [Obsolete("Use the IEnumerable<string> overload instead to prevent command injection.")]
    public bool RunCommand(string fileName, string args, bool dryRun, Action<string>? onOutput = null)
    {
      if (dryRun) return true;

      var startInfo = new ProcessStartInfo
      {
        FileName = fileName,
        Arguments = args,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      };
      return RunProcessInternal(startInfo, fileName, onOutput);
    }

    public bool RunCommand(string fileName, IEnumerable<string> args, bool dryRun, Action<string>? onOutput = null)
    {
      if (dryRun) return true;

      var startInfo = new ProcessStartInfo
      {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      };
      foreach (var arg in args)
      {
        startInfo.ArgumentList.Add(arg);
      }
      return RunProcessInternal(startInfo, fileName, onOutput);
    }

    public async Task<bool> RunCommandAsync(
        string fileName,
        IEnumerable<string> args,
        bool dryRun,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (dryRun) return true;
      var result = await RunCommandDetailedAsync(
          fileName,
          args,
          line => onOutput?.Invoke(line.Text),
          cancellationToken).ConfigureAwait(false);
      if (!result.Started)
      {
        onOutput?.Invoke($"[ProcessRunner] Error starting {fileName}.");
      }

      return result.Started && result.ExitCode == 0;
    }

    public Task<ProcessRunResult> RunCommandDetailedAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<ProcessOutputLine>? onOutput,
        CancellationToken cancellationToken) => RunCommandDetailedAsync(
            fileName,
            arguments,
            null,
            onOutput,
            cancellationToken);

    public async Task<ProcessRunResult> RunCommandDetailedAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<ProcessOutputLine>? onOutput,
        CancellationToken cancellationToken)
    {
      ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
      ArgumentNullException.ThrowIfNull(arguments);
      cancellationToken.ThrowIfCancellationRequested();
      var argumentSnapshot = arguments.ToArray();

      try
      {
        return OperatingSystem.IsWindows()
            ? await RunWindowsJobCommandDetailedAsync(
                fileName,
                argumentSnapshot,
                workingDirectory,
                onOutput,
                cancellationToken).ConfigureAwait(false)
            : await RunPortableCommandDetailedAsync(
                fileName,
                argumentSnapshot,
                workingDirectory,
                onOutput,
                cancellationToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception exception)
      {
        global::System.Diagnostics.Trace.WriteLine(
            $"[ProcessRunner] Could not start process: {exception.GetType().Name}");
        return new ProcessRunResult(false, null, [], []);
      }
    }

    private static async Task<ProcessRunResult> RunWindowsJobCommandDetailedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        Action<ProcessOutputLine>? onOutput,
        CancellationToken cancellationToken)
    {
      using var processJob = WindowsProcessJob.Start(fileName, arguments, workingDirectory);
      var standardOutput = new List<string>();
      var standardError = new List<string>();
      var outputGate = new object();
      var outputTask = CaptureOutputAsync(
          processJob.StandardOutput,
          false,
          standardOutput,
          outputGate,
          onOutput);
      var errorTask = CaptureOutputAsync(
          processJob.StandardError,
          true,
          standardError,
          outputGate,
          onOutput);
      using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
      using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
          cancellationToken,
          timeout.Token);

      try
      {
        await processJob.Process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
        var exitCode = processJob.Process.ExitCode;
        await processJob.WaitForEmptyAsync(linkedCancellation.Token).ConfigureAwait(false);
        await Task.WhenAll(outputTask, errorTask)
            .WaitAsync(TimeSpan.FromSeconds(5), linkedCancellation.Token)
            .ConfigureAwait(false);
        return SnapshotResult(true, exitCode, standardOutput, standardError);
      }
      catch (OperationCanceledException)
      {
        processJob.Terminate();
        await DrainAfterTerminationAsync(outputTask, errorTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return SnapshotResult(true, null, standardOutput, standardError);
      }
    }

    private static async Task<ProcessRunResult> RunPortableCommandDetailedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        Action<ProcessOutputLine>? onOutput,
        CancellationToken cancellationToken)
    {
      var startInfo = new ProcessStartInfo
      {
        FileName = fileName,
        WorkingDirectory = workingDirectory ?? string.Empty,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      };
      foreach (var argument in arguments)
      {
        startInfo.ArgumentList.Add(argument);
      }

      using var process = new Process { StartInfo = startInfo };
      if (!process.Start())
      {
        return new ProcessRunResult(false, null, [], []);
      }

      var standardOutput = new List<string>();
      var standardError = new List<string>();
      var outputGate = new object();
      var outputTask = CaptureOutputAsync(
          process.StandardOutput,
          false,
          standardOutput,
          outputGate,
          onOutput);
      var errorTask = CaptureOutputAsync(
          process.StandardError,
          true,
          standardError,
          outputGate,
          onOutput);
      using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
      using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
          cancellationToken,
          timeout.Token);

      try
      {
        await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
        var exitCode = process.ExitCode;
        await Task.WhenAll(outputTask, errorTask)
            .WaitAsync(TimeSpan.FromSeconds(5), linkedCancellation.Token)
            .ConfigureAwait(false);
        return SnapshotResult(true, exitCode, standardOutput, standardError);
      }
      catch (OperationCanceledException)
      {
        try
        {
          if (!process.HasExited)
          {
            process.Kill(entireProcessTree: true);
          }
        }
        catch (InvalidOperationException)
        {
          // The process exited between HasExited and Kill.
        }

        await DrainAfterTerminationAsync(outputTask, errorTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return SnapshotResult(true, null, standardOutput, standardError);
      }
    }

    private static async Task CaptureOutputAsync(
        StreamReader reader,
        bool isStandardError,
        List<string> destination,
        object outputGate,
        Action<ProcessOutputLine>? onOutput)
    {
      while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
      {
        lock (outputGate)
        {
          destination.Add(line);
          try
          {
            onOutput?.Invoke(new ProcessOutputLine(isStandardError, line));
          }
          catch (Exception exception)
          {
            global::System.Diagnostics.Trace.WriteLine(
                $"[ProcessRunner] Output observer failed: {exception.GetType().Name}");
          }
        }
      }
    }

    private static async Task DrainAfterTerminationAsync(params Task[] outputTasks)
    {
      try
      {
        await Task.WhenAll(outputTasks)
            .WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
      }
      catch (Exception exception)
      {
        global::System.Diagnostics.Trace.WriteLine(
            $"[ProcessRunner] Failed while draining terminated process output: {exception.GetType().Name}");
      }
    }

    private static ProcessRunResult SnapshotResult(
        bool started,
        int? exitCode,
        List<string> standardOutput,
        List<string> standardError) => new(
            started,
            exitCode,
            Array.AsReadOnly(standardOutput.ToArray()),
            Array.AsReadOnly(standardError.ToArray()));

    private bool RunProcessInternal(ProcessStartInfo startInfo, string fileName, Action<string>? onOutput)
    {


      try
      {
        using var process = new Process { StartInfo = startInfo };
        if (onOutput != null)
        {
          process.OutputDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data); };
          process.ErrorDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data); };
        }

        if (!process.Start())
        {
          return false;
        }

        if (onOutput != null)
        {
          process.BeginOutputReadLine();
          process.BeginErrorReadLine();
        }
        else
        {
          // Still read to avoid hanging
          Task.Run(() => process.StandardOutput.ReadToEnd());
          Task.Run(() => process.StandardError.ReadToEnd());
        }

        if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
          process.Kill(true);
          return false;
        }

        if (onOutput != null)
        {
          // Ensure async event handlers (BeginOutputReadLine/BeginErrorReadLine)
          // have finished processing streams before we dispose the process.
          // completed in pr.no 134
          process.WaitForExit();
        }

        return process.ExitCode == 0;
      }
      catch (Exception ex)
      {
        if (onOutput != null) onOutput($"[ProcessRunner] Error starting {fileName}: {ex.Message}");
        global::System.Diagnostics.Trace.WriteLine($"[ProcessRunner] Error starting {fileName}: {ex.Message}");
        return false;
      }
    }

    [Obsolete("Use the IEnumerable<string> overload instead to prevent command injection.")]
    public string RunCommandWithOutput(string fileName, string args)
    {
      return RunCommandWithOutput(fileName, args, null);
    }

    public string RunCommandWithOutput(string fileName, IEnumerable<string> args)
    {
      var startInfo = new ProcessStartInfo
      {
        FileName = fileName,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = false,
        UseShellExecute = false,
        CreateNoWindow = true
      };
      foreach (var arg in args)
      {
        startInfo.ArgumentList.Add(arg);
      }
      return RunProcessWithOutputInternal(startInfo, null);
    }

    [Obsolete("Use the IEnumerable<string> overload instead to prevent command injection.")]
    public string RunCommandWithOutput(string fileName, string args, string? standardInput)
    {
      var startInfo = new ProcessStartInfo
      {
        FileName = fileName,
        Arguments = args,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = standardInput != null,
        UseShellExecute = false,
        CreateNoWindow = true
      };
      return RunProcessWithOutputInternal(startInfo, standardInput);
    }

    public string RunCommandWithOutput(string fileName, IEnumerable<string> args, string? standardInput)
    {
      var startInfo = new ProcessStartInfo
      {
        FileName = fileName,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = standardInput != null,
        UseShellExecute = false,
        CreateNoWindow = true
      };
      foreach (var arg in args)
      {
        startInfo.ArgumentList.Add(arg);
      }
      return RunProcessWithOutputInternal(startInfo, standardInput);
    }

    private string RunProcessWithOutputInternal(ProcessStartInfo startInfo, string? standardInput)
    {


      try
      {
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        if (standardInput != null)
        {
          using var writer = process.StandardInput;
          writer.Write(standardInput);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        if (process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
          // Process exited, wait for streams to finish with a short timeout
          Task.WaitAll(new Task[] { outputTask, errorTask }, TimeSpan.FromSeconds(5));
          return outputTask.Result;
        }
        else
        {
          process.Kill(true);
          return string.Empty;
        }
      }
      catch (Exception ex)
      {
        global::System.Diagnostics.Trace.WriteLine($"[ProcessRunner] Error running process {startInfo.FileName}: {ex.Message}");
        return string.Empty;
      }
    }
    [Obsolete("Use the IEnumerable<string> overload instead to prevent command injection.")]
    public string RunAndCapture(string fileName, string arguments)
    {
      try
      {
        var psi = new global::System.Diagnostics.ProcessStartInfo
        {
          FileName = fileName,
          Arguments = arguments,
          RedirectStandardOutput = true,
          UseShellExecute = false,
          CreateNoWindow = true
        };
        using var process = global::System.Diagnostics.Process.Start(psi);
        if (process == null) return string.Empty;

        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
          process.Kill(true);
          return string.Empty;
        }
        string output = process.StandardOutput.ReadToEnd().Trim();
        return output;
      }
      catch (Exception ex)
      {
        global::System.Diagnostics.Trace.WriteLine($"[ProcessRunner] RunAndCapture failed for {fileName}: {ex.Message}");
        return string.Empty;
      }
    }

    public string RunAndCapture(string fileName, IEnumerable<string> arguments)
    {
      try
      {
        var psi = new global::System.Diagnostics.ProcessStartInfo
        {
          FileName = fileName,
          RedirectStandardOutput = true,
          UseShellExecute = false,
          CreateNoWindow = true
        };
        foreach (var arg in arguments)
        {
          psi.ArgumentList.Add(arg);
        }
        using var process = global::System.Diagnostics.Process.Start(psi);
        if (process == null) return string.Empty;

        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
          process.Kill(true);
          return string.Empty;
        }
        string output = process.StandardOutput.ReadToEnd().Trim();
        return output;
      }
      catch (Exception ex)
      {
        global::System.Diagnostics.Trace.WriteLine($"[ProcessRunner] RunAndCapture failed for {fileName}: {ex.Message}");
        return string.Empty;
      }
    }

    public bool RunProcessWithStartInfo(ProcessStartInfo startInfo)
    {
      using var process = Process.Start(startInfo);

      if (process == null)
        throw new Exception("Failed to start process");

      Task<string>? outputTask = null;
      Task<string>? errorTask = null;

      if (startInfo.RedirectStandardOutput)
        outputTask = process.StandardOutput.ReadToEndAsync();

      if (startInfo.RedirectStandardError)
        errorTask = process.StandardError.ReadToEndAsync();

      if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
      {
        process.Kill(true);
        throw new TimeoutException("Process execution timed out.");
      }

      if (outputTask != null)
        outputTask.GetAwaiter().GetResult();

      if (errorTask != null)
        errorTask.GetAwaiter().GetResult();

      var error = errorTask != null ? errorTask.Result : string.Empty;

      if (process.ExitCode != 0)
      {
        throw new Exception(
            $"Process failed with exit code {process.ExitCode}: {error}");
      }

      return true;
    }
  }
}
