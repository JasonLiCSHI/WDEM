using Wdem.Core.Execution;
using Wdem.Core.Providers;

namespace Wdem.Core.Profiles;

public sealed class DirectoryProfileCatalog : IProfileCatalog
{
  private readonly string _directory;
  private readonly ProfileValidator _validator;

  public DirectoryProfileCatalog(
      string directory,
      IResourceProviderRegistry providerRegistry)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    ArgumentNullException.ThrowIfNull(providerRegistry);

    _directory = Path.GetFullPath(directory);
    _validator = new ProfileValidator(providerRegistry);
  }

  public async Task<ProfileLoadResult> LoadAsync(
      string id,
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!IsSafeProfileId(id))
    {
      return Failure(
          _directory,
          "The profile id is not safe.",
          $"Profile id '{id}' must match [A-Za-z0-9][A-Za-z0-9._-]*.",
          "/profile/id");
    }

    foreach (var extension in new[] { ".yaml", ".json" })
    {
      cancellationToken.ThrowIfCancellationRequested();
      var path = Path.Combine(_directory, $"{id}{extension}");
      if (!File.Exists(path))
      {
        continue;
      }

      var boundaryError = ValidateDiscoveredPathBoundary(path);
      cancellationToken.ThrowIfCancellationRequested();
      if (boundaryError is not null)
      {
        return boundaryError;
      }

      return await LoadFileCoreAsync(path, cancellationToken, _directory).ConfigureAwait(false);
    }

    cancellationToken.ThrowIfCancellationRequested();
    return Failure(
        Path.Combine(_directory, $"{id}.yaml"),
        "The requested profile was not found.",
        $"Neither '{id}.yaml' nor '{id}.json' exists in '{_directory}'.",
        "/profile/id");
  }

  /// <remarks>
  /// This explicit-path API trusts its caller to select a file. Discovery through
  /// <see cref="LoadAsync(string, CancellationToken)"/> additionally enforces the configured root boundary.
  /// </remarks>
  public async Task<ProfileLoadResult> LoadFileAsync(
      string path,
      CancellationToken cancellationToken = default) =>
      await LoadFileCoreAsync(path, cancellationToken, requiredRoot: null).ConfigureAwait(false);

  private async Task<ProfileLoadResult> LoadFileCoreAsync(
      string path,
      CancellationToken cancellationToken,
      string? requiredRoot)
  {
    cancellationToken.ThrowIfCancellationRequested();
    string sourcePath;
    try
    {
      sourcePath = Path.GetFullPath(path);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return FailureFromException(
          path,
          "The profile path is invalid.",
          "The supplied explicit path could not be canonicalized.",
          string.Empty,
          exception);
    }

    var extension = Path.GetExtension(sourcePath);
    if (!extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) &&
        !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Failure(
          sourcePath,
          "The profile file extension is not supported.",
          $"File '{Path.GetFileName(sourcePath)}' has extension '{extension}'. Only .yaml and .json are supported.");
    }

    using var readResult = await ProfileDocumentReader.ReadAsync(
        sourcePath,
        cancellationToken,
        requiredRoot).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    if (!readResult.IsValid)
    {
      return new ProfileLoadResult
      {
        SourcePath = sourcePath,
        Errors = readResult.Errors
      };
    }

    var validation = await _validator.ValidateAsync(
        readResult.Document!.RootElement,
        sourcePath,
        cancellationToken).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return new ProfileLoadResult
    {
      Profile = validation.Document?.Profile,
      SourcePath = sourcePath,
      Errors = validation.Errors
    };
  }

  public async Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    string[] paths;
    try
    {
      paths = Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly)
          .Where(path =>
              Path.GetExtension(path).Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
              Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
          .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
          .ThenBy(Path.GetFileName, StringComparer.Ordinal)
          .ToArray();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return [FailureFromException(
          _directory,
          "The profile directory could not be enumerated.",
          "The file system rejected directory enumeration.",
          string.Empty,
          exception)];
    }

    var results = new List<ProfileLoadResult>(paths.Length);
    foreach (var path in paths)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var boundaryError = ValidateDiscoveredPathBoundary(path);
      cancellationToken.ThrowIfCancellationRequested();
      results.Add(boundaryError ??
          await LoadFileCoreAsync(path, cancellationToken, _directory).ConfigureAwait(false));
    }

    cancellationToken.ThrowIfCancellationRequested();
    AddDuplicateProfileIdErrors(results, cancellationToken);
    cancellationToken.ThrowIfCancellationRequested();
    return results;
  }

  private static void AddDuplicateProfileIdErrors(
      List<ProfileLoadResult> results,
      CancellationToken cancellationToken)
  {
    var duplicateGroups = results
        .Select((result, index) => (result, index))
        .Where(item => item.result.Profile is not null)
        .GroupBy(item => item.result.Profile!.Id, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() > 1);
    foreach (var group in duplicateGroups)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var files = string.Join(", ", group.Select(item => Path.GetFileName(item.result.SourcePath)));
      foreach (var item in group)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var error = ProfileErrorFactory.Create(
            item.result.SourcePath,
            "A duplicate profile id was found.",
            $"Duplicate profile id '{group.Key}' appears in multiple files: {files}.",
            "/profile/id");
        results[item.index] = item.result with
        {
          Errors = item.result.Errors.Concat([error]).ToArray()
        };
      }
    }
  }

  private static bool IsSafeProfileId(string? id)
  {
    if (string.IsNullOrEmpty(id) || !IsAsciiLetterOrDigit(id[0]))
    {
      return false;
    }

    return id.All(character =>
        IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
  }

  private static bool IsAsciiLetterOrDigit(char character) =>
      character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

  private ProfileLoadResult? ValidateDiscoveredPathBoundary(string path)
  {
    try
    {
      var resolvedTarget = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
      if (resolvedTarget is null)
      {
        return null;
      }

      var relativeTarget = Path.GetRelativePath(_directory, resolvedTarget.FullName);
      var escapesRoot = Path.IsPathRooted(relativeTarget) ||
          relativeTarget.Equals("..", StringComparison.Ordinal) ||
          relativeTarget.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
          relativeTarget.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
      return escapesRoot
          ? Failure(
              path,
              "The discovered profile leaves the configured profile root.",
              "LoadAsync does not follow a symbolic link or reparse point outside the configured profile root. " +
              "LoadFileAsync is the explicit trusted-path API.")
          : null;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
    {
      return FailureFromException(
          path,
          "The discovered profile path could not be resolved safely.",
          "LoadAsync could not verify the symbolic-link or reparse-point boundary.",
          string.Empty,
          exception);
    }
  }

  private static ProfileLoadResult Failure(
      string sourcePath,
      string summary,
      string detail,
      string pointer = "") => new()
      {
        SourcePath = sourcePath,
        Errors = [ProfileErrorFactory.Create(sourcePath, summary, detail, pointer)]
      };

  private static ProfileLoadResult FailureFromException(
      string sourcePath,
      string summary,
      string safeContext,
      string pointer,
      Exception exception) => new()
      {
        SourcePath = sourcePath,
        Errors = [ProfileErrorFactory.FromException(
            sourcePath,
            summary,
            safeContext,
            pointer,
            exception)]
      };
}
