using System.Reflection;
using System.Text.Json;
using Json.Schema;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Versions;
using YamlDotNet.Serialization;

namespace Wdem.Core.Profiles;

public sealed class DirectoryProfileCatalog : IProfileCatalog
{
  private static readonly Lazy<JsonSchema> ProfileSchema = new(LoadEmbeddedSchema);
  private readonly string _directory;
  private readonly IResourceProviderRegistry _providerRegistry;

  public DirectoryProfileCatalog(
      string directory,
      IResourceProviderRegistry providerRegistry)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    ArgumentNullException.ThrowIfNull(providerRegistry);

    _directory = Path.GetFullPath(directory);
    _providerRegistry = providerRegistry;
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
          $"Profile id '{id}' must be a file-name-safe id, not an absolute path or traversal path.",
          "/profile/id");
    }

    var yamlPath = Path.Combine(_directory, $"{id}.yaml");
    if (File.Exists(yamlPath))
    {
      return await LoadFileAsync(yamlPath, cancellationToken).ConfigureAwait(false);
    }

    var jsonPath = Path.Combine(_directory, $"{id}.json");
    if (File.Exists(jsonPath))
    {
      return await LoadFileAsync(jsonPath, cancellationToken).ConfigureAwait(false);
    }

    return Failure(
        Path.GetFullPath(yamlPath),
        "The requested profile was not found.",
        $"Neither '{Path.GetFileName(yamlPath)}' nor '{Path.GetFileName(jsonPath)}' exists in '{_directory}'.",
        "/profile/id");
  }

  public async Task<ProfileLoadResult> LoadFileAsync(
      string path,
      CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    string sourcePath;
    try
    {
      sourcePath = Path.GetFullPath(path);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return Failure(
          path,
          "The profile path is invalid.",
          exception.Message,
          string.Empty,
          exception);
    }

    var extension = Path.GetExtension(sourcePath);
    if (!extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) &&
        !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
    {
      return Failure(
          sourcePath,
          "The profile file extension is not supported.",
          $"File '{Path.GetFileName(sourcePath)}' has extension '{extension}'. Only .yaml and .json are supported.",
          string.Empty);
    }

    string text;
    try
    {
      text = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      return Failure(
          sourcePath,
          "The profile file could not be read.",
          exception.Message,
          string.Empty,
          exception);
    }

    JsonDocument json;
    try
    {
      var jsonText = extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
          ? ConvertYamlToJson(text)
          : text;
      json = JsonDocument.Parse(jsonText);
    }
    catch (Exception exception) when (
        exception is JsonException or YamlDotNet.Core.YamlException or InvalidOperationException)
    {
      return Failure(
          sourcePath,
          "The profile syntax is invalid.",
          exception.Message,
          string.Empty,
          exception);
    }

    using (json)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var schemaErrors = ValidateSchema(json.RootElement, sourcePath);
      var structuralErrors = ValidateRawSemanticGuards(json.RootElement, sourcePath);
      if (schemaErrors.Count > 0 || structuralErrors.Count > 0)
      {
        return new ProfileLoadResult
        {
          SourcePath = sourcePath,
          Errors = schemaErrors.Concat(structuralErrors).ToArray()
        };
      }

      ProfileDocument document;
      try
      {
        document = BuildDocument(json.RootElement);
      }
      catch (Exception exception) when (exception is JsonException or InvalidOperationException)
      {
        return Failure(
            sourcePath,
            "The profile could not be materialized.",
            exception.Message,
            string.Empty,
          exception);
      }

      var profile = document.Profile;
      var errors = await ValidateSemanticsAsync(
          json.RootElement,
          document,
          sourcePath,
          cancellationToken).ConfigureAwait(false);
      return new ProfileLoadResult
      {
        Profile = profile,
        SourcePath = sourcePath,
        Errors = errors
      };
    }
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
      return [Failure(
          _directory,
          "The profile directory could not be enumerated.",
          exception.Message,
          string.Empty,
          exception)];
    }

    var results = new List<ProfileLoadResult>(paths.Length);
    foreach (var path in paths)
    {
      cancellationToken.ThrowIfCancellationRequested();
      results.Add(await LoadFileAsync(path, cancellationToken).ConfigureAwait(false));
    }

    var duplicateGroups = results
        .Select((result, index) => (result, index))
        .Where(item => item.result.Profile is not null)
        .GroupBy(item => item.result.Profile!.Id, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() > 1);
    foreach (var group in duplicateGroups)
    {
      var files = string.Join(", ", group.Select(item => Path.GetFileName(item.result.SourcePath)));
      foreach (var item in group)
      {
        var error = CreateError(
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

    return results;
  }

  private static string ConvertYamlToJson(string yaml)
  {
    var deserializer = new DeserializerBuilder()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();
    var yamlObject = deserializer.Deserialize<object?>(yaml);
    return new SerializerBuilder().JsonCompatible().Build().Serialize(yamlObject);
  }

  private static IReadOnlyList<StructuredError> ValidateSchema(JsonElement root, string sourcePath)
  {
    var result = ProfileSchema.Value.Evaluate(root, new EvaluationOptions
    {
      OutputFormat = OutputFormat.List
    });
    if (result.IsValid)
    {
      return Array.Empty<StructuredError>();
    }

    var failures = ((IEnumerable<EvaluationResults>?)result.Details ?? Array.Empty<EvaluationResults>())
        .Where(detail => !detail.IsValid && detail.Errors is { Count: > 0 })
        .SelectMany(detail => detail.Errors!.Values.Select(message =>
            CreateError(
                sourcePath,
                "The profile does not match the developer profile schema.",
                message,
                detail.InstanceLocation.ToString())))
        .ToArray();
    return failures.Length > 0
        ? failures
        : [CreateError(
            sourcePath,
            "The profile does not match the developer profile schema.",
            "The schema validator did not provide further detail.",
            string.Empty)];
  }

  private static IReadOnlyList<StructuredError> ValidateRawSemanticGuards(
      JsonElement root,
      string sourcePath)
  {
    var errors = new List<StructuredError>();
    if (root.ValueKind != JsonValueKind.Object)
    {
      return errors;
    }

    if (root.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
    {
      AddWhitespaceError(profile, "id", "/profile/id", "profile id", sourcePath, errors);
      AddReferenceWhitespaceErrors(profile, "requiredResources", sourcePath, errors);
      AddReferenceWhitespaceErrors(profile, "optionalResources", sourcePath, errors);
    }

    if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Object)
    {
      return errors;
    }

    var resourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var property in resources.EnumerateObject())
    {
      var resourcePointer = $"/resources/{ProfileValueExpander.EscapePointer(property.Name)}";
      if (string.IsNullOrWhiteSpace(property.Name))
      {
        errors.Add(CreateError(sourcePath, "The resource id cannot be blank.",
            "Resource ids must contain at least one non-whitespace character.", resourcePointer));
      }

      if (!resourceIds.Add(property.Name))
      {
        errors.Add(CreateError(sourcePath, "A resource id is duplicated.",
            $"Resource id '{property.Name}' conflicts with another id when compared case-insensitively.",
            resourcePointer));
      }

      if (property.Value.ValueKind != JsonValueKind.Object)
      {
        continue;
      }

      AddWhitespaceError(property.Value, "type", $"{resourcePointer}/type", "resource type", sourcePath, errors);
      AddWhitespaceError(
          property.Value,
          "provider",
          $"{resourcePointer}/provider",
          "resource provider",
          sourcePath,
          errors);

      if (!property.Value.TryGetProperty("parameters", out var parameters) ||
          parameters.ValueKind != JsonValueKind.Object)
      {
        continue;
      }

      var parameterKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var parameter in parameters.EnumerateObject())
      {
        if (!parameterKeys.Add(parameter.Name))
        {
          errors.Add(CreateError(sourcePath, "A parameter key is duplicated.",
              $"Parameter key '{parameter.Name}' conflicts with another key when compared case-insensitively.",
              $"{resourcePointer}/parameters/{ProfileValueExpander.EscapePointer(parameter.Name)}"));
        }
      }
    }

    return errors;
  }

  private static void AddReferenceWhitespaceErrors(
      JsonElement profile,
      string propertyName,
      string sourcePath,
      List<StructuredError> errors)
  {
    if (!profile.TryGetProperty(propertyName, out var references) ||
        references.ValueKind != JsonValueKind.Array)
    {
      return;
    }

    var index = 0;
    foreach (var reference in references.EnumerateArray())
    {
      if (reference.ValueKind == JsonValueKind.Object)
      {
        AddWhitespaceError(
            reference,
            "id",
            $"/profile/{propertyName}/{index}/id",
            "resource reference id",
            sourcePath,
            errors);
      }

      index++;
    }
  }

  private static void AddWhitespaceError(
      JsonElement element,
      string propertyName,
      string pointer,
      string fieldName,
      string sourcePath,
      List<StructuredError> errors)
  {
    if (element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        string.IsNullOrWhiteSpace(value.GetString()))
    {
      errors.Add(CreateError(sourcePath, $"The {fieldName} cannot be blank.",
          $"The {fieldName} must contain at least one non-whitespace character.", pointer));
    }
  }

  private static ProfileDocument BuildDocument(JsonElement root)
  {
    var profileElement = root.GetProperty("profile");
    var resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase);
    foreach (var property in root.GetProperty("resources").EnumerateObject())
    {
      var resource = property.Value;
      if (resources.ContainsKey(property.Name))
      {
        continue;
      }

      resources.Add(property.Name, new ResourceDefinition
      {
        Id = property.Name,
        Type = resource.GetProperty("type").GetString()!,
        Provider = resource.GetProperty("provider").GetString()!,
        VersionConstraint = GetOptionalString(resource, "versionConstraint"),
        PreferredVersion = GetOptionalString(resource, "preferredVersion"),
        Dependencies = GetStringArray(resource, "dependsOn"),
        Parameters = GetParameters(resource)
      });
    }

    var profile = new DeveloperProfile
    {
      Id = profileElement.GetProperty("id").GetString()!,
      Version = profileElement.GetProperty("version").GetString()!,
      DisplayName = profileElement.GetProperty("displayName").GetString()!,
      Description = profileElement.GetProperty("description").GetString()!,
      RequiredResources = GetReferences(profileElement, "requiredResources"),
      OptionalResources = GetReferences(profileElement, "optionalResources"),
      Resources = resources
    };

    return new ProfileDocument
    {
      SchemaVersion = root.GetProperty("schemaVersion").GetString()!,
      Profile = profile,
      Resources = resources
    };
  }

  private async Task<IReadOnlyList<StructuredError>> ValidateSemanticsAsync(
      JsonElement root,
      ProfileDocument document,
      string sourcePath,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var profile = document.Profile;
    var errors = new List<StructuredError>();
    if (string.IsNullOrWhiteSpace(profile.Id))
    {
      errors.Add(CreateError(sourcePath, "The profile id cannot be blank.",
          "The profile id must contain at least one non-whitespace character.", "/profile/id"));
    }

    if (!SemanticVersion.TryParse(profile.Version, out _))
    {
      errors.Add(CreateError(sourcePath, "The profile version is invalid.",
          $"'{profile.Version}' is not a semantic version.", "/profile/version"));
    }

    var allReferences = profile.RequiredResources
        .Select((reference, index) => (reference, pointer: $"/profile/requiredResources/{index}"))
        .Concat(profile.OptionalResources
            .Select((reference, index) => (reference, pointer: $"/profile/optionalResources/{index}")))
        .ToArray();
    var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in allReferences)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (string.IsNullOrWhiteSpace(item.reference.Id))
      {
        errors.Add(CreateError(sourcePath, "The resource reference id cannot be blank.",
            "Resource reference ids must contain at least one non-whitespace character.",
            $"{item.pointer}/id"));
        continue;
      }

      if (!referenced.Add(item.reference.Id))
      {
        errors.Add(CreateError(sourcePath, "A resource is referenced more than once.",
            $"Resource '{item.reference.Id}' is duplicated.", $"{item.pointer}/id"));
      }

      if (!document.Resources.ContainsKey(item.reference.Id))
      {
        errors.Add(CreateError(sourcePath, "A resource reference is unknown.",
            $"Resource '{item.reference.Id}' is not defined in the resources map.", $"{item.pointer}/id"));
      }

      ValidateVersions(
          item.reference.VersionConstraint,
          item.reference.PreferredVersion,
          item.pointer,
          sourcePath,
          errors);
    }

    var seenResourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var property in root.GetProperty("resources").EnumerateObject())
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (!seenResourceIds.Add(property.Name))
      {
        errors.Add(CreateError(sourcePath, "A resource id is duplicated.",
            $"Resource id '{property.Name}' differs from another id only by casing.",
            $"/resources/{ProfileValueExpander.EscapePointer(property.Name)}"));
        continue;
      }

      var resource = document.Resources[property.Name];
      var resourcePointer = $"/resources/{ProfileValueExpander.EscapePointer(property.Name)}";
      if (string.IsNullOrWhiteSpace(property.Name))
      {
        errors.Add(CreateError(sourcePath, "The resource id cannot be blank.",
            "Resource ids must contain at least one non-whitespace character.", resourcePointer));
        continue;
      }

      var hasBlankProviderIdentity = false;
      if (string.IsNullOrWhiteSpace(resource.Type))
      {
        errors.Add(CreateError(sourcePath, "The resource type cannot be blank.",
            "Resource types must contain at least one non-whitespace character.", $"{resourcePointer}/type"));
        hasBlankProviderIdentity = true;
      }

      if (string.IsNullOrWhiteSpace(resource.Provider))
      {
        errors.Add(CreateError(sourcePath, "The resource provider cannot be blank.",
            "Resource providers must contain at least one non-whitespace character.",
            $"{resourcePointer}/provider"));
        hasBlankProviderIdentity = true;
      }

      ValidateVersions(resource.VersionConstraint, resource.PreferredVersion, resourcePointer, sourcePath, errors);

      for (var index = 0; index < resource.Dependencies.Count; index++)
      {
        var dependency = resource.Dependencies[index];
        if (!document.Resources.ContainsKey(dependency))
        {
          errors.Add(CreateError(sourcePath, "A resource dependency is unknown.",
              $"Dependency '{dependency}' is not defined in the resources map.",
              $"{resourcePointer}/dependsOn/{index}"));
        }
      }

      if (hasBlankProviderIdentity)
      {
        continue;
      }

      cancellationToken.ThrowIfCancellationRequested();
      if (!_providerRegistry.TryGet(resource.Type, resource.Provider, out var provider) || provider is null)
      {
        errors.Add(CreateError(sourcePath, "The resource provider is not registered.",
            $"No provider named '{resource.Provider}' is registered for resource type '{resource.Type}'.",
            $"{resourcePointer}/provider"));
        continue;
      }

      ProviderValidationResult validation;
      try
      {
        validation = await provider.ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception exception)
      {
        errors.Add(CreateError(sourcePath, "The resource provider failed during validation.",
            exception.Message, resourcePointer, exception));
        continue;
      }

      foreach (var validationError in validation.Errors)
      {
        cancellationToken.ThrowIfCancellationRequested();
        errors.Add(CreateError(sourcePath, "The resource provider rejected the resource.",
            validationError, resourcePointer));
      }
    }

    cancellationToken.ThrowIfCancellationRequested();
    return errors;
  }

  private static void ValidateVersions(
      string? versionConstraint,
      string? preferredVersion,
      string pointer,
      string sourcePath,
      List<StructuredError> errors)
  {
    if (versionConstraint is not null)
    {
      try
      {
        _ = VersionConstraint.Parse(versionConstraint);
      }
      catch (FormatException exception)
      {
        errors.Add(CreateError(sourcePath, "The version constraint is invalid.",
            exception.Message, $"{pointer}/versionConstraint", exception));
      }
    }

    if (preferredVersion is not null && !SemanticVersion.TryParse(preferredVersion, out _))
    {
      errors.Add(CreateError(sourcePath, "The preferred version is invalid.",
          $"'{preferredVersion}' is not a semantic version.", $"{pointer}/preferredVersion"));
    }
  }

  private static IReadOnlyList<ProfileResourceReference> GetReferences(
      JsonElement profile,
      string propertyName)
  {
    if (!profile.TryGetProperty(propertyName, out var references))
    {
      return Array.Empty<ProfileResourceReference>();
    }

    return references.EnumerateArray().Select(reference => new ProfileResourceReference
    {
      Id = reference.GetProperty("id").GetString()!,
      VersionConstraint = GetOptionalString(reference, "versionConstraint"),
      PreferredVersion = GetOptionalString(reference, "preferredVersion"),
      DefaultSelected = reference.TryGetProperty("defaultSelected", out var selected) && selected.GetBoolean()
    }).ToArray();
  }

  private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName) =>
      element.TryGetProperty(propertyName, out var array)
          ? array.EnumerateArray().Select(item => item.GetString()!).ToArray()
          : Array.Empty<string>();

  private static IReadOnlyDictionary<string, string?> GetParameters(JsonElement resource)
  {
    if (!resource.TryGetProperty("parameters", out var parameters))
    {
      return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (var property in parameters.EnumerateObject())
    {
      result.TryAdd(
          property.Name,
          property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString());
    }

    return result;
  }

  private static string? GetOptionalString(JsonElement element, string propertyName) =>
      element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;

  private static bool IsSafeProfileId(string? id)
  {
    if (string.IsNullOrWhiteSpace(id) || Path.IsPathRooted(id) || id is "." or "..")
    {
      return false;
    }

    return id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !id.Contains(Path.DirectorySeparatorChar) &&
        !id.Contains(Path.AltDirectorySeparatorChar) &&
        string.Equals(Path.GetFileName(id), id, StringComparison.Ordinal);
  }

  private static ProfileLoadResult Failure(
      string sourcePath,
      string summary,
      string detail,
      string pointer,
      Exception? exception = null) => new()
  {
    SourcePath = sourcePath,
    Errors = [CreateError(sourcePath, summary, detail, pointer, exception)]
  };

  private static StructuredError CreateError(
      string sourcePath,
      string summary,
      string detail,
      string pointer,
      Exception? exception = null)
  {
    var location = string.IsNullOrEmpty(pointer) ? "" : $" at '{pointer}'";
    return new StructuredError(
        WdemErrorCode.ProfileError,
        summary,
        $"Profile file '{Path.GetFileName(sourcePath)}'{location}: {detail}")
    {
      UnderlyingException = exception,
      SuggestedAction = "Correct the profile and load it again."
    };
  }

  private static JsonSchema LoadEmbeddedSchema()
  {
    var assembly = typeof(DirectoryProfileCatalog).Assembly;
    var resourceName = assembly.GetManifestResourceNames().Single(
        name => name.EndsWith("developer-profile.schema.json", StringComparison.Ordinal));
    using var stream = assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException("The developer profile schema resource is missing.");
    using var reader = new StreamReader(stream);
    return JsonSchema.FromText(reader.ReadToEnd());
  }
}
