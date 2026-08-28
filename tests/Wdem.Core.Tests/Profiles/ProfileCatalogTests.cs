using System.Reflection;
using Wdem.Core.Execution;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Xunit;

namespace Wdem.Core.Tests.Profiles;

public sealed class ProfileCatalogTests
{
  [Fact]
  public void DeveloperProfile_NormalizesAssignedResourceDictionaryToOrdinalIgnoreCase()
  {
    var profile = new DeveloperProfile
    {
      Id = "developer",
      Version = "1.0.0",
      DisplayName = "Developer",
      Description = "Description",
      Resources = new Dictionary<string, ResourceDefinition>
      {
        ["git"] = new() { Id = "git", Type = "package", Provider = "winget" }
      }
    };

    Assert.Same(profile.Resources["git"], profile.Resources["GIT"]);
  }

  [Fact]
  public async Task LoadFileAsync_LoadsYamlProfileAndPreservesUnselectedOptionalToken()
  {
    var catalog = CreateCatalog(TestDataDirectory);

    var result = await catalog.LoadFileAsync(Path.Combine(TestDataDirectory, "valid-csharp.yaml"));

    Assert.True(result.IsValid, FormatErrors(result.Errors));
    Assert.Equal("csharp-developer", result.Profile!.Id);
    Assert.Equal(["git", "dotnet-sdk"], result.Profile.RequiredResources.Select(item => item.Id));
    var optional = Assert.Single(result.Profile.OptionalResources, item => item.Id == "resharper");
    Assert.True(optional.DefaultSelected);
    Assert.Equal(
        "${WDEM_COMPANY_VSIX_PATH}",
        result.Profile.Resources["company-vsix"].Parameters["path"]);
    Assert.Equal(Path.GetFullPath(Path.Combine(TestDataDirectory, "valid-csharp.yaml")), result.SourcePath);
  }

  [Fact]
  public async Task LoadFileAsync_LoadsEquivalentJsonProfile()
  {
    var result = await CreateCatalog(TestDataDirectory)
        .LoadFileAsync(Path.Combine(TestDataDirectory, "valid-csharp.json"));

    Assert.True(result.IsValid, FormatErrors(result.Errors));
    Assert.Equal("csharp-developer", result.Profile!.Id);
    Assert.Equal(["git", "dotnet-sdk"], result.Profile.RequiredResources.Select(item => item.Id));
    Assert.True(Assert.Single(result.Profile.OptionalResources).DefaultSelected);
    Assert.True(result.Profile.Resources.ContainsKey("GIT"));
  }

  [Fact]
  public async Task LoadFileAsync_ResolvesYamlAliasesWithoutLosingScalarTypes()
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write("aliases.yaml", """
        schemaVersion: "1.0"
        profile:
          id: alias-profile
          version: "1.0.0"
          displayName: Alias Profile
          description: Uses a YAML alias.
          requiredResources:
            - id: git
          optionalResources:
            - id: tool
              defaultSelected: true
        resources:
          git: &package
            type: package
            provider: winget
          tool: *package
        """);

    var result = await CreateCatalog(directory.Path).LoadFileAsync(path);

