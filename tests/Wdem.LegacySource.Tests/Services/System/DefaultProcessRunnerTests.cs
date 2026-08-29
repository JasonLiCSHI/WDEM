using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Wdem.LegacySource.Models;
using Wdem.LegacySource.Services.System;
using Xunit;

namespace Wdem.LegacySource.Tests.Services.System
{
  public class DefaultProcessRunnerTests
  {
    [Fact]
    public async Task RunCommandDetailedAsync_RetainsExitCodeAndSeparatedOutputAfterDrain()
    {
      var runner = new DefaultProcessRunner();
      var output = new List<ProcessOutputLine>();
      var (executable, arguments) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
          ? ("cmd", new[] { "/d", "/c", "(echo stdout-line) & (echo stderr-line 1>&2) & exit /b 37" })
          : ("sh", new[] { "-c", "echo stdout-line; echo stderr-line >&2; exit 37" });

      var result = await runner.RunCommandDetailedAsync(
          executable,
          arguments,
          output.Add,
          CancellationToken.None);

      Assert.True(result.Started);
      Assert.Equal(37, result.ExitCode);
      Assert.Contains("stdout-line", result.StandardOutput);
      Assert.Contains(result.StandardError, line => line.Trim() == "stderr-line");
      Assert.Contains(output, line => !line.IsStandardError && line.Text == "stdout-line");
      Assert.Contains(output, line => line.IsStandardError && line.Text.Trim() == "stderr-line");
    }

    [Fact]
    public async Task RunCommandDetailedAsync_PreservesArgumentTokensAndWorkingDirectory()
    {
      var runner = new DefaultProcessRunner();
      var directory = Path.Combine(Path.GetTempPath(), $"wdem-process-{Guid.NewGuid():N}");
      Directory.CreateDirectory(directory);
      try
      {
        var specialArgument = "two words-quote\"-trailing\\";
        var scriptPath = Path.Combine(directory, "echo-args.ps1");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
          File.WriteAllText(
              scriptPath,
              "[Console]::Out.WriteLine((Get-Location).Path)\n[Console]::Out.WriteLine($args[0])\n");
        }

        var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (FileName: "powershell.exe", Arguments: new[]
            {
              "-NoLogo", "-NoProfile", "-NonInteractive", "-File", scriptPath,
              specialArgument
            })
            : (FileName: "sh", Arguments: new[] { "-c", "pwd; printf '%s\\n' \"$1\"", "sh", specialArgument });

        var result = await runner.RunCommandDetailedAsync(
            command.FileName,
            command.Arguments,
            directory,
            null,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(result.StandardOutput[0]).TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        Assert.Equal(specialArgument, result.StandardOutput[1]);
      }
      finally
      {
        Directory.Delete(directory, recursive: true);
      }
    }

    [Fact]
    public async Task RunCommandDetailedAsync_DrainsAllOutputAfterProcessExit()
    {
      var runner = new DefaultProcessRunner();
      const int lineCount = 400;
      var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
          ? (FileName: "powershell.exe", Arguments: new[]
          {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            $"1..{lineCount} | ForEach-Object {{ [Console]::Out.WriteLine(\"out-$_\"); [Console]::Error.WriteLine(\"err-$_\") }}"
          })
          : (FileName: "sh", Arguments: new[] { "-c", $"i=1; while [ $i -le {lineCount} ]; do echo out-$i; echo err-$i >&2; i=$((i+1)); done" });

      var result = await runner.RunCommandDetailedAsync(
          command.FileName,
          command.Arguments,
          null,
          CancellationToken.None);

      Assert.Equal(0, result.ExitCode);
      Assert.Equal(lineCount, result.StandardOutput.Count);
      Assert.Equal(lineCount, result.StandardError.Count);
      Assert.Equal($"out-{lineCount}", result.StandardOutput[^1]);
      Assert.Equal($"err-{lineCount}", result.StandardError[^1]);
    }

    [Fact]
    public async Task RunCommandDetailedAsync_MissingExecutableReturnsNotStarted()
    {
      var runner = new DefaultProcessRunner();

      var result = await runner.RunCommandDetailedAsync(
          $"missing-{Guid.NewGuid():N}",
          [],
          null,
          CancellationToken.None);

      Assert.False(result.Started);
      Assert.Null(result.ExitCode);
      Assert.Empty(result.StandardOutput);
      Assert.Empty(result.StandardError);
      Assert.Equal("ExecutableNotFound", result.FailureKind.ToString());
    }

