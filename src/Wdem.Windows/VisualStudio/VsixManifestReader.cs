using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Wdem.Core.Execution;

namespace Wdem.Windows.VisualStudio;

public sealed record VsixManifest(
    string Id,
    string Version,
    string ManifestPath,
    string VisualStudioInstanceId,
    IReadOnlyList<VsixInstallationTarget>? InstallationTargets = null)
{
  public IReadOnlyList<VsixInstallationTarget> Targets { get; } =
      InstallationTargets?.ToArray() ?? [];
}

public sealed record VsixInstallationTarget(string Id, string? VersionRange);

public sealed record VsixManifestReadResult(
    VsixManifest? Manifest,
    StructuredError? Error);

public interface IVsixManifestReader
{
  Task<IReadOnlyList<VsixManifest>> ReadInstalledAsync(
      VisualStudioInstance instance,
      CancellationToken cancellationToken);

  Task<VsixManifestReadResult> ReadSourceAsync(
      string path,
      string visualStudioInstanceId,
      CancellationToken cancellationToken);
}

public sealed class VsixManifestReader : IVsixManifestReader
{
  private const long MaxManifestBytes = 1024 * 1024;

  public async Task<IReadOnlyList<VsixManifest>> ReadInstalledAsync(
      VisualStudioInstance instance,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(instance);
    cancellationToken.ThrowIfCancellationRequested();
    var roots = GetInstalledExtensionRoots(instance);
    var manifests = new List<VsixManifest>();
    foreach (var root in roots.Where(Directory.Exists))
    {
      IEnumerable<string> paths;
      try
      {
        paths = Directory.EnumerateFiles(root, "*.vsixmanifest", SearchOption.AllDirectories);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
        throw new IOException($"Could not enumerate Visual Studio extensions in '{root}'.", exception);
      }

      foreach (var path in paths)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await ReadManifestFileAsync(
            path,
            instance.InstanceId,
            cancellationToken).ConfigureAwait(false);
        if (result.Manifest is null)
        {
          throw new InvalidDataException(result.Error?.Detail ?? "The VSIX manifest is invalid.");
        }

        manifests.Add(result.Manifest);
      }
    }

    return manifests;
  }

  public async Task<VsixManifestReadResult> ReadSourceAsync(
      string path,
      string visualStudioInstanceId,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (string.IsNullOrWhiteSpace(path) ||
        !Path.IsPathFullyQualified(path) ||
        string.IsNullOrWhiteSpace(visualStudioInstanceId))
    {
      return Failure("The VSIX source path and Visual Studio instance ID are required.");
    }

    try
    {
      var fullPath = Path.GetFullPath(path);
      if (string.Equals(
              Path.GetExtension(fullPath),
              ".vsixmanifest",
              StringComparison.OrdinalIgnoreCase))
      {
        return await ReadManifestFileAsync(
            fullPath,
            visualStudioInstanceId,
            cancellationToken).ConfigureAwait(false);
      }

      await using var stream = new FileStream(
          fullPath,
          FileMode.Open,
          FileAccess.Read,
          FileShare.Read,
          bufferSize: 81920,
          FileOptions.Asynchronous | FileOptions.SequentialScan);
      using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
      var entries = archive.Entries.Where(entry => entry.FullName.EndsWith(
          ".vsixmanifest",
          StringComparison.OrdinalIgnoreCase)).ToArray();
      if (entries.Length != 1 || entries[0].Length > MaxManifestBytes)
      {
        return Failure("The VSIX must contain exactly one bounded .vsixmanifest file.");
      }

      await using var manifestStream = entries[0].Open();
      return await ParseAsync(
          manifestStream,
          $"{fullPath}!/{entries[0].FullName}",
          visualStudioInstanceId,
          cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
        InvalidDataException or XmlException)
    {
      return Failure("The VSIX source could not be safely read as a valid manifest.", exception);
    }
  }

  private static async Task<VsixManifestReadResult> ReadManifestFileAsync(
      string path,
      string instanceId,
      CancellationToken cancellationToken)
  {
    try
    {
      var fullPath = Path.GetFullPath(path);
      var info = new FileInfo(fullPath);
      if (!info.Exists || info.Length > MaxManifestBytes)
      {
        return Failure("The VSIX manifest is missing or exceeds the configured size limit.");
      }

      await using var stream = new FileStream(
          fullPath,
          FileMode.Open,
          FileAccess.Read,
          FileShare.Read,
          bufferSize: 81920,
          FileOptions.Asynchronous | FileOptions.SequentialScan);
      return await ParseAsync(stream, fullPath, instanceId, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
        XmlException)
    {
      return Failure("The VSIX manifest could not be safely read.", exception);
    }
  }

  private static async Task<VsixManifestReadResult> ParseAsync(
      Stream stream,
      string manifestPath,
      string instanceId,
      CancellationToken cancellationToken)
  {
    var settings = new XmlReaderSettings
    {
      Async = true,
      DtdProcessing = DtdProcessing.Prohibit,
      XmlResolver = null,
      MaxCharactersInDocument = MaxManifestBytes
    };
    using var reader = XmlReader.Create(stream, settings);
    var document = await XDocument.LoadAsync(
        reader,
        LoadOptions.None,
        cancellationToken).ConfigureAwait(false);
    var identities = document.Descendants().Where(element => string.Equals(
        element.Name.LocalName,
        "Identity",
        StringComparison.Ordinal)).ToArray();
    if (identities.Length != 1)
    {
      return Failure("The VSIX manifest must contain exactly one Identity element.");
    }

    var id = identities[0].Attributes().FirstOrDefault(attribute => string.Equals(
        attribute.Name.LocalName,
        "Id",
        StringComparison.Ordinal))?.Value;
    var version = identities[0].Attributes().FirstOrDefault(attribute => string.Equals(
        attribute.Name.LocalName,
        "Version",
        StringComparison.Ordinal))?.Value;
    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
    {
      return Failure("The VSIX Identity must contain non-empty Id and Version attributes.");
    }

    var targets = document.Descendants()
        .Where(element => string.Equals(
            element.Name.LocalName,
            "InstallationTarget",
            StringComparison.Ordinal))
        .Select(element => new VsixInstallationTarget(
            element.Attributes().FirstOrDefault(attribute => string.Equals(
                attribute.Name.LocalName,
                "Id",
                StringComparison.Ordinal))?.Value ?? string.Empty,
            element.Attributes().FirstOrDefault(attribute => string.Equals(
                attribute.Name.LocalName,
                "Version",
                StringComparison.Ordinal))?.Value))
        .ToArray();
    if (targets.Any(target => string.IsNullOrWhiteSpace(target.Id)))
    {
      return Failure("Every VSIX InstallationTarget must contain a non-empty Id attribute.");
    }

    return new VsixManifestReadResult(
        new VsixManifest(id, version, manifestPath, instanceId, targets),
        null);
  }

  private static IReadOnlyList<string> GetInstalledExtensionRoots(VisualStudioInstance instance)
  {
    var roots = new List<string>
    {
      Path.Combine(instance.InstallationPath, "Common7", "IDE", "Extensions")
    };
    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (!string.IsNullOrWhiteSpace(local))
    {
      roots.Add(Path.Combine(local, "Microsoft", "VisualStudio", instance.InstanceId, "Extensions"));
    }

    return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  }

  private static VsixManifestReadResult Failure(string detail, Exception? exception = null) => new(
      null,
      new StructuredError(
          WdemErrorCode.ConfigurationError,
          "VSIX manifest is invalid.",
          detail)
      {
        UnderlyingException = exception
      });
}