    Assert.True(result.IsValid, FormatErrors(result.Errors));
    Assert.True(Assert.Single(result.Profile!.OptionalResources).DefaultSelected);
    Assert.Equal("winget", result.Profile.Resources["tool"].Provider);
  }

  [Fact]
  public async Task LoadFileAsync_DoesNotCoerceYamlParameterTypesToStrings()
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write("typed.yaml", """
        schemaVersion: "1.0"
        profile:
          id: typed
          version: "1.0.0"
          displayName: Typed
          description: Invalid parameter type.
          requiredResources:
            - id: git
        resources:
          git:
            type: package
            provider: winget
            parameters:
              retries: 3
        """);

    var result = await CreateCatalog(directory.Path).LoadFileAsync(path);

    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, error =>
        error.Detail.Contains("/resources/git/parameters/retries", StringComparison.Ordinal));
  }

  [Fact]
  public async Task LoadFileAsync_UnknownProviderReturnsOneActionableProfileError()
  {
    var result = await CreateCatalog(TestDataDirectory)
        .LoadFileAsync(Path.Combine(TestDataDirectory, "invalid-provider.json"));

    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.ProfileError, error.Code);
    Assert.Contains("invalid-provider.json", error.Detail, StringComparison.Ordinal);
    Assert.Contains("/resources/git/provider", error.Detail, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("unknown-field.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"surprise\":true},\"resources\":{}}", "/profile")]
  [InlineData("missing-type.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"git\"}]},\"resources\":{\"git\":{\"provider\":\"winget\"}}}", "/resources/git")]
  [InlineData("missing-provider.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"git\"}]},\"resources\":{\"git\":{\"type\":\"package\"}}}", "/resources/git")]
  public async Task LoadFileAsync_SchemaViolationsIncludeJsonPointer(string fileName, string contents, string pointer)
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write(fileName, contents);

    var result = await CreateCatalog(directory.Path).LoadFileAsync(path);

    Assert.False(result.IsValid);
    Assert.All(result.Errors, error => Assert.Equal(WdemErrorCode.ProfileError, error.Code));
    Assert.Contains(result.Errors, error => error.Detail.Contains(pointer, StringComparison.Ordinal));
  }

  [Theory]
  [InlineData("duplicate-ref.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"git\"},{\"id\":\"GIT\"}]},\"resources\":{\"git\":{\"type\":\"package\",\"provider\":\"winget\"}}}", "/profile/requiredResources/1/id")]
  [InlineData("unknown-ref.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"missing\"}]},\"resources\":{}}", "/profile/requiredResources/0/id")]
  [InlineData("unknown-dependency.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"git\"}]},\"resources\":{\"git\":{\"type\":\"package\",\"provider\":\"winget\",\"dependsOn\":[\"missing\"]}}}", "/resources/git/dependsOn/0")]
  [InlineData("bad-constraint.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"git\"}]},\"resources\":{\"git\":{\"type\":\"package\",\"provider\":\"winget\",\"versionConstraint\":\"latest\"}}}", "/resources/git/versionConstraint")]
  [InlineData("bad-preferred.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"git\"}]},\"resources\":{\"git\":{\"type\":\"package\",\"provider\":\"winget\",\"preferredVersion\":\"vNext\"}}}", "/resources/git/preferredVersion")]
  public async Task LoadFileAsync_SemanticViolationsIncludeExactJsonPointer(string fileName, string contents, string pointer)
  {
    using var directory = new TemporaryDirectory();
    var result = await CreateCatalog(directory.Path).LoadFileAsync(directory.Write(fileName, contents));

    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, error => error.Detail.Contains(pointer, StringComparison.Ordinal));
  }

  [Fact]
  public async Task LoadFileAsync_ReportsProviderValidationFailure()
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write("provider-rejects.json", ValidSingleResourceJson("git"));
    var registry = new ResourceProviderRegistry([new StubProvider("package", "winget", "package unavailable")]);

    var result = await new DirectoryProfileCatalog(directory.Path, registry).LoadFileAsync(path);

    var error = Assert.Single(result.Errors);
    Assert.Contains("/resources/git", error.Detail, StringComparison.Ordinal);
    Assert.Contains("package unavailable", error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LoadFileAsync_EscapesResourceIdInJsonPointer()
  {
    using var directory = new TemporaryDirectory();
    var contents = """
        {
          "schemaVersion": "1.0",
          "profile": {
            "id": "escaped",
            "version": "1.0.0",
            "displayName": "Escaped",
            "description": "Escaped pointer",
            "requiredResources": [{ "id": "git/tools~beta" }]
          },
          "resources": {
            "git/tools~beta": { "type": "package", "provider": "missing" }
          }
        }
        """;

    var result = await CreateCatalog(directory.Path)
        .LoadFileAsync(directory.Write("escaped.json", contents));

    Assert.Contains("/resources/git~1tools~0beta/provider", Assert.Single(result.Errors).Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LoadFileAsync_YamlSyntaxErrorIncludesFileName()
  {
    using var directory = new TemporaryDirectory();
    var result = await CreateCatalog(directory.Path)
        .LoadFileAsync(directory.Write("broken.yaml", "profile: [unterminated"));

    Assert.False(result.IsValid);
    Assert.Contains("broken.yaml", Assert.Single(result.Errors).Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LoadFileAsync_RejectsUnsupportedExtension()
  {
    using var directory = new TemporaryDirectory();
    var result = await CreateCatalog(directory.Path)
        .LoadFileAsync(directory.Write("profile.txt", "{}"));

    Assert.False(result.IsValid);
    Assert.Contains(".txt", Assert.Single(result.Errors).Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LoadFileAsync_MissingFileReturnsProfileError()
  {
    using var directory = new TemporaryDirectory();

    var result = await CreateCatalog(directory.Path)
        .LoadFileAsync(Path.Combine(directory.Path, "missing.yaml"));

    Assert.False(result.IsValid);
    Assert.Equal(WdemErrorCode.ProfileError, Assert.Single(result.Errors).Code);
    Assert.Contains("missing.yaml", result.Errors[0].Detail, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("../outside")]
  [InlineData("..\\outside")]
  [InlineData("C:\\outside")]
  public async Task LoadAsync_RejectsUnsafeProfileId(string id)
  {
    using var directory = new TemporaryDirectory();

    var result = await CreateCatalog(directory.Path).LoadAsync(id);

    Assert.False(result.IsValid);
    Assert.Equal(WdemErrorCode.ProfileError, Assert.Single(result.Errors).Code);
  }

  [Fact]
  public async Task LoadAsync_PrefersYamlThenFallsBackToJson()
  {
    using var directory = new TemporaryDirectory();
    directory.Write("choice.json", ValidSingleResourceJson("choice"));
    directory.Write("choice.yaml", "not: a-profile");

    var result = await CreateCatalog(directory.Path).LoadAsync("choice");

    Assert.EndsWith("choice.yaml", result.SourcePath, StringComparison.OrdinalIgnoreCase);
    Assert.False(result.IsValid);
  }

  [Fact]
  public async Task LoadAllAsync_SortsFilesAndReportsDuplicateProfileIds()
  {
    using var directory = new TemporaryDirectory();
    directory.Write("b.json", ValidSingleResourceJson("same"));
    directory.Write("a.yaml", ValidSingleResourceYaml("same"));
    directory.Write("c.json", ValidSingleResourceJson("unique"));

    var results = await CreateCatalog(directory.Path).LoadAllAsync();

    Assert.Equal(["a.yaml", "b.json", "c.json"], results.Select(result => Path.GetFileName(result.SourcePath)));
    Assert.False(results[0].IsValid);
    Assert.False(results[1].IsValid);
    Assert.Contains(results.SelectMany(result => result.Errors), error => error.Detail.Contains("duplicate profile id", StringComparison.OrdinalIgnoreCase));
    Assert.True(results[2].IsValid, FormatErrors(results[2].Errors));
  }

  [Fact]
  public async Task LoadAllAsync_ReportsBlankProfileId()
  {
    using var directory = new TemporaryDirectory();
    directory.Write("blank.json", ValidSingleResourceJson(""));

    var result = Assert.Single(await CreateCatalog(directory.Path).LoadAllAsync());

    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, error => error.Detail.Contains("/profile/id", StringComparison.Ordinal));
  }

  [Fact]
  public async Task LoadFileAsync_ObservesPreCancelledToken()
  {
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        CreateCatalog(TestDataDirectory)
            .LoadFileAsync(Path.Combine(TestDataDirectory, "valid-csharp.yaml"), cancellation.Token));
  }

  [Fact]
  public void Schema_IsEmbeddedAndRejectsUnknownFields()
  {
    var assembly = typeof(DeveloperProfile).Assembly;
    var resourceName = Assert.Single(
        assembly.GetManifestResourceNames(),
        name => name.EndsWith("developer-profile.schema.json", StringComparison.Ordinal));

    using var stream = assembly.GetManifestResourceStream(resourceName);
    using var reader = new StreamReader(Assert.IsAssignableFrom<Stream>(stream));
    var schema = reader.ReadToEnd();

    Assert.Contains("\"additionalProperties\": false", schema, StringComparison.Ordinal);
  }

  private static string TestDataDirectory =>
      Path.Combine(AppContext.BaseDirectory, "TestData", "Profiles");

  private static DirectoryProfileCatalog CreateCatalog(string directory) =>
      new(directory, new ResourceProviderRegistry([
        new StubProvider("package", "winget"),
        new StubProvider("extension", "file")
      ]));

  private static string ValidSingleResourceJson(string profileId) => $$"""
      {
        "schemaVersion": "1.0",
        "profile": {
          "id": "{{profileId}}",
          "version": "1.0.0",
          "displayName": "Profile",
          "description": "Description",
          "requiredResources": [{ "id": "git" }]
        },
        "resources": { "git": { "type": "package", "provider": "winget" } }
      }
      """;

  private static string ValidSingleResourceYaml(string profileId) => $$"""
      schemaVersion: "1.0"
      profile:
        id: "{{profileId}}"
        version: "1.0.0"
        displayName: Profile
        description: Description
        requiredResources:
          - id: git
      resources:
        git:
          type: package
          provider: winget
      """;

  private static string FormatErrors(IEnumerable<StructuredError> errors) =>
      string.Join(Environment.NewLine, errors.Select(error => error.Detail));

  private sealed class StubProvider(
      string resourceType,
      string providerName,
      params string[] validationErrors) : IResourceProvider
  {
    public string ResourceType { get; } = resourceType;
    public string ProviderName { get; } = providerName;
    public ProviderCapabilities Capabilities { get; } = new();

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return ValueTask.FromResult(
          validationErrors.Length == 0
              ? ProviderValidationResult.Valid
              : ProviderValidationResult.Invalid(validationErrors));
    }

    public ValueTask<DetectedState> DetectAsync(ResourceDefinition resource, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public ValueTask<ResourcePlan> PlanAsync(ResourceDefinition resource, DetectedState currentState, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public ValueTask<ResourceApplyResult> ApplyAsync(ResourceDefinition resource, ResourcePlan plan, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public ValueTask<VerificationResult> VerifyAsync(ResourceDefinition resource, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
  }

  private sealed class TemporaryDirectory : IDisposable
  {
    public TemporaryDirectory()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wdem-profile-{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string fileName, string contents)
    {
      var path = System.IO.Path.Combine(Path, fileName);
      File.WriteAllText(path, contents);
      return path;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
  }
}
