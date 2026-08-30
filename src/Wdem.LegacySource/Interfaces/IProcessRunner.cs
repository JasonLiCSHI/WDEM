using System;
using System.Collections.Generic;
using System.Diagnostics;
using Wdem.LegacySource.Models;

namespace Wdem.LegacySource.Interfaces
{
  /// <summary>Abstraction for running external processes with security-conscious argument passing.</summary>
  public interface IProcessRunner
  {
    /// <summary>Runs a command with a raw argument string (deprecated).</summary>
    [Obsolete("Use the IEnumerable<string> overload instead to prevent command injection.")]
    bool RunCommand(string fileName, string arguments, bool dryRun, Action<string>? onOutput = null);

    /// <summary>Runs a command with individual argument tokens to prevent injection.</summary>
    bool RunCommand(string fileName, IEnumerable<string> arguments, bool dryRun, Action<string>? onOutput = null);

    /// <summary>Runs a command asynchronously and terminates its process tree when cancelled.</summary>
    Task<bool> RunCommandAsync(
        string fileName,
        IEnumerable<string> arguments,
        bool dryRun,
        Action<string>? onOutput,
        CancellationToken cancellationToken);

    /// <summary>Runs a command and retains its exit code and separated output evidence.</summary>
    Task<ProcessRunResult> RunCommandDetailedAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<ProcessOutputLine>? onOutput,
        CancellationToken cancellationToken);

    /// <summary>Runs a command in a working directory and retains detailed evidence.</summary>
    Task<ProcessRunResult> RunCommandDetailedAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<ProcessOutputLine>? onOutput,
        CancellationToken cancellationToken) => RunCommandDetailedAsync(
            fileName,
            arguments,
            onOutput,
            cancellationToken);

    /// <summary>Runs a command with an optional per-request execution timeout.</summary>
    Task<ProcessRunResult> RunCommandDetailedAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        TimeSpan? timeout,
        Action<ProcessOutputLine>? onOutput,
        CancellationToken cancellationToken) => RunCommandDetailedAsync(
            fileName,
            arguments,
            workingDirectory,
            onOutput,
            cancellationToken);

    /// <summary>
    /// Runs a command while optionally treating cancellation as a launch gate only.
    /// Once a launch-only command starts, completion and output evidence are retained.
    /// </summary>
    Task<ProcessRunResult> RunCommandDetailedAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        TimeSpan? timeout,
        Action<ProcessOutputLine>? onOutput,
        CancellationToken cancellationToken,
        bool continueAfterStart) => continueAfterStart
            ? Task.FromException<ProcessRunResult>(new NotSupportedException(
                "This process runner does not support launch-only cancellation."))
            : RunCommandDetailedAsync(
                fileName,
                arguments,
                workingDirectory,
                timeout,
                onOutput,
                cancellationToken);

    /// <summary>Runs a command and captures output using a raw argument string (deprecated).</summary>
    [Obsolete("Use the IEnumerable<string> overload instead to prevent command injection.")]
    string RunCommandWithOutput(string fileName, string args);

    /// <summary>Runs a command and captures output using individual argument tokens.</summary>
    string RunCommandWithOutput(string fileName, IEnumerable<string> args);

    /// <summary>Runs a command with stdin input using a raw argument string (deprecated).</summary>
    [Obsolete("Use the IEnumerable<string> overload instead to prevent command injection.")]
    string RunCommandWithOutput(string fileName, string args, string? standardInput);

    /// <summary>Runs a command with stdin input using individual argument tokens.</summary>
    string RunCommandWithOutput(string fileName, IEnumerable<string> args, string? standardInput);

    /// <summary>Runs and captures process output using a raw argument string (deprecated).</summary>
    [Obsolete("Use the IEnumerable<string> overload instead to prevent command injection.")]
    string RunAndCapture(string fileName, string arguments);

    /// <summary>Runs and captures process output using individual argument tokens.</summary>
    string RunAndCapture(string fileName, IEnumerable<string> arguments);

    /// <summary>Runs a process with a fully specified <see cref="ProcessStartInfo"/>.</summary>
    bool RunProcessWithStartInfo(ProcessStartInfo startInfo);
  }
}
