using Wdem.Core.Resources;
using Wdem.Core.Providers;
using Xunit;

namespace Wdem.Core.Tests;

public sealed class ResourceDefinitionTests
{
  [Fact]
  public void Defaults_AreBehaviorSafe()
  {
    var resource = new ResourceDefinition
    {
      Id = "git",
      Type = "package",
      Provider = "winget"
    };

    Assert.Empty(resource.Dependencies);
    Assert.Empty(resource.Parameters);
    Assert.Equal(PrivilegeRequirement.CurrentUser, resource.PrivilegeRequirement);
    Assert.Equal(RestartPolicy.NoRestart, resource.RestartPolicy);
  }

  [Fact]
  public void ProviderRegistry_ResolvesByResourceTypeAndProviderName()
  {
    var winget = new StubProvider("package", "winget");
    var registry = new ResourceProviderRegistry([winget]);

    var resolved = registry.GetRequired("PACKAGE", "WINGET");

    Assert.Same(winget, resolved);
  }

  [Fact]
  public void ProviderRegistry_RejectsDuplicateProviderKeys()
  {
    var providers = new[]
    {
      new StubProvider("package", "winget"),
      new StubProvider("PACKAGE", "WINGET")
    };

    Assert.Throws<InvalidOperationException>(() => new ResourceProviderRegistry(providers));
  }

  [Fact]
  public void Fingerprint_IsStableAcrossDictionaryInsertionOrder()
  {
    var first = new ResourceDefinition
    {
      Id = "git",
      Type = "package",
      Provider = "winget",
      Parameters = new Dictionary<string, string?>
      {
        ["packageId"] = "Git.Git",
        ["source"] = "winget"
      }
    };
    var second = first with
    {
      Parameters = new Dictionary<string, string?>
      {
        ["source"] = "winget",
        ["packageId"] = "Git.Git"
      }
    };

    Assert.Equal(
        ResourceDefinitionFingerprint.Create(first),
        ResourceDefinitionFingerprint.Create(second));
  }

  private sealed class StubProvider(string resourceType, string providerName) : IResourceProvider
  {
    public string ResourceType { get; } = resourceType;
    public string ProviderName { get; } = providerName;
    public ProviderCapabilities Capabilities { get; } = new();

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
  }
}