    [Fact]
    public async Task RunCommandDetailedAsync_TimeoutRetainsStartedStateAndCollectedEvidence()
    {
      var runner = new DefaultProcessRunner(new ProcessRunnerTestHooks
      {
        ProcessTimeout = TimeSpan.FromMilliseconds(100),
        WaitForExitAsync = (_, cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
      });
      var (executable, arguments) = Echo("before-timeout");

      var result = await runner.RunCommandDetailedAsync(
          executable,
          arguments,
          null,
          CancellationToken.None);

      Assert.True(result.Started);
      Assert.Null(result.ExitCode);
      Assert.Contains("before-timeout", result.StandardOutput);
      Assert.Equal(ProcessFailureKind.TimedOut, result.FailureKind);
      Assert.Equal("Process execution timed out.", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RunCommandDetailedAsync_InvalidTimeoutIsRejectedBeforeExecutableLaunch(
        int timeoutMilliseconds)
    {
      var runner = new DefaultProcessRunner();
      var missingExecutable = $"missing-{Guid.NewGuid():N}";

      await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
          runner.RunCommandDetailedAsync(
              missingExecutable,
              [],
              null,
              TimeSpan.FromMilliseconds(timeoutMilliseconds),
              null,
              CancellationToken.None));
    }

    [Fact]
    public async Task RunCommandDetailedAsync_DrainFailureRetainsExitCodeAndCollectedEvidence()
    {
      var runner = new DefaultProcessRunner(new ProcessRunnerTestHooks
      {
        DrainOutputAsync = async (tasks, cancellationToken) =>
        {
          await Task.WhenAll(tasks).WaitAsync(cancellationToken);
          throw new IOException("secret drain implementation detail");
        }
      });
      var (executable, arguments) = Echo("before-drain-failure");

      var result = await runner.RunCommandDetailedAsync(
          executable,
          arguments,
          null,
          CancellationToken.None);

      Assert.True(result.Started);
      Assert.Equal(0, result.ExitCode);
      Assert.Contains("before-drain-failure", result.StandardOutput);
      Assert.Equal(ProcessFailureKind.OutputDrainFailed, result.FailureKind);
      Assert.Equal("Process output could not be completely collected.", result.FailureMessage);
      Assert.DoesNotContain("secret", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunCommandAsync_DrainFailureDoesNotReportSuccess()
    {
      var runner = new DefaultProcessRunner(new ProcessRunnerTestHooks
      {
        DrainOutputAsync = async (tasks, cancellationToken) =>
        {
          await Task.WhenAll(tasks).WaitAsync(cancellationToken);
          throw new IOException("injected");
        }
      });
      var (executable, arguments) = Echo("output");

      var succeeded = await runner.RunCommandAsync(
          executable,
          arguments,
          false,
          null,
          CancellationToken.None);

      Assert.False(succeeded);
    }

    [Fact]
    public async Task RunCommandDetailedAsync_PostStartFailureRetainsKnownExitCode()
    {
      var runner = new DefaultProcessRunner(new ProcessRunnerTestHooks
      {
        AfterExitAsync = (_, _) => throw new InvalidOperationException("secret job detail")
      });
      var (executable, arguments) = Echo("before-post-start-failure");

      var result = await runner.RunCommandDetailedAsync(
          executable,
          arguments,
          null,
          CancellationToken.None);

      Assert.True(result.Started);
      Assert.Equal(0, result.ExitCode);
      Assert.Contains("before-post-start-failure", result.StandardOutput);
      Assert.Equal(ProcessFailureKind.PostStartFailed, result.FailureKind);
      Assert.Equal("Process completion could not be verified.", result.FailureMessage);
      Assert.DoesNotContain("secret", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunCommandDetailedAsync_CancellationTerminatesProcess()
    {
      var runner = new DefaultProcessRunner();
      var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : "sh";
      var arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
          ? new[] { "/c", "ping 127.0.0.1 -n 30 > nul" }
          : new[] { "-c", "sleep 30" };
      using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

      await Assert.ThrowsAsync<OperationCanceledException>(() =>
          runner.RunCommandDetailedAsync(
              executable,
              arguments,
              null,
              cancellation.Token));
    }

    [Fact]
    public async Task RunCommandAsync_MissingExecutableRetainsFailureCallbackBehavior()
    {
      var runner = new DefaultProcessRunner();
      var output = new List<string>();
      var executable = $"missing-{Guid.NewGuid():N}";

      var result = await runner.RunCommandAsync(
          executable,
          [],
          false,
          output.Add,
          CancellationToken.None);

      Assert.False(result);
      Assert.Contains(output, line => line.Contains("Error starting", StringComparison.Ordinal));
    }

    /// <summary>RunCommand returns true without executing when dryRun is enabled.</summary>
    [Fact]
    public void RunCommand_DryRunTrue_ReturnsTrueWithoutExecuting()
    {
      // Arrange
      var runner = new DefaultProcessRunner();
      string exe = "zzz-no-such-exe-" + Guid.NewGuid().ToString("N");

      // Act
      bool result = runner.RunCommand(exe, new[] { "--version" }, true);

      // Assert
      Assert.True(result);
    }

    /// <summary>RunCommand returns true for a successful process.</summary>
    [Fact]
    public void RunCommand_SuccessfulProcess_ReturnsTrue()
    {
      // Arrange
      var runner = new DefaultProcessRunner();

      // Act
      bool result = runner.RunCommand("dotnet", new[] { "--version" }, false);

      // Assert
      Assert.True(result);
    }

    /// <summary>RunCommand returns false when the process exits with a non-zero code.</summary>
    [Fact]
    public void RunCommand_ExitNonZero_ReturnsFalse()
    {
      // Arrange
      var runner = new DefaultProcessRunner();
      var (exe, args) = ExitNonZero();

      // Act
      bool result = runner.RunCommand(exe, args, false);

      // Assert
      Assert.False(result);
    }

    /// <summary>RunCommand returns false when the executable does not exist.</summary>
    [Fact]
    public void RunCommand_NonExistentExecutable_ReturnsFalse()
    {
      // Arrange
      var runner = new DefaultProcessRunner();
      string exe = "zzz-no-such-exe-" + Guid.NewGuid().ToString("N");

      // Act
      bool result = runner.RunCommand(exe, new[] { "--version" }, false);

      // Assert
      Assert.False(result);
    }

    /// <summary>RunCommand forwards stdout lines to the onOutput callback.</summary>
    [Fact]
    public void RunCommand_OnOutputReceivesStdoutLines()
    {
      // Arrange
      var runner = new DefaultProcessRunner();
      string marker = "hello-stdout";
      var (exe, args) = Echo(marker);
      var outputs = new ConcurrentBag<string>();
      using var outputReceived = new ManualResetEventSlim(false);

      // Act
      bool result = runner.RunCommand(exe, args, false, line =>
      {
        outputs.Add(line);
        if (line.Contains(marker, StringComparison.Ordinal))
        {
          outputReceived.Set();
        }
      });

      // Assert
      Assert.True(result);
      Assert.True(outputReceived.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for stdout output.");
      Assert.Contains(outputs, s => s.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>RunCommand forwards stderr lines to the onOutput callback.</summary>
    [Fact]
    public void RunCommand_OnOutputReceivesStderrLines()
    {
      // Arrange
      var runner = new DefaultProcessRunner();
      string marker = "hello-stderr";
      var (exe, args) = WriteToStderr(marker);
      var outputs = new ConcurrentBag<string>();
      using var outputReceived = new ManualResetEventSlim(false);

      // Act
      bool result = runner.RunCommand(exe, args, false, line =>
      {
        outputs.Add(line);
        if (line.Contains(marker, StringComparison.Ordinal))
        {
          outputReceived.Set();
        }
      });

      // Assert
      Assert.True(result);
      Assert.True(outputReceived.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for stderr output.");
      Assert.Contains(outputs, s => s.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>RunCommand with null onOutput completes without throwing and returns success.</summary>
    [Fact]
    public void RunCommand_OnOutputNull_DoesNotThrow()
    {
      // Arrange
      var runner = new DefaultProcessRunner();

      // Act
      bool result = runner.RunCommand("dotnet", new[] { "--version" }, false, null);

      // Assert
      Assert.True(result);
    }

    [Fact]
    public async Task RunCommandAsync_CancellationTerminatesProcess()
    {
      var runner = new DefaultProcessRunner();
      using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
      var stopwatch = global::System.Diagnostics.Stopwatch.StartNew();
      var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : "sh";
      var arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
          ? new[] { "/c", "ping 127.0.0.1 -n 30 > nul" }
          : new[] { "-c", "sleep 30" };

      await Assert.ThrowsAsync<OperationCanceledException>(() =>
          runner.RunCommandAsync(executable, arguments, false, null, cancellation.Token));

      Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunCommandAsync_CancellationTerminatesChildAfterParentExits()
    {
      if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

      var runner = new DefaultProcessRunner();
      var testDirectory = Path.Combine(
          Path.GetTempPath(),
          $"wdem_job_test_{Guid.NewGuid():N}");
      Directory.CreateDirectory(testDirectory);
      var markerPath = Path.Combine(testDirectory, "child-survived.txt");
      var childScript = Path.Combine(testDirectory, "child.cmd");
      var parentScript = Path.Combine(testDirectory, "parent.cmd");

      try
      {
        File.WriteAllText(
            childScript,
            $"@ping 127.0.0.1 -n 4 >nul{Environment.NewLine}@echo survived>\"{markerPath}\"");
        File.WriteAllText(
            parentScript,
            $"@start \"\" /b cmd /c \"\"{childScript}\"\"");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunCommandAsync(
                "cmd",
                new[] { "/c", parentScript },
                false,
                null,
                cancellation.Token));

        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.False(
            File.Exists(markerPath),
            "A child process survived after the cancelled Job Object was closed.");
      }
      finally
      {
        if (Directory.Exists(testDirectory))
        {
          Directory.Delete(testDirectory, recursive: true);
        }
      }
    }

    /// <summary>RunCommandWithOutput returns non-empty stdout for dotnet --version.</summary>
    [Fact]
    public void RunCommandWithOutput_ReturnsStdoutFromDotnetVersion()
    {
      // Arrange
      var runner = new DefaultProcessRunner();

      // Act
      string output = runner.RunCommandWithOutput("dotnet", new[] { "--version" }).Trim();

      // Assert
      Assert.False(string.IsNullOrWhiteSpace(output));
      Assert.True(char.IsDigit(output[0]));
    }

    /// <summary>RunCommandWithOutput returns empty string for a non-existent executable.</summary>
    [Fact]
    public void RunCommandWithOutput_NonExistentExecutable_ReturnsEmpty()
    {
      // Arrange
      var runner = new DefaultProcessRunner();
      string exe = "zzz-no-such-exe-" + Guid.NewGuid().ToString("N");

      // Act
      string output = runner.RunCommandWithOutput(exe, new[] { "--version" });

      // Assert
      Assert.Equal(string.Empty, output);
    }

    /// <summary>RunCommandWithOutput returns stdout even when the process exits non-zero.</summary>
    [Fact]
    public void RunCommandWithOutput_ExitNonZero_StillReturnsStdout()
    {
      // Arrange
      var runner = new DefaultProcessRunner();
      string marker = "hello-output";
      string exe;
      string[] args;

      if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      {
        exe = "cmd";
        args = new[] { "/c", $"echo {marker} & exit 1" };
      }
      else
      {
        exe = "sh";
        args = new[] { "-c", $"echo {marker}; exit 1" };
      }

      // Act
      string output = runner.RunCommandWithOutput(exe, args).Trim();

      // Assert
      Assert.Contains(marker, output);
    }

    /// <summary>RunCommandWithOutput writes standard input and returns echoed output.</summary>
    [Fact]
    public void RunCommandWithOutput_WithStandardInput_EchoesInput()
    {
      // Arrange
      var runner = new DefaultProcessRunner();
      // findstr requires at least one non-empty line to avoid hanging.
      string input = "stdin-echo";
      string exe;
      string[] args;

      if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      {
        exe = "findstr";
        args = new[] { "." };
      }
      else
      {
        exe = "cat";
        args = Array.Empty<string>();
      }

      // Act
      string output = runner.RunCommandWithOutput(exe, args, input);

      // Assert
      Assert.Contains(input, output);
    }

    /// <summary>RunAndCapture returns trimmed stdout for dotnet --version.</summary>
    [Fact]
    public void RunAndCapture_ReturnsTrimmedStdout()
    {
      // Arrange
      var runner = new DefaultProcessRunner();

      // Act
      string output = runner.RunAndCapture("dotnet", new[] { "--version" });

      // Assert
      Assert.False(string.IsNullOrWhiteSpace(output));
      Assert.True(char.IsDigit(output[0]));
      Assert.Equal(output.Trim(), output);
    }

    /// <summary>RunAndCapture returns empty string for a non-existent executable.</summary>
    [Fact]
    public void RunAndCapture_NonExistentExecutable_ReturnsEmpty()
    {
      // Arrange
      var runner = new DefaultProcessRunner();
      string exe = "zzz-no-such-exe-" + Guid.NewGuid().ToString("N");

      // Act
      string output = runner.RunAndCapture(exe, new[] { "--version" });

      // Assert
      Assert.Equal(string.Empty, output);
    }

    /// <summary>RunAndCapture does not capture stderr output.</summary>
    [Fact]
    public void RunAndCapture_StderrOnly_ReturnsEmptyOrWhitespace()
    {
      // Arrange
      var runner = new DefaultProcessRunner();
      var (exe, args) = WriteToStderr("stderr-only");

      // Act
      string output = runner.RunAndCapture(exe, args);

      // Assert
      Assert.True(string.IsNullOrWhiteSpace(output));
    }

    /// <summary>RunAndCapture with IEnumerable arguments returns trimmed stdout for dotnet --version.</summary>
    [Fact]
    public void RunAndCapture_EnumerableArgs_ReturnsTrimmedStdout()
    {
      // Arrange
      var runner = new DefaultProcessRunner();

      // Act
      string output = runner.RunAndCapture("dotnet", new[] { "--version" });

      // Assert
      Assert.False(string.IsNullOrWhiteSpace(output));
      Assert.True(char.IsDigit(output[0]));
      Assert.Equal(output.Trim(), output);
    }

    /// <summary>RunAndCapture with IEnumerable arguments returns empty string for a non-existent executable.</summary>
    [Fact]
    public void RunAndCapture_EnumerableArgs_NonExistentExecutable_ReturnsEmpty()
    {
      // Arrange
      var runner = new DefaultProcessRunner();
      string exe = "zzz-no-such-exe-" + Guid.NewGuid().ToString("N");

      // Act
      string output = runner.RunAndCapture(exe, new[] { "--version" });

      // Assert
      Assert.Equal(string.Empty, output);
    }

    /// <summary>RunAndCapture with IEnumerable arguments does not capture stderr output.</summary>
    [Fact]
    public void RunAndCapture_EnumerableArgs_StderrOnly_ReturnsEmptyOrWhitespace()
    {
      // Arrange
      var runner = new DefaultProcessRunner();
      string exe;
      string[] args;
      if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      {
        exe = "cmd";
        args = new[] { "/c", "echo stderr-only 1>&2" };
      }
      else
      {
        exe = "sh";
        args = new[] { "-c", "echo stderr-only >&2" };
      }

      // Act
      string output = runner.RunAndCapture(exe, args);

      // Assert
      Assert.True(string.IsNullOrWhiteSpace(output));
    }


    private static (string exe, string[] args) Echo(string text)
    {
      if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      {
        return ("cmd", new[] { "/c", "echo", text });
      }

      return ("sh", new[] { "-c", $"echo {text}" });
    }

    private static (string exe, string[] args) WriteToStderr(string text)
    {
      if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      {
        // Use cmd to reliably and quickly write to stderr across environments
        return ("cmd", new[] { "/c", "echo", text, "1>&2" });
      }

      return ("sh", new[] { "-c", $"echo {text} >&2" });
    }

    private static (string exe, string[] args) ExitNonZero()
    {
      if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      {
        return ("cmd", new[] { "/c", "exit 1" });
      }

      return ("sh", new[] { "-c", "exit 1" });
    }
  }
}
