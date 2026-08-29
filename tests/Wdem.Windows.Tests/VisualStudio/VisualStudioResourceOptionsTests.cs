using Wdem.Core.Resources;
using Wdem.Windows.VisualStudio;
using Xunit;

namespace Wdem.Windows.Tests.VisualStudio;

public sealed class VisualStudioResourceOptionsTests
{
  public static TheoryData<string, string?, string> InvalidParameters => new()
  {
    { "productId", null, "productId" },
    { "edition", " ", "edition" },
    { "channelId", null, "channelId" },
    { "instanceId", " ", "instanceId" },
    { "installPath", @"relative\VS", "installPath" },
    { "workloads", " ", "workloads" },
    { "components", "component-a,,component-b", "components" },
    { "vsconfigPath", "developer.vsconfig", "vsconfigPath" },
    { "bootstrapperUri", "http://example.test/vs.exe", "bootstrapperUri" },
    { "bootstrapperSha256", "not-a-sha256", "bootstrapperSha256" },
    { "unexpected", "value", "unexpected" }
  };

  [Fact]
  public void TryParse_CompleteParameters_ReturnsNormalizedOptions()
  {
    var resource = new ResourceDefinition
    {
      Id = "visual-studio",
      Type = "visual-studio",
      Provider = "visual-studio",
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["productId"] = "Microsoft.VisualStudio.Product.Community",
        ["instanceId"] = "17.0_abc",
        ["edition"] = "Community",
        ["channelId"] = "VisualStudio.18.Release",
        ["installPath"] = @"C:\VS",
        ["workloads"] = "Microsoft.VisualStudio.Workload.ManagedDesktop, Microsoft.VisualStudio.Workload.NetWeb",
        ["components"] = "Microsoft.NetCore.Component.Runtime.10.0;Microsoft.VisualStudio.Component.Git",
        ["vsconfigPath"] = @"C:\Profiles\developer.vsconfig",
        ["bootstrapperUri"] = "https://example.test/vs_community.exe",
        ["bootstrapperSha256"] = new string('A', 64)
      }
    };

    var parsed = VisualStudioResourceOptions.TryParse(resource, out var options, out var errors);

    Assert.True(parsed);
    Assert.Empty(errors);
    Assert.NotNull(options);
    Assert.Equal("Microsoft.VisualStudio.Product.Community", options.ProductId);
    Assert.Equal("17.0_abc", options.InstanceId);
    Assert.Equal("Community", options.Edition);
    Assert.Equal("VisualStudio.18.Release", options.ChannelId);
    Assert.Equal(@"C:\VS", options.InstallPath);
    Assert.Equal(
        [
          "Microsoft.VisualStudio.Workload.ManagedDesktop",
          "Microsoft.VisualStudio.Workload.NetWeb"
        ],
        options.Workloads);
    Assert.Equal(
        [
          "Microsoft.NetCore.Component.Runtime.10.0",
          "Microsoft.VisualStudio.Component.Git"
        ],
        options.Components);
    Assert.Equal(@"C:\Profiles\developer.vsconfig", options.VsConfigPath);
    Assert.Equal(new Uri("https://example.test/vs_community.exe"), options.BootstrapperUri);
    Assert.Equal(new string('A', 64), options.BootstrapperSha256);
  }

  [Theory]
  [MemberData(nameof(InvalidParameters))]
  public void TryParse_InvalidParameter_ReturnsActionableError(
      string parameter,
      string? value,
      string expectedError)
  {
    var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
      ["productId"] = "Microsoft.VisualStudio.Product.Community",
      ["edition"] = "Community",
      ["channelId"] = "VisualStudio.18.Release",
      ["workloads"] = "Microsoft.VisualStudio.Workload.ManagedDesktop",
      ["components"] = "Microsoft.VisualStudio.Component.Git"
    };
    parameters[parameter] = value;
    var resource = new ResourceDefinition
    {
      Id = "visual-studio",
      Type = "visual-studio",
      Provider = "visual-studio",
      Parameters = parameters
    };

    var parsed = VisualStudioResourceOptions.TryParse(resource, out var options, out var errors);

    Assert.False(parsed);
    Assert.Null(options);
    Assert.Contains(errors, error => error.Contains(expectedError, StringComparison.Ordinal));
  }

  [Fact]
  public void TryParse_MissingWorkloads_ReturnsRequiredParameterError()
  {
    var resource = new ResourceDefinition
    {
      Id = "visual-studio",
      Type = "visual-studio",
      Provider = "visual-studio",
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["productId"] = "Microsoft.VisualStudio.Product.Community",
        ["edition"] = "Community",
        ["channelId"] = "VisualStudio.18.Release",
        ["components"] = "Microsoft.VisualStudio.Component.Git"
      }
    };

    var parsed = VisualStudioResourceOptions.TryParse(resource, out var options, out var errors);

    Assert.False(parsed);
    Assert.Null(options);
    Assert.Contains(errors, error => error.Contains("workloads", StringComparison.Ordinal));
  }

  [Fact]
  public void TryParse_MissingComponents_ReturnsRequiredParameterError()
  {
    var resource = new ResourceDefinition
    {
      Id = "visual-studio",
      Type = "visual-studio",
      Provider = "visual-studio",
      Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
      {
        ["productId"] = "Microsoft.VisualStudio.Product.Community",
        ["edition"] = "Community",
        ["channelId"] = "VisualStudio.18.Release",
        ["workloads"] = "Microsoft.VisualStudio.Workload.ManagedDesktop"
      }
    };

    var parsed = VisualStudioResourceOptions.TryParse(resource, out var options, out var errors);

    Assert.False(parsed);
    Assert.Null(options);
    Assert.Contains(errors, error => error.Contains("components", StringComparison.Ordinal));
  }
}
