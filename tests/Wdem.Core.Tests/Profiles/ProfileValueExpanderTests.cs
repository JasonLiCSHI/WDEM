using Wdem.Core.Execution;
using Wdem.Core.Profiles;
using Wdem.Core.Resources;
using Xunit;

namespace Wdem.Core.Tests.Profiles;

public sealed class ProfileValueExpanderTests
{
  [Fact]
  public void ExpandSelected_ExpandsRequiredSelectedAndDependencyClosureOnly()
  {
    var profile = CreateProfile();
    var values = new Dictionary<string, string>(StringComparer.Ordinal)
    {
      ["WDEM_GIT_TOKEN"] = "git-secret",
      ["WDEM_TOOL_PATH"] = "tool-path",
      ["WDEM_COMPANY_VSIX_PATH"] = "company-path"
    };

    var result = ProfileValueExpander.ExpandSelected(
        profile,
        ["tool"],
        name => values.GetValueOrDefault(name));

    Assert.True(result.IsValid);
    Assert.Equal("git-secret", result.Profile!.Resources["git"].Parameters["token"]);
    Assert.Equal("tool-path", result.Profile.Resources["tool"].Parameters["path"]);
    Assert.Equal(
        "${WDEM_COMPANY_VSIX_PATH}",
        result.Profile.Resources["company-vsix"].Parameters["path"]);
  }

  [Fact]
  public void ExpandSelected_UnresolvedSelectedValueReportsEscapedJsonPointer()
  {
    var profile = CreateProfile() with
    {
      Resources = new Dictionary<string, ResourceDefinition>(CreateProfile().Resources, StringComparer.OrdinalIgnoreCase)
      {
        ["tool"] = CreateProfile().Resources["tool"] with
        {
          Parameters = new Dictionary<string, string?> { ["path/name~value"] = "${WDEM_MISSING}" }
        }
      }
    };

    var result = ProfileValueExpander.ExpandSelected(
        profile,
        ["tool"],
        name => name == "WDEM_GIT_TOKEN" ? "git-secret" : null);

    Assert.False(result.IsValid);
    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.ProfileError, error.Code);
    Assert.Contains("/resources/tool/parameters/path~1name~0value", error.Detail, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("${HOME}")]
  [InlineData("${WDEM_lower}")]
  [InlineData("prefix-${WDEM_TOKEN}")]
  public void ExpandSelected_DoesNotTreatNonExactWdemTokensAsSecretInputs(string value)
  {
    var source = CreateProfile();
    var profile = source with
    {
      Resources = new Dictionary<string, ResourceDefinition>(source.Resources, StringComparer.OrdinalIgnoreCase)
      {
        ["tool"] = source.Resources["tool"] with
        {
          Parameters = new Dictionary<string, string?> { ["path"] = value }
        }
      }
    };

    var result = ProfileValueExpander.ExpandSelected(profile, ["tool"], _ => "secret");

    Assert.True(result.IsValid);
    Assert.Equal(value, result.Profile!.Resources["tool"].Parameters["path"]);
  }

  [Fact]
  public void ExpandSelected_HandlesThousandsDeepDependencyCycleIteratively()
  {
    const int resourceCount = 8_000;
    var resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < resourceCount; index++)
    {
      var id = $"resource-{index}";
      resources[id] = new ResourceDefinition
      {
        Id = id,
        Type = "package",
        Provider = "winget",
        Dependencies = [$"resource-{(index + 1) % resourceCount}"],
        Parameters = new Dictionary<string, string?> { ["value"] = "${WDEM_VALUE}" }
      };
    }

    var profile = new DeveloperProfile
    {
      Id = "deep",
      Version = "1.0.0",
      DisplayName = "Deep",
      Description = "Deep dependency cycle",
      RequiredResources = [new ProfileResourceReference { Id = "resource-0" }],
      Resources = resources
    };

    var result = ProfileValueExpander.ExpandSelected(profile, [], _ => "expanded");

    Assert.True(result.IsValid);
    Assert.All(result.Profile!.Resources.Values, resource =>
        Assert.Equal("expanded", resource.Parameters["value"]));
  }

  private static DeveloperProfile CreateProfile() => new()
  {
    Id = "developer",
    Version = "1.0.0",
    DisplayName = "Developer",
    Description = "Developer profile",
    RequiredResources = [new ProfileResourceReference { Id = "git" }],
    OptionalResources = [
      new ProfileResourceReference { Id = "tool" },
      new ProfileResourceReference { Id = "company-vsix" }
    ],
    Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = new()
      {
        Id = "git",
        Type = "package",
        Provider = "winget",
        Parameters = new Dictionary<string, string?> { ["token"] = "${WDEM_GIT_TOKEN}" }
      },
      ["tool"] = new()
      {
        Id = "tool",
        Type = "package",
        Provider = "winget",
        Dependencies = ["git"],
        Parameters = new Dictionary<string, string?> { ["path"] = "${WDEM_TOOL_PATH}" }
      },
      ["company-vsix"] = new()
      {
        Id = "company-vsix",
        Type = "extension",
        Provider = "file",
        Parameters = new Dictionary<string, string?> { ["path"] = "${WDEM_COMPANY_VSIX_PATH}" }
      }
    }
  };
}
