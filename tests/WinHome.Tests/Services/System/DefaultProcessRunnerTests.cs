using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using WinHome.Services.System;
using Xunit;

namespace WinHome.Tests.Services.System
{
  public class DefaultProcessRunnerTests
  {
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
          $"winhome_job_test_{Guid.NewGuid():N}");
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
