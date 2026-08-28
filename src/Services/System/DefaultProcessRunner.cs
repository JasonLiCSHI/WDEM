using System.Diagnostics;
using WinHome.Interfaces;

namespace WinHome.Services.System
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

      try
      {
        using var process = new Process { StartInfo = startInfo };
        var outputClosed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var errorClosed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, eventArgs) =>
        {
          if (eventArgs.Data is null)
          {
            outputClosed.TrySetResult();
          }
          else
          {
            onOutput?.Invoke(eventArgs.Data);
          }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
          if (eventArgs.Data is null)
          {
            errorClosed.TrySetResult();
          }
          else
          {
            onOutput?.Invoke(eventArgs.Data);
          }
        };

        if (!process.Start())
        {
          return false;
        }
        using var processJob = WindowsProcessJob.TryCreateAndAssign(process);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
          await process.WaitForExitAsync(linkedCancellation.Token);
          if (processJob is not null)
          {
            await processJob.WaitForEmptyAsync(linkedCancellation.Token);
          }
          try
          {
            await Task.WhenAll(outputClosed.Task, errorClosed.Task)
                .WaitAsync(TimeSpan.FromSeconds(5), linkedCancellation.Token);
          }
          catch (TimeoutException)
          {
            global::System.Diagnostics.Trace.WriteLine(
                $"[ProcessRunner] Timed out draining output for {fileName}.");
            return false;
          }

          return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
          processJob?.Terminate();
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
          catch (Exception cleanupError)
          {
            global::System.Diagnostics.Trace.WriteLine(
                $"[ProcessRunner] Failed to terminate {fileName}: {cleanupError.Message}");
          }

          try
          {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(outputClosed.Task, errorClosed.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
          }
          catch (Exception cleanupError)
          {
            global::System.Diagnostics.Trace.WriteLine(
                $"[ProcessRunner] Failed while waiting for {fileName} cleanup: {cleanupError.Message}");
          }

          cancellationToken.ThrowIfCancellationRequested();
          return false;
        }
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception ex)
      {
        onOutput?.Invoke($"[ProcessRunner] Error starting {fileName}: {ex.Message}");
        global::System.Diagnostics.Trace.WriteLine(
            $"[ProcessRunner] Error starting {fileName}: {ex.Message}");
        return false;
      }
    }

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
