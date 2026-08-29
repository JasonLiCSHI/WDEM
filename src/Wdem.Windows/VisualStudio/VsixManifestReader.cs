using System.IO.Compression;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using Wdem.Core.Execution;
using Wdem.Core.Versions;

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
    StructuredError? Error,
    string? ClaimedId = null,
    IReadOnlyList<string>? CandidateClaimedIds = null)
{
  public IReadOnlyList<string> ClaimedIds { get; } = CandidateClaimedIds is null
      ? ClaimedId is null ? [] : [ClaimedId]
      : CandidateClaimedIds
          .Where(id => !string.IsNullOrWhiteSpace(id))
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();
}

public sealed record VsixInstalledManifestError(
    string ManifestPath,
    string? ClaimedId,
    StructuredError Error,
    IReadOnlyList<string>? CandidateClaimedIds = null)
{
  public IReadOnlyList<string> ClaimedIds { get; } = CandidateClaimedIds is null
      ? ClaimedId is null ? [] : [ClaimedId]
      : CandidateClaimedIds
          .Where(id => !string.IsNullOrWhiteSpace(id))
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();
}

public sealed record VsixInstalledManifestReadResult(
    IReadOnlyList<VsixManifest> Manifests,
    IReadOnlyList<VsixInstalledManifestError> Errors);

public interface IVsixManifestReader
{
  Task<IReadOnlyList<VsixManifest>> ReadInstalledAsync(
      VisualStudioInstance instance,
      CancellationToken cancellationToken);

  async Task<VsixInstalledManifestReadResult> ReadInstalledWithDiagnosticsAsync(
      VisualStudioInstance instance,
      CancellationToken cancellationToken) => new(
          await ReadInstalledAsync(instance, cancellationToken).ConfigureAwait(false),
          []);

  Task<VsixManifestReadResult> ReadSourceAsync(
      string path,
      string visualStudioInstanceId,
      CancellationToken cancellationToken);
}

public sealed class VsixManifestReader : IVsixManifestReader
{
  private const long MaxManifestBytes = 1024 * 1024;
  private const string VsixNamespace = "http://schemas.microsoft.com/developer/vsx-schema/2011";
  private readonly string _localApplicationData;

  public VsixManifestReader(string? localApplicationData = null)
  {
    _localApplicationData = localApplicationData ??
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  }

  public async Task<IReadOnlyList<VsixManifest>> ReadInstalledAsync(
      VisualStudioInstance instance,
      CancellationToken cancellationToken) =>
      (await ReadInstalledWithDiagnosticsAsync(instance, cancellationToken).ConfigureAwait(false))
      .Manifests;

