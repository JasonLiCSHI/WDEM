using System.Reflection;
using System.Text.Json;
using Json.Schema;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Versions;

namespace Wdem.Core.Profiles;

internal sealed class ProfileValidator(IResourceProviderRegistry providerRegistry)
{
  private static readonly Lazy<JsonSchema> ProfileSchema = new(LoadEmbeddedSchema);

  public async Task<ProfileValidationResult> ValidateAsync(
      JsonElement root,
      string sourcePath,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var schemaErrors = ValidateSchema(root, sourcePath);
    cancellationToken.ThrowIfCancellationRequested();
    var structuralErrors = ValidateRawSemanticGuards(root, sourcePath, cancellationToken);
    cancellationToken.ThrowIfCancellationRequested();
    if (schemaErrors.Count > 0 || structuralErrors.Count > 0)
    {
      return new ProfileValidationResult(
          null,
          schemaErrors.Concat(structuralErrors).ToArray());
    }

    ProfileDocument document;
    try
    {
      document = ProfileDocumentMapper.Map(root);
    }
    catch (Exception exception) when (exception is JsonException or InvalidOperationException)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return new ProfileValidationResult(null, [ProfileErrorFactory.FromException(
          sourcePath,
          "The profile could not be materialized.",
          "The validated document could not be mapped.",
          string.Empty,
          exception)]);
    }

