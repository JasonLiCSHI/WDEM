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
  public async Task LoadAsync_ShippedCSharpProfile_HasTheCompleteMvpResourceSet()
  {
    var result = await CreateProductionCatalog().LoadAsync("csharp-developer", CancellationToken.None);

    Assert.True(result.IsValid, FormatErrors(result.Errors));
    Assert.Equal(
        ["visual-studio", "dotnet-sdk", "git"],
        result.Profile!.RequiredResources.Select(resource => resource.Id));
    Assert.Equal(
        ["resharper", "resharper-settings", "company-vs-extension", "visual-studio-settings"],
        result.Profile.OptionalResources.Select(resource => resource.Id));
    Assert.Equal(["visual-studio"], result.Profile.Resources["resharper"].Dependencies);
    Assert.Equal(["resharper"], result.Profile.Resources["resharper-settings"].Dependencies);
    Assert.Equal(["visual-studio"], result.Profile.Resources["company-vs-extension"].Dependencies);
    Assert.Equal(["visual-studio"], result.Profile.Resources["visual-studio-settings"].Dependencies);
    Assert.Equal(7, result.Profile.Resources.Count);
    Assert.All(result.Profile.Resources.Values, resource =>
    {
      Assert.False(string.IsNullOrWhiteSpace(resource.DisplayName));
      Assert.False(string.IsNullOrWhiteSpace(resource.Description));
    });
  }

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
    Assert.Equal("Git", result.Profile.Resources["git"].DisplayName);
    Assert.Equal("Distributed version control", result.Profile.Resources["git"].Description);
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
    Assert.Equal("Git", result.Profile.Resources["git"].DisplayName);
    Assert.Equal("Distributed version control", result.Profile.Resources["git"].Description);
  }

  [Theory]
  [InlineData("", "/resources/git/displayName")]
  [InlineData("\"displayName\": \"Git\",", "/resources/git/description")]
  [InlineData("\"displayName\": \"   \", \"description\": \"Git client\",", "/resources/git/displayName")]
  [InlineData("\"displayName\": \"Git\", \"description\": \"\\t\",", "/resources/git/description")]
  public async Task LoadFileAsync_RequiresNonWhitespaceResourcePresentationFields(
      string presentationFields,
      string pointer)
  {
    using var directory = new TemporaryDirectory();
    var contents = $$"""
        {
          "schemaVersion": "1.0",
          "profile": {
            "id": "presentation",
            "version": "1.0.0",
            "displayName": "Presentation",
            "description": "Profile description",
            "requiredResources": [{ "id": "git" }]
          },
          "resources": {
            "git": { {{presentationFields}} "type": "package", "provider": "winget" }
          }
        }
        """;

    var result = await CreateCatalog(directory.Path)
        .LoadFileAsync(directory.Write("presentation.json", contents));

    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, error => error.Detail.Contains(pointer, StringComparison.Ordinal));
  }

  [Fact]
  public async Task LoadFileAsync_RejectsWhitespaceProfileDescriptionAtExactPointer()
  {
    using var directory = new TemporaryDirectory();
    var contents = """
        {
          "schemaVersion": "1.0",
          "profile": {
            "id": "presentation",
            "version": "1.0.0",
            "displayName": "Presentation",
            "description": "   "
          },
          "resources": {}
        }
        """;

    var result = await CreateCatalog(directory.Path)
        .LoadFileAsync(directory.Write("profile-description.json", contents));

    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, error =>
        error.Detail.Contains("/profile/description", StringComparison.Ordinal));
  }

  [Fact]
  public async Task LoadFileAsync_PreservesYamlScalarTypes()
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
          git:
            displayName: Git
            description: Git resource
            type: package
            provider: winget
          tool:
            displayName: Tool
            description: Optional tool
            type: package
            provider: winget
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

  [Fact]
  public async Task LoadFileAsync_JsonRejectsWhitespaceOnlyIdentifiersWithPointers()
  {
    using var directory = new TemporaryDirectory();
    var contents = """
        {
          "schemaVersion": "1.0",
          "profile": {
            "id": "   ",
            "version": "1.0.0",
            "displayName": "Whitespace",
            "description": "Whitespace identifiers",
            "optionalResources": [{ "id": "\t" }]
          },
          "resources": {
            "   ": { "type": "\n", "provider": "\u00a0" }
          }
        }
        """;

    var result = await CreateCatalog(directory.Path)
        .LoadFileAsync(directory.Write("whitespace.json", contents));

    Assert.False(result.IsValid);
    Assert.All(result.Errors, error => Assert.Equal(WdemErrorCode.ProfileError, error.Code));
    Assert.Contains(result.Errors, error => error.Detail.Contains("/profile/id", StringComparison.Ordinal));
    Assert.Contains(result.Errors, error => error.Detail.Contains("/profile/optionalResources/0/id", StringComparison.Ordinal));
    Assert.Contains(result.Errors, error => error.Detail.Contains("/resources/   ", StringComparison.Ordinal));
  }

  [Fact]
  public async Task LoadFileAsync_RejectsYamlAnchorsAndAliasesInBoundedTime()
  {
    using var directory = new TemporaryDirectory();
    var bomb = """
        a: &a ["lol","lol","lol","lol","lol","lol","lol","lol","lol"]
        b: &b [*a,*a,*a,*a,*a,*a,*a,*a,*a]
        c: &c [*b,*b,*b,*b,*b,*b,*b,*b,*b]
        d: [*c,*c,*c,*c,*c,*c,*c,*c,*c]
        """.PadRight(216, ' ');
    var task = CreateCatalog(directory.Path)
        .LoadFileAsync(directory.Write("alias-bomb.yaml", bomb));

    var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));

    Assert.Same(task, completed);
    var result = await task;
    var error = Assert.Single(result.Errors);
    Assert.Contains("alias-bomb.yaml", error.Detail, StringComparison.Ordinal);
    Assert.True(
        error.Detail.Contains("anchor", StringComparison.OrdinalIgnoreCase) ||
        error.Detail.Contains("alias", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public async Task LoadFileAsync_RejectsInputLargerThanOneMiB()
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write("oversized.json", new string(' ', (1024 * 1024) + 1));

    var result = await CreateCatalog(directory.Path).LoadFileAsync(path);

    Assert.False(result.IsValid);
    Assert.Contains("size", Assert.Single(result.Errors).Summary, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task LoadFileAsync_RejectsJsonAstAboveNodeQuota()
  {
    using var directory = new TemporaryDirectory();
    var values = string.Join(',', Enumerable.Repeat("0", 100_001));

    var result = await CreateCatalog(directory.Path)
        .LoadFileAsync(directory.Write("too-many-nodes.json", $"[{values}]"));

    Assert.False(result.IsValid);
    Assert.Contains("complex", Assert.Single(result.Errors).Summary, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task LoadFileAsync_CancellationWinsOverInvalidSchemaEarlyReturn()
  {
    using var directory = new TemporaryDirectory();
    using var cancellation = new CancellationTokenSource();
    var padding = string.Join(',', Enumerable.Repeat("0", 80_000));
    var path = directory.Write("cancel-schema.json", $$"""
        {
          "schemaVersion": "1.0",
          "profile": { "id": "cancel", "version": "1.0.0", "displayName": "Cancel", "description": "Cancel" },
          "resources": {},
          "invalidPadding": [{{padding}}]
        }
        """);

    cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        CreateCatalog(directory.Path).LoadFileAsync(path, cancellation.Token));
  }

  [Theory]
  [InlineData(
      "duplicate-profile-id.json",
      "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"first\",\"id\":\"second\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\"},\"resources\":{}}",
      "/profile/id")]
  [InlineData(
      "duplicate-provider.json",
      "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"git\"}]},\"resources\":{\"git\":{\"type\":\"package\",\"provider\":\"winget\",\"provider\":\"winget\"}}}",
      "/resources/git/provider")]
  [InlineData(
      "duplicate-parameter.json",
      "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"git\"}]},\"resources\":{\"git\":{\"type\":\"package\",\"provider\":\"winget\",\"parameters\":{\"path\":\"one\",\"path\":\"two\"}}}}",
      "/resources/git/parameters/path")]
  public async Task LoadFileAsync_RejectsExactJsonDuplicateProperties(
      string fileName,
      string contents,
      string pointer)
  {
    using var directory = new TemporaryDirectory();

    var result = await CreateCatalog(directory.Path)
        .LoadFileAsync(directory.Write(fileName, contents));

    var error = Assert.Single(result.Errors, item => item.Detail.Contains(pointer, StringComparison.Ordinal));
    Assert.Contains("duplicate", error.Summary, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task LoadFileAsync_RejectsExactYamlDuplicatePropertiesWithPointer()
  {
    using var directory = new TemporaryDirectory();
    var contents = """
        schemaVersion: "1.0"
        profile:
          id: duplicate-yaml
          version: "1.0.0"
          displayName: Duplicate YAML
          description: Duplicate YAML property
          requiredResources:
            - id: git
        resources:
          git:
            type: package
            provider: winget
            provider: winget
        """;

    var result = await CreateCatalog(directory.Path)
        .LoadFileAsync(directory.Write("duplicate.yaml", contents));

    var error = Assert.Single(result.Errors, item =>
        item.Detail.Contains("/resources/git/provider", StringComparison.Ordinal));
    Assert.Contains("duplicate", error.Summary, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task LoadFileAsync_YamlRejectsWhitespaceOnlyReferenceTypeAndProviderWithPointers()
  {
    using var directory = new TemporaryDirectory();
    var contents = """
        schemaVersion: "1.0"
        profile:
          id: whitespace-yaml
          version: "1.0.0"
          displayName: Whitespace
          description: Whitespace values
          requiredResources:
            - id: "  "
        resources:
          git:
            type: "  "
            provider: "  "
        """;

    var result = await CreateCatalog(directory.Path)
        .LoadFileAsync(directory.Write("whitespace.yaml", contents));

    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, error => error.Detail.Contains("/profile/requiredResources/0/id", StringComparison.Ordinal));
    Assert.Contains(result.Errors, error => error.Detail.Contains("/resources/git/type", StringComparison.Ordinal));
    Assert.Contains(result.Errors, error => error.Detail.Contains("/resources/git/provider", StringComparison.Ordinal));
  }

  [Fact]
  public async Task LoadFileAsync_ReportsCaseInsensitiveParameterKeyConflictAtConflictingKey()
  {
    using var directory = new TemporaryDirectory();
    var contents = """
        {
          "schemaVersion": "1.0",
          "profile": {
            "id": "duplicate-parameters",
            "version": "1.0.0",
            "displayName": "Duplicate parameters",
            "description": "Duplicate parameter keys",
            "requiredResources": [{ "id": "git/tools~beta" }]
          },
          "resources": {
            "git/tools~beta": {
              "type": "package",
              "provider": "winget",
              "parameters": { "Path": "one", "path": "two", "a/b~c": "three", "A/B~C": "four" }
            }
          }
        }
        """;

    var result = await CreateCatalog(directory.Path)
        .LoadFileAsync(directory.Write("duplicate-parameters.json", contents));

    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, error =>
        error.Detail.Contains("/resources/git~1tools~0beta/parameters/path", StringComparison.Ordinal));
    Assert.Contains(result.Errors, error =>
        error.Detail.Contains("/resources/git~1tools~0beta/parameters/A~1B~0C", StringComparison.Ordinal));
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
  [InlineData("duplicate-ref.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"git\"},{\"id\":\"GIT\"}]},\"resources\":{\"git\":{\"displayName\":\"Git\",\"description\":\"Git resource\",\"type\":\"package\",\"provider\":\"winget\"}}}", "/profile/requiredResources/1/id")]
  [InlineData("unknown-ref.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"missing\"}]},\"resources\":{}}", "/profile/requiredResources/0/id")]
  [InlineData("unknown-dependency.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"git\"}]},\"resources\":{\"git\":{\"displayName\":\"Git\",\"description\":\"Git resource\",\"type\":\"package\",\"provider\":\"winget\",\"dependsOn\":[\"missing\"]}}}", "/resources/git/dependsOn/0")]
  [InlineData("bad-constraint.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"git\"}]},\"resources\":{\"git\":{\"displayName\":\"Git\",\"description\":\"Git resource\",\"type\":\"package\",\"provider\":\"winget\",\"versionConstraint\":\"latest\"}}}", "/resources/git/versionConstraint")]
  [InlineData("bad-preferred.json", "{\"schemaVersion\":\"1.0\",\"profile\":{\"id\":\"p\",\"version\":\"1.0.0\",\"displayName\":\"P\",\"description\":\"D\",\"requiredResources\":[{\"id\":\"git\"}]},\"resources\":{\"git\":{\"displayName\":\"Git\",\"description\":\"Git resource\",\"type\":\"package\",\"provider\":\"winget\",\"preferredVersion\":\"vNext\"}}}", "/resources/git/preferredVersion")]
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
  public async Task LoadFileAsync_StructuredProviderValidationFailure_IsPreserved()
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write("provider-structured-rejection.json", ValidSingleResourceJson("git"));
    var providerError = new StructuredError(
        WdemErrorCode.ProviderError,
        "Provider rejected the resource.",
        "The requested source is not trusted.")
    {
      ResourceId = "git",
      SuggestedAction = "Choose a trusted source."
    };
    var registry = new ResourceProviderRegistry([
      new DelegateProvider((_, _) => ValueTask.FromResult(
          ProviderValidationResult.Invalid(providerError)))
    ]);

    var result = await new DirectoryProfileCatalog(directory.Path, registry).LoadFileAsync(path);

    Assert.False(result.IsValid);
    Assert.Same(providerError, Assert.Single(result.Errors));
  }

  [Fact]
  public async Task LoadFileAsync_BothProviderDiagnosticForms_PrefersStructuredErrors()
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write("provider-inconsistent-rejection.json", ValidSingleResourceJson("git"));
    var providerError = new StructuredError(
        WdemErrorCode.ProviderError,
        "Structured rejection.",
        "Structured detail.");
    var registry = new ResourceProviderRegistry([
      new DelegateProvider((_, _) => ValueTask.FromResult(new ProviderValidationResult
      {
        Errors = ["legacy duplicate"],
        StructuredErrors = [providerError]
      }))
    ]);

    var result = await new DirectoryProfileCatalog(directory.Path, registry).LoadFileAsync(path);

    Assert.Same(providerError, Assert.Single(result.Errors));
    Assert.DoesNotContain(result.Errors, error =>
        error.Detail.Contains("legacy duplicate", StringComparison.Ordinal));
  }

  [Fact]
  public async Task LoadFileAsync_SanitizesProviderValidationDiagnostics()
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write("provider-secret.json", ValidSingleResourceJson("provider-secret"));
    var registry = new ResourceProviderRegistry([
      new StubProvider(
          "package",
          "winget",
          @"token=raw-secret Bearer bearer-secret C:\Users\Alice\profile.yaml")
    ]);

    var result = await new DirectoryProfileCatalog(directory.Path, registry).LoadFileAsync(path);

    var error = Assert.Single(result.Errors);
    Assert.DoesNotContain("raw-secret", error.Detail, StringComparison.Ordinal);
    Assert.DoesNotContain("bearer-secret", error.Detail, StringComparison.Ordinal);
    Assert.DoesNotContain("Alice", error.Detail, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task LoadFileAsync_ProviderContractViolationsReturnProfileError(bool nullResult)
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write("provider-contract.json", ValidSingleResourceJson("provider-contract"));
    var registry = new ResourceProviderRegistry([
      new DelegateProvider((_, _) => ValueTask.FromResult(
          nullResult
              ? null!
              : new ProviderValidationResult { Errors = null! }))
    ]);

    var result = await new DirectoryProfileCatalog(directory.Path, registry).LoadFileAsync(path);

    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.ProfileError, error.Code);
    Assert.Contains("contract", error.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("/resources/git", error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LoadFileAsync_NullProviderValidationErrorReturnsProfileError()
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write("provider-null-error.json", ValidSingleResourceJson("provider-null-error"));
    var registry = new ResourceProviderRegistry([
      new DelegateProvider((_, _) => ValueTask.FromResult(
          new ProviderValidationResult { Errors = new string[] { null! } }))
    ]);

    var result = await new DirectoryProfileCatalog(directory.Path, registry).LoadFileAsync(path);

    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.ProfileError, error.Code);
    Assert.Contains("contract", error.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("/resources/git", error.Detail, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task LoadFileAsync_NullStructuredProviderDiagnosticsReturnContractError(
      bool nullCollection)
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write("provider-null-structured-error.json", ValidSingleResourceJson("git"));
    var registry = new ResourceProviderRegistry([
      new DelegateProvider((_, _) => ValueTask.FromResult(new ProviderValidationResult
      {
        StructuredErrors = nullCollection ? null! : new StructuredError[] { null! }
      }))
    ]);

    var result = await new DirectoryProfileCatalog(directory.Path, registry).LoadFileAsync(path);

    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.ProfileError, error.Code);
    Assert.Contains("contract", error.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("/resources/git", error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LoadFileAsync_ReadsEachProviderValidationErrorOnce()
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write("provider-single-read.json", ValidSingleResourceJson("provider-single-read"));
    var errors = new SingleReadErrorList("first rejection", "second rejection");
    var registry = new ResourceProviderRegistry([
      new DelegateProvider((_, _) => ValueTask.FromResult(
          new ProviderValidationResult { Errors = errors }))
    ]);

    var result = await new DirectoryProfileCatalog(directory.Path, registry).LoadFileAsync(path);

    Assert.Equal(2, result.Errors.Count);
    Assert.Contains(result.Errors, error => error.Detail.Contains("first rejection", StringComparison.Ordinal));
    Assert.Contains(result.Errors, error => error.Detail.Contains("second rejection", StringComparison.Ordinal));
  }

  [Fact]
  public async Task LoadFileAsync_SpuriousProviderCancellationReturnsStructuredError()
  {
    using var directory = new TemporaryDirectory();
    var path = directory.Write("spurious-cancellation.json", ValidSingleResourceJson("spurious-cancellation"));
    var registry = new ResourceProviderRegistry([
      new DelegateProvider((_, _) => throw new OperationCanceledException("provider cancelled itself"))
    ]);

    var result = await new DirectoryProfileCatalog(directory.Path, registry).LoadFileAsync(path);

    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.ProfileError, error.Code);
    Assert.Contains("/resources/git", error.Detail, StringComparison.Ordinal);
    Assert.Contains("provider cancelled itself", error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LoadFileAsync_ThrowsWhenCallerCancelsDuringProviderValidation()
  {
    using var directory = new TemporaryDirectory();
    using var cancellation = new CancellationTokenSource();
    var path = directory.Write("caller-cancellation.json", ValidSingleResourceJson("caller-cancellation"));
    var registry = new ResourceProviderRegistry([
      new DelegateProvider((_, _) =>
      {
        cancellation.Cancel();
        return ValueTask.FromResult(ProviderValidationResult.Valid);
      })
    ]);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        new DirectoryProfileCatalog(directory.Path, registry)
            .LoadFileAsync(path, cancellation.Token));
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
            "git/tools~beta": {
              "displayName": "Git tools",
              "description": "Git tool bundle",
              "type": "package",
              "provider": "missing"
            }
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

  [Fact]
  public async Task LoadFileAsync_SanitizesIoExceptionDiagnostics()
  {
    using var directory = new TemporaryDirectory();
    var path = Path.Combine(directory.Path, "token=raw-secret.yaml");

    var result = await CreateCatalog(directory.Path).LoadFileAsync(path);

    var error = Assert.Single(result.Errors);
    Assert.DoesNotContain("raw-secret", error.Detail, StringComparison.Ordinal);
    Assert.NotNull(error.UnderlyingException);
    Assert.DoesNotContain("raw-secret", error.UnderlyingExceptionMessage, StringComparison.Ordinal);
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

  [Theory]
  [InlineData("contains space")]
  [InlineData("éclair")]
  [InlineData(".hidden")]
  [InlineData("name:stream")]
  public async Task LoadAsync_RejectsIdsOutsideCrossPlatformAllowlist(string id)
  {
    using var directory = new TemporaryDirectory();

    var result = await CreateCatalog(directory.Path).LoadAsync(id);

    var error = Assert.Single(result.Errors);
    Assert.Contains("not safe", error.Summary, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("/profile/id", error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task LoadAsync_RejectsSymlinkThatEscapesProfileRootWhileLoadFileRemainsExplicit()
  {
    using var directory = new TemporaryDirectory();
    using var outside = new TemporaryDirectory();
    var target = outside.Write("outside.yaml", ValidSingleResourceYaml("outside"));
    var link = Path.Combine(directory.Path, "linked.yaml");
    try
    {
      File.CreateSymbolicLink(link, target);
    }
    catch (Exception exception) when (
        exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
    {
      return;
    }

    var catalog = CreateCatalog(directory.Path);
    var discovered = await catalog.LoadAsync("linked");
    var discoveredAll = Assert.Single(await catalog.LoadAllAsync());
    var explicitLoad = await catalog.LoadFileAsync(target);

    Assert.False(discovered.IsValid);
    Assert.Contains("profile root", Assert.Single(discovered.Errors).Detail, StringComparison.OrdinalIgnoreCase);
    Assert.False(discoveredAll.IsValid);
    Assert.Contains("profile root", Assert.Single(discoveredAll.Errors).Detail, StringComparison.OrdinalIgnoreCase);
    Assert.True(explicitLoad.IsValid, FormatErrors(explicitLoad.Errors));
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
    Assert.Contains("JasonLiCSHI/WDEM", schema, StringComparison.Ordinal);
    Assert.DoesNotContain("DotDev262/WinHome", schema, StringComparison.Ordinal);
  }

  private static string TestDataDirectory =>
      Path.Combine(AppContext.BaseDirectory, "TestData", "Profiles");

  private static DirectoryProfileCatalog CreateCatalog(string directory) =>
      new(directory, new ResourceProviderRegistry([
        new StubProvider("package", "winget"),
        new StubProvider("extension", "file")
      ]));

  private static DirectoryProfileCatalog CreateProductionCatalog() =>
      new(
          Path.Combine(FindRepositoryRoot(), "profiles"),
          new ResourceProviderRegistry([
            new StubProvider("visual-studio", "visual-studio"),
            new StubProvider("dotnet-sdk", "winget"),
            new StubProvider("git", "winget"),
            new StubProvider("resharper", "winget"),
            new StubProvider("resharper-settings", "file"),
            new StubProvider("visual-studio-extension", "vsix"),
            new StubProvider("visual-studio-settings", "visual-studio-settings")
          ]));

  private static string FindRepositoryRoot()
  {
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
        directory is not null;
        directory = directory.Parent)
    {
      if (File.Exists(Path.Combine(directory.FullName, "Wdem.sln")))
      {
        return directory.FullName;
      }
    }

    throw new DirectoryNotFoundException("Could not locate the repository root.");
  }

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
        "resources": {
          "git": {
            "displayName": "Git",
            "description": "Git resource",
            "type": "package",
            "provider": "winget"
          }
        }
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
          displayName: Git
          description: Git resource
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

  private sealed class DelegateProvider(
      Func<ResourceDefinition, CancellationToken, ValueTask<ProviderValidationResult>> validate) : IResourceProvider
  {
    public string ResourceType => "package";
    public string ProviderName => "winget";
    public ProviderCapabilities Capabilities { get; } = new();

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => validate(resource, cancellationToken);

    public ValueTask<DetectedState> DetectAsync(ResourceDefinition resource, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public ValueTask<ResourcePlan> PlanAsync(ResourceDefinition resource, DetectedState currentState, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public ValueTask<ResourceApplyResult> ApplyAsync(ResourceDefinition resource, ResourcePlan plan, IProgress<ProviderProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public ValueTask<VerificationResult> VerifyAsync(ResourceDefinition resource, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
  }

  private sealed class SingleReadErrorList(params string[] errors) : IReadOnlyList<string>
  {
    private readonly bool[] _wasRead = new bool[errors.Length];

    public int Count => errors.Length;

    public string this[int index]
    {
      get
      {
        Assert.False(_wasRead[index], $"Provider error at index {index} was read more than once.");
        _wasRead[index] = true;
        return errors[index];
      }
    }

    public IEnumerator<string> GetEnumerator()
    {
      for (var index = 0; index < Count; index++)
      {
        yield return this[index];
      }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
