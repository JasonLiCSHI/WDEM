using System.Text.Json;
using Wdem.Core.Processes;

namespace Wdem.Windows.VisualStudio;

public sealed class VsWhereVisualStudioDiscovery : IVisualStudioDiscovery
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true
  };

  private readonly IProcessExecutor _processExecutor;
  private readonly string _vsWherePath;

  public VsWhereVisualStudioDiscovery(
      IProcessExecutor processExecutor,
      string? vsWherePath = null)
  {
    _processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
    _vsWherePath = vsWherePath ?? DefaultVsWherePath();
  }

  public async Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(
      IReadOnlyList<string> requestedWorkloads,
      IReadOnlyList<string> requestedComponents,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(requestedWorkloads);
    ArgumentNullException.ThrowIfNull(requestedComponents);
    var result = await _processExecutor.ExecuteAsync(
        new ProcessExecutionRequest(
            _vsWherePath,
            ["-products", "*", "-format", "json", "-utf8", "-prerelease"]),
        null,
        cancellationToken).ConfigureAwait(false);
    EnsureSuccessful(result, "Visual Studio instance query");

    var records = Deserialize(result);
    var instances = records.Select(Map).ToArray();
    var workloads = instances.ToDictionary(
        instance => instance.InstanceId,
        _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        StringComparer.OrdinalIgnoreCase);
    var components = instances.ToDictionary(
        instance => instance.InstanceId,
        _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        StringComparer.OrdinalIgnoreCase);

    await AddMembershipAsync(requestedWorkloads, workloads, cancellationToken)
        .ConfigureAwait(false);
    await AddMembershipAsync(requestedComponents, components, cancellationToken)
        .ConfigureAwait(false);
    return instances.Select(instance => instance with
    {
      Workloads = workloads[instance.InstanceId],
      Components = components[instance.InstanceId]
    }).ToArray();
  }

  private async Task AddMembershipAsync(
      IReadOnlyList<string> requestedIds,
      IReadOnlyDictionary<string, HashSet<string>> membership,
      CancellationToken cancellationToken)
  {
    foreach (var requestedId in requestedIds.Distinct(StringComparer.OrdinalIgnoreCase))
    {
      var result = await _processExecutor.ExecuteAsync(
          new ProcessExecutionRequest(
              _vsWherePath,
              [
                "-products", "*", "-requires", requestedId,
                "-format", "json", "-utf8"
              ]),
          null,
          cancellationToken).ConfigureAwait(false);
      EnsureSuccessful(result, $"Visual Studio membership query for '{requestedId}'");

      foreach (var record in Deserialize(result))
      {
        var instanceId = Required(record.InstanceId, "instanceId");
        if (membership.TryGetValue(instanceId, out var instanceMembership))
        {
          instanceMembership.Add(requestedId);
        }
      }
    }
  }

  private static VsWhereRecord[] Deserialize(ProcessExecutionResult result)
  {
    try
    {
      return JsonSerializer.Deserialize<VsWhereRecord[]>(
          string.Join(Environment.NewLine, result.StandardOutput),
          JsonOptions) ?? throw new InvalidDataException(
          "vswhere returned a null JSON root instead of an array.");
    }
    catch (JsonException exception)
    {
      throw new InvalidDataException("vswhere returned malformed JSON.", exception);
    }
  }

  private static void EnsureSuccessful(ProcessExecutionResult result, string operation)
  {
    if (result.Error is not null)
    {
      throw new InvalidOperationException(
          $"{operation} failed: {result.Error.Detail}",
          result.Error.UnderlyingException);
    }

    if (!result.Started || result.ExitCode != 0)
    {
      var details = result.StandardError.Count == 0
          ? "vswhere did not complete successfully."
          : string.Join(" ", result.StandardError);
      throw new InvalidOperationException(
          $"{operation} failed with exit code {result.ExitCode?.ToString() ?? "unknown"}: {details}");
    }
  }

  private static VisualStudioInstance Map(VsWhereRecord record)
  {
    var productId = Required(record.ProductId, "productId");
    return new VisualStudioInstance
    {
      InstanceId = Required(record.InstanceId, "instanceId"),
      InstallationPath = Required(record.InstallationPath, "installationPath"),
      ProductId = productId,
      ProductPath = Required(record.ProductPath, "productPath"),
      ProductDisplayVersion = Required(
          record.Catalog?.ProductDisplayVersion,
          "catalog.productDisplayVersion"),
      InstallationVersion = Required(record.InstallationVersion, "installationVersion"),
      ChannelId = Required(record.ChannelId, "channelId"),
      Edition = Edition(productId),
      IsComplete = Required(record.IsComplete, "isComplete"),
      IsLaunchable = Required(record.IsLaunchable, "isLaunchable")
    };
  }

  private static string Required(string? value, string propertyName)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      throw new InvalidDataException(
          $"vswhere record is missing required property '{propertyName}'.");
    }

    return value;
  }

  private static bool Required(bool? value, string propertyName) =>
      value ?? throw new InvalidDataException(
          $"vswhere record is missing required property '{propertyName}'.");

  private static string Edition(string? productId)
  {
    if (string.IsNullOrWhiteSpace(productId))
    {
      return string.Empty;
    }

    var separator = productId.LastIndexOf('.');
    return separator < 0 ? productId : productId[(separator + 1)..];
  }

  private static string DefaultVsWherePath()
  {
    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    if (string.IsNullOrWhiteSpace(programFiles))
    {
      programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? string.Empty;
    }

    return Path.Combine(
        programFiles,
        "Microsoft Visual Studio",
        "Installer",
        "vswhere.exe");
  }

  private sealed record VsWhereRecord
  {
    public string? InstanceId { get; init; }
    public string? InstallationPath { get; init; }
    public string? ProductId { get; init; }
    public string? ProductPath { get; init; }
    public string? InstallationVersion { get; init; }
    public string? ChannelId { get; init; }
    public bool? IsComplete { get; init; }
    public bool? IsLaunchable { get; init; }
    public VsWhereCatalog? Catalog { get; init; }
  }

  private sealed record VsWhereCatalog
  {
    public string? ProductDisplayVersion { get; init; }
  }
}