    cancellationToken.ThrowIfCancellationRequested();
    var errors = await ValidateSemanticsAsync(
        root,
        document,
        sourcePath,
        cancellationToken).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return new ProfileValidationResult(document, errors);
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
            ProfileErrorFactory.Create(
                sourcePath,
                "The profile does not match the developer profile schema.",
                message,
                detail.InstanceLocation.ToString())))
        .ToArray();
    return failures.Length > 0
        ? failures
        : [ProfileErrorFactory.Create(
            sourcePath,
            "The profile does not match the developer profile schema.",
            "The schema validator did not provide further detail.")];
  }

  private static IReadOnlyList<StructuredError> ValidateRawSemanticGuards(
      JsonElement root,
      string sourcePath,
      CancellationToken cancellationToken)
  {
    var errors = new List<StructuredError>();
    if (root.ValueKind != JsonValueKind.Object)
    {
      return errors;
    }

    if (root.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
    {
      AddWhitespaceError(profile, "id", "/profile/id", "profile id", sourcePath, errors);
      AddReferenceWhitespaceErrors(profile, "requiredResources", sourcePath, errors, cancellationToken);
      AddReferenceWhitespaceErrors(profile, "optionalResources", sourcePath, errors, cancellationToken);
    }

    if (!root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Object)
    {
      return errors;
    }

    var resourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var property in resources.EnumerateObject())
    {
      cancellationToken.ThrowIfCancellationRequested();
      var resourcePointer = $"/resources/{ProfileValueExpander.EscapePointer(property.Name)}";
      if (string.IsNullOrWhiteSpace(property.Name))
      {
        errors.Add(ProfileErrorFactory.Create(sourcePath, "The resource id cannot be blank.",
            "Resource ids must contain at least one non-whitespace character.", resourcePointer));
      }

      if (!resourceIds.Add(property.Name))
      {
        errors.Add(ProfileErrorFactory.Create(sourcePath, "A resource id is duplicated.",
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
        cancellationToken.ThrowIfCancellationRequested();
        if (!parameterKeys.Add(parameter.Name))
        {
          errors.Add(ProfileErrorFactory.Create(sourcePath, "A parameter key is duplicated.",
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
      List<StructuredError> errors,
      CancellationToken cancellationToken)
  {
    if (!profile.TryGetProperty(propertyName, out var references) ||
        references.ValueKind != JsonValueKind.Array)
    {
      return;
    }

    var index = 0;
    foreach (var reference in references.EnumerateArray())
    {
      cancellationToken.ThrowIfCancellationRequested();
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
      errors.Add(ProfileErrorFactory.Create(sourcePath, $"The {fieldName} cannot be blank.",
          $"The {fieldName} must contain at least one non-whitespace character.", pointer));
    }
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
      errors.Add(ProfileErrorFactory.Create(sourcePath, "The profile id cannot be blank.",
          "The profile id must contain at least one non-whitespace character.", "/profile/id"));
    }

    if (!SemanticVersion.TryParse(profile.Version, out _))
    {
      errors.Add(ProfileErrorFactory.Create(sourcePath, "The profile version is invalid.",
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
        errors.Add(ProfileErrorFactory.Create(sourcePath, "The resource reference id cannot be blank.",
            "Resource reference ids must contain at least one non-whitespace character.",
            $"{item.pointer}/id"));
        continue;
      }

      if (!referenced.Add(item.reference.Id))
      {
        errors.Add(ProfileErrorFactory.Create(sourcePath, "A resource is referenced more than once.",
            $"Resource '{item.reference.Id}' is duplicated.", $"{item.pointer}/id"));
      }

      if (!document.Resources.ContainsKey(item.reference.Id))
      {
        errors.Add(ProfileErrorFactory.Create(sourcePath, "A resource reference is unknown.",
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
        errors.Add(ProfileErrorFactory.Create(sourcePath, "A resource id is duplicated.",
            $"Resource id '{property.Name}' differs from another id only by casing.",
            $"/resources/{ProfileValueExpander.EscapePointer(property.Name)}"));
        continue;
      }

      var resource = document.Resources[property.Name];
      var resourcePointer = $"/resources/{ProfileValueExpander.EscapePointer(property.Name)}";
      if (string.IsNullOrWhiteSpace(property.Name))
      {
        errors.Add(ProfileErrorFactory.Create(sourcePath, "The resource id cannot be blank.",
            "Resource ids must contain at least one non-whitespace character.", resourcePointer));
        continue;
      }

      var hasBlankProviderIdentity = false;
      if (string.IsNullOrWhiteSpace(resource.Type))
      {
        errors.Add(ProfileErrorFactory.Create(sourcePath, "The resource type cannot be blank.",
            "Resource types must contain at least one non-whitespace character.", $"{resourcePointer}/type"));
        hasBlankProviderIdentity = true;
      }

      if (string.IsNullOrWhiteSpace(resource.Provider))
      {
        errors.Add(ProfileErrorFactory.Create(sourcePath, "The resource provider cannot be blank.",
            "Resource providers must contain at least one non-whitespace character.",
            $"{resourcePointer}/provider"));
        hasBlankProviderIdentity = true;
      }

      ValidateVersions(resource.VersionConstraint, resource.PreferredVersion, resourcePointer, sourcePath, errors);
      for (var index = 0; index < resource.Dependencies.Count; index++)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var dependency = resource.Dependencies[index];
        if (!document.Resources.ContainsKey(dependency))
        {
          errors.Add(ProfileErrorFactory.Create(sourcePath, "A resource dependency is unknown.",
              $"Dependency '{dependency}' is not defined in the resources map.",
              $"{resourcePointer}/dependsOn/{index}"));
        }
      }

      if (hasBlankProviderIdentity)
      {
        continue;
      }

      cancellationToken.ThrowIfCancellationRequested();
      if (!providerRegistry.TryGet(resource.Type, resource.Provider, out var provider) || provider is null)
      {
        errors.Add(ProfileErrorFactory.Create(sourcePath, "The resource provider is not registered.",
            $"No provider named '{resource.Provider}' is registered for resource type '{resource.Type}'.",
            $"{resourcePointer}/provider"));
        continue;
      }

      string[] validationErrors;
      try
      {
        var validation = await provider.ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (validation?.Errors is null)
        {
          throw new InvalidOperationException(
              "Provider validation contract violation: the result, Errors collection, and its entries must not be null.");
        }

        validationErrors = new string[validation.Errors.Count];
        for (var index = 0; index < validationErrors.Length; index++)
        {
          validationErrors[index] = validation.Errors[index] ?? throw new InvalidOperationException(
              "Provider validation contract violation: the result, Errors collection, and its entries must not be null.");
        }
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (ArgumentException exception)
      {
        errors.Add(ProfileErrorFactory.FromException(
            sourcePath,
            "The resource provider violated the validation contract.",
            "Provider validation contract violation:",
            resourcePointer,
            exception));
        continue;
      }
      catch (Exception exception)
      {
        errors.Add(ProfileErrorFactory.FromException(
            sourcePath,
            "The resource provider failed during validation.",
            "The provider did not return a valid validation result.",
            resourcePointer,
            exception));
        continue;
      }

      foreach (var validationError in validationErrors)
      {
        cancellationToken.ThrowIfCancellationRequested();
        errors.Add(ProfileErrorFactory.Create(sourcePath, "The resource provider rejected the resource.",
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
        errors.Add(ProfileErrorFactory.FromException(
            sourcePath,
            "The version constraint is invalid.",
            "The version constraint parser rejected the value.",
            $"{pointer}/versionConstraint",
            exception));
      }
    }

    if (preferredVersion is not null && !SemanticVersion.TryParse(preferredVersion, out _))
    {
      errors.Add(ProfileErrorFactory.Create(sourcePath, "The preferred version is invalid.",
          $"'{preferredVersion}' is not a semantic version.", $"{pointer}/preferredVersion"));
    }
  }

  private static JsonSchema LoadEmbeddedSchema()
  {
    var assembly = typeof(ProfileValidator).Assembly;
    var resourceName = assembly.GetManifestResourceNames().Single(
        name => name.EndsWith("developer-profile.schema.json", StringComparison.Ordinal));
    using var stream = assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException("The developer profile schema resource is missing.");
    using var reader = new StreamReader(stream);
    return JsonSchema.FromText(reader.ReadToEnd());
  }
}

internal sealed record ProfileValidationResult(
    ProfileDocument? Document,
    IReadOnlyList<StructuredError> Errors)
{
  public bool IsValid => Document is not null && Errors.Count == 0;
}
