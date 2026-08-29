using System.Diagnostics;

namespace Wdem.Tests;

internal sealed record PublishedElevatedHostResult(
    int PublishExitCode,
    string PublishOutput,
    IReadOnlyList<string> HostFiles,
    int? HostExitCode,
    string HostStandardOutput,
    string HostStandardError);

internal static class PublishedElevatedHostSmoke
{
  public const string UsageError =
      "The elevated host accepts only the required bootstrap arguments.";

  public static async Task<PublishedElevatedHostResult> PublishAndRunAsync(
      bool useBundledCliPublishOptions,
      params string[] projectSegments)
  {
    string repositoryRoot = FindRepositoryRoot();
    string projectPath = Path.Combine([repositoryRoot, .. projectSegments]);
    string publishDirectory = Path.Combine(
        Path.GetTempPath(),
        "wdem-published-host-smoke",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(publishDirectory);

    try
    {
      List<string> publishArguments =
      [
        "publish",
        projectPath,
        "-c", "Release",
        "-o", publishDirectory,
        "--nologo",
        "--verbosity", "minimal"
      ];
      if (useBundledCliPublishOptions)
      {
        publishArguments.AddRange(
        [
          "-r", "win-x64",
          "--self-contained", "true",
          "-p:PublishSingleFile=true"
        ]);
      }

      var publish = await RunAsync(
          Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
          repositoryRoot,
          publishArguments).ConfigureAwait(false);
      string publishOutput = publish.StandardOutput + publish.StandardError;
      string[] hostFiles = Directory
          .EnumerateFiles(publishDirectory, "Wdem.ElevatedHost*")
          .Select(Path.GetFileName)
          .Where(static name => name is not null)
          .Cast<string>()
          .Order(StringComparer.Ordinal)
          .ToArray();
      string hostPath = Path.Combine(publishDirectory, "Wdem.ElevatedHost.exe");
      if (publish.ExitCode != 0 || !File.Exists(hostPath))
      {
        return new PublishedElevatedHostResult(
            publish.ExitCode,
            publishOutput,
            hostFiles,
            null,
            string.Empty,
            string.Empty);
      }

      var host = await RunAsync(
          hostPath,
          publishDirectory,
          ["--invalid"]).ConfigureAwait(false);
      return new PublishedElevatedHostResult(
          publish.ExitCode,
          publishOutput,
          hostFiles,
          host.ExitCode,
          host.StandardOutput,
          host.StandardError);
    }
    finally
    {
      try
      {
        Directory.Delete(publishDirectory, recursive: true);
      }
      catch (IOException)
      {
      }
      catch (UnauthorizedAccessException)
      {
      }
    }
  }

  private static async Task<ProcessResult> RunAsync(
      string fileName,
      string workingDirectory,
      IReadOnlyList<string> arguments)
  {
    var startInfo = new ProcessStartInfo(fileName)
    {
      WorkingDirectory = workingDirectory,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true
    };
    foreach (string argument in arguments)
    {
      startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
    Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
    Task<string> standardError = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
    return new ProcessResult(
        process.ExitCode,
        await standardOutput.ConfigureAwait(false),
        await standardError.ConfigureAwait(false));
  }

  private static string FindRepositoryRoot()
  {
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
      if (File.Exists(Path.Combine(directory.FullName, "Wdem.sln")))
      {
        return directory.FullName;
      }

      directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the WDEM repository root.");
  }

  private sealed record ProcessResult(
      int ExitCode,
      string StandardOutput,
      string StandardError);
}