  public async Task<VsixInstalledManifestReadResult> ReadInstalledWithDiagnosticsAsync(
      VisualStudioInstance instance,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(instance);
    cancellationToken.ThrowIfCancellationRequested();
    var roots = GetInstalledExtensionRoots(instance);
    var manifests = new List<VsixManifest>();
    var errors = new List<VsixInstalledManifestError>();
    foreach (var root in roots)
    {
      IEnumerable<string> paths;
      try
      {
        if (!Directory.Exists(root))
        {
          continue;
        }

        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
          Trace.WriteLine("[VSIX] Skipped a redirected installed-extension root.");
          continue;
        }

        paths = Directory.EnumerateFiles(
            root,
            "*.vsixmanifest",
            new EnumerationOptions
            {
              RecurseSubdirectories = true,
              AttributesToSkip = FileAttributes.ReparsePoint,
              IgnoreInaccessible = false
            }).ToArray();
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
          var error = result.Error ?? Failure("The VSIX manifest is invalid.").Error!;
          errors.Add(new VsixInstalledManifestError(
              Path.GetFullPath(path),
              result.ClaimedId,
              error,
              result.ClaimedIds));
          Trace.WriteLine("[VSIX] Skipped an invalid installed extension manifest.");
          continue;
        }

        manifests.Add(result.Manifest);
      }
    }

    return new VsixInstalledManifestReadResult(manifests, errors);
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
    var claimedIds = document.Root?.DescendantsAndSelf()
        .Where(element => string.Equals(
            element.Name.LocalName,
            "Identity",
            StringComparison.Ordinal))
        .SelectMany(element => element.Attributes())
        .Where(attribute => string.Equals(
            attribute.Name.LocalName,
            "Id",
            StringComparison.Ordinal))
        .Select(attribute => attribute.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];
    XNamespace schema = VsixNamespace;
    var root = document.Root;
    if (root?.Name != schema + "PackageManifest")
    {
      return Failure(
          "The VSIX manifest root must be PackageManifest in the supported namespace.",
          claimedIds: claimedIds);
    }

    var structuralNames = new HashSet<string>(StringComparer.Ordinal)
    {
      "PackageManifest",
      "Metadata",
      "Identity",
      "Installation",
      "InstallationTarget"
    };
    if (root.DescendantsAndSelf().Any(element =>
            structuralNames.Contains(element.Name.LocalName) && element.Name.Namespace != schema))
    {
      return Failure(
          "VSIX structural elements must use the supported manifest namespace.",
          claimedIds: claimedIds);
    }

    var metadata = root.Elements(schema + "Metadata").ToArray();
    if (metadata.Length != 1 || root.Descendants(schema + "Metadata").Count() != 1)
    {
      return Failure(
          "The VSIX manifest must contain one direct Metadata element.",
          claimedIds: claimedIds);
    }

    var identities = root.Descendants(schema + "Identity").ToArray();
    if (identities.Length != 1 || identities[0].Parent != metadata[0])
    {
      return Failure(
          "VSIX Metadata must contain exactly one direct Identity element.",
          claimedIds: claimedIds);
    }

    if (!TryGetRequiredUnqualifiedAttribute(identities[0], "Id", out var id))
    {
      return Failure(
          "The VSIX Identity must contain an unambiguous Id attribute.",
          claimedIds: claimedIds);
    }

    if (!TryGetRequiredUnqualifiedAttribute(identities[0], "Version", out var version) ||
        !SemanticVersion.TryParse(version, out _))
    {
      return Failure(
          "The VSIX Identity must contain an unambiguous semantic Version attribute.",
          claimedId: id,
          claimedIds: claimedIds);
    }

    var installations = root.Elements(schema + "Installation").ToArray();
    if (installations.Length != 1 || root.Descendants(schema + "Installation").Count() != 1)
    {
      return Failure(
          "The VSIX manifest must contain one direct Installation element.",
          claimedId: id,
          claimedIds: claimedIds);
    }

    var targetElements = installations[0].Elements(schema + "InstallationTarget").ToArray();
    if (targetElements.Length == 0 ||
        root.Descendants(schema + "InstallationTarget").Count() != targetElements.Length)
    {
      return Failure(
          "VSIX Installation must contain at least one direct InstallationTarget.",
          claimedId: id,
          claimedIds: claimedIds);
    }

    var targets = new List<VsixInstallationTarget>(targetElements.Length);
    foreach (var targetElement in targetElements)
    {
      if (!TryGetRequiredUnqualifiedAttribute(targetElement, "Id", out var targetId) ||
          !TryGetOptionalUnqualifiedAttribute(targetElement, "Version", out var versionRange) ||
          !IsValidVersionRange(versionRange))
      {
        return Failure(
            "Every VSIX InstallationTarget must contain an unambiguous Id and valid optional Version range.",
            claimedId: id,
            claimedIds: claimedIds);
      }

      targets.Add(new VsixInstallationTarget(targetId, versionRange));
    }

    return new VsixManifestReadResult(
        new VsixManifest(id, version, manifestPath, instanceId, targets),
        null);
  }

  private static bool TryGetRequiredUnqualifiedAttribute(
      XElement element,
      string localName,
      out string value)
  {
    var attributes = element.Attributes()
        .Where(attribute => string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal))
        .ToArray();
    value = attributes.Length == 1 && attributes[0].Name.Namespace == XNamespace.None
        ? attributes[0].Value
        : string.Empty;
    return !string.IsNullOrWhiteSpace(value);
  }

  private static bool TryGetOptionalUnqualifiedAttribute(
      XElement element,
      string localName,
      out string? value)
  {
    var attributes = element.Attributes()
        .Where(attribute => string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal))
        .ToArray();
    if (attributes.Length == 0)
    {
      value = null;
      return true;
    }

    value = attributes.Length == 1 && attributes[0].Name.Namespace == XNamespace.None
        ? attributes[0].Value
        : null;
    return !string.IsNullOrWhiteSpace(value);
  }

  private static bool IsValidVersionRange(string? expression)
  {
    if (expression is null)
    {
      return true;
    }

    var range = expression.Trim();
    if (Version.TryParse(range, out _))
    {
      return true;
    }

    if (range.Length < 3 || range[0] is not ('[' or '(') || range[^1] is not (']' or ')'))
    {
      return false;
    }

    var bounds = range[1..^1].Split(',', StringSplitOptions.TrimEntries);
    if (bounds.Length == 1)
    {
      return range[0] == '[' && range[^1] == ']' && Version.TryParse(bounds[0], out _);
    }

    if (bounds.Length != 2 || (bounds[0].Length == 0 && bounds[1].Length == 0))
    {
      return false;
    }

    Version? minimum = null;
    Version? maximum = null;
    if (bounds[0].Length > 0 && !Version.TryParse(bounds[0], out minimum) ||
        bounds[1].Length > 0 && !Version.TryParse(bounds[1], out maximum))
    {
      return false;
    }

    if (minimum is null || maximum is null)
    {
      return true;
    }

    var comparison = minimum.CompareTo(maximum);
    return comparison < 0 || comparison == 0 && range[0] == '[' && range[^1] == ']';
  }

  private IReadOnlyList<string> GetInstalledExtensionRoots(VisualStudioInstance instance)
  {
    var roots = new List<string>
    {
      Path.Combine(instance.InstallationPath, "Common7", "IDE", "Extensions")
    };
    if (!string.IsNullOrWhiteSpace(_localApplicationData) &&
        Version.TryParse(instance.InstallationVersion, out var installationVersion))
    {
      var prefix = $"{installationVersion.Major}.0_";
      var profileDirectory = instance.InstanceId.StartsWith(
          prefix,
          StringComparison.OrdinalIgnoreCase)
          ? instance.InstanceId
          : $"{prefix}{instance.InstanceId}";
      roots.Add(Path.Combine(
          _localApplicationData,
          "Microsoft",
          "VisualStudio",
          profileDirectory,
          "Extensions"));
    }

    return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  }

  private static VsixManifestReadResult Failure(
      string detail,
      Exception? exception = null,
      string? claimedId = null,
      IReadOnlyList<string>? claimedIds = null) => new(
      null,
      new StructuredError(
          WdemErrorCode.ConfigurationError,
          "VSIX manifest is invalid.",
          detail)
      {
        UnderlyingException = exception
      },
      claimedId ?? (claimedIds?.Count == 1 ? claimedIds[0] : null),
      claimedIds);
}
