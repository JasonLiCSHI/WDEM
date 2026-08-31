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
  private static readonly TimeSpan PublishLeaseTimeout = TimeSpan.FromMinutes(5);
  private static readonly TimeSpan PublishLeaseRetryDelay = TimeSpan.FromMilliseconds(100);

  public static async Task<FileStream> AcquirePublishLeaseAsync(
      string repositoryRoot,
      CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
    string normalizedRoot = Path.GetFullPath(repositoryRoot)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .ToUpperInvariant();
    byte[] rootHash = System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(normalizedRoot));
    string lockDirectory = Path.Combine(Path.GetTempPath(), "wdem-test-publish-locks");
    string lockPath = Path.Combine(
        lockDirectory,
        $"{Convert.ToHexString(rootHash)[..24]}.lock");
    Directory.CreateDirectory(lockDirectory);

    var wait = System.Diagnostics.Stopwatch.StartNew();
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        return new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.Asynchronous);
      }
      catch (IOException exception) when (wait.Elapsed >= PublishLeaseTimeout)
      {
        throw new TimeoutException(
            $"Timed out waiting for the repository publish-test lease at '{lockPath}'.",
            exception);
      }
      catch (IOException)
      {
        await Task.Delay(PublishLeaseRetryDelay, cancellationToken).ConfigureAwait(false);
      }
    }
  }

  public static async Task<PublishedElevatedHostResult> PublishAndRunAsync(
      bool useBundledCliPublishOptions,
      params string[] projectSegments)
  {
    string repositoryRoot = FindRepositoryRoot();
    await using FileStream publishLease =
        await AcquirePublishLeaseAsync(repositoryRoot).ConfigureAwait(false);
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
      else
      {
        publishArguments.AddRange(["-r", "win-x64"]);
      }

      TestProcessResult publish = await TestProcessRunner.RunAsync(
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

      TestProcessResult host = await TestProcessRunner.RunAsync(
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
}
