using Wdem.Windows.VisualStudio;
using Xunit;

namespace Wdem.Windows.Tests.VisualStudio;

public sealed class VsixManifestReaderTests
{
  public static TheoryData<string> InvalidManifests => new()
  {
    "<NotPackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\"><Metadata><Identity Id=\"Contoso.DeveloperTools\" Version=\"3.2.0\" /></Metadata><Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" /></Installation></NotPackageManifest>",
    "<PackageManifest xmlns=\"urn:not-vsix\"><Metadata><Identity Id=\"Contoso.DeveloperTools\" Version=\"3.2.0\" /></Metadata><Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" /></Installation></PackageManifest>",
    "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\"><Metadata><Wrapper><Identity Id=\"Contoso.DeveloperTools\" Version=\"3.2.0\" /></Wrapper></Metadata><Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" /></Installation></PackageManifest>",
    "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\"><Metadata><Identity Id=\"One\" Version=\"3.2.0\" /><Identity Id=\"Two\" Version=\"3.2.0\" /></Metadata><Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" /></Installation></PackageManifest>",
    "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\"><Metadata><Identity Id=\"Contoso.DeveloperTools\" Version=\"not-a-version\" /></Metadata><Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" /></Installation></PackageManifest>",
    "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\"><Metadata><Identity Id=\"Contoso.DeveloperTools\" Version=\"3.2.0\" /></Metadata></PackageManifest>",
    "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\" xmlns:evil=\"urn:evil\"><Metadata><Identity Id=\"Contoso.DeveloperTools\" evil:Id=\"Other\" Version=\"3.2.0\" /></Metadata><Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" /></Installation></PackageManifest>",
    "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\"><Metadata><Identity Id=\"Contoso.DeveloperTools\" Version=\"3.2.0\" /></Metadata><Installation><InstallationTarget Version=\"[17.0,18.0)\" /></Installation></PackageManifest>",
    "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\" xmlns:evil=\"urn:evil\"><Metadata><Identity Id=\"Contoso.DeveloperTools\" Version=\"3.2.0\" /></Metadata><Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" evil:Id=\"Other\" /></Installation></PackageManifest>",
    "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\"><Metadata><Identity Id=\"Contoso.DeveloperTools\" Version=\"3.2.0\" /></Metadata><Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" Version=\"not-a-range\" /></Installation></PackageManifest>",
    "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\"><Metadata><Identity Id=\"Contoso.DeveloperTools\" Version=\"3.2.0\" /></Metadata><Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" Version=\"[18.0,17.0)\" /></Installation></PackageManifest>"
  };

  [Fact]
  public async Task ReadInstalledAsync_UsesSelectedProfileDirectoryAndStableManifestIdentity()
  {
    var root = Path.Combine(Path.GetTempPath(), $"wdem-vsix-reader-{Guid.NewGuid():N}");
    var selectedPath = Path.Combine(
        root,
        "Microsoft",
        "VisualStudio",
        "17.0_a",
        "Extensions",
        "random-folder",
        "extension.vsixmanifest");
    var wrongInstancePath = Path.Combine(
        root,
        "Microsoft",
        "VisualStudio",
        "17.0_b",
        "Extensions",
        "same-extension",
        "extension.vsixmanifest");
    Directory.CreateDirectory(Path.GetDirectoryName(selectedPath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(wrongInstancePath)!);
    await File.WriteAllTextAsync(selectedPath, ValidManifest("3.2.0"));
    await File.WriteAllTextAsync(wrongInstancePath, ValidManifest("9.0.0"));
    try
    {
      var reader = new VsixManifestReader(root);

      var manifests = await reader.ReadInstalledAsync(Instance("a"), CancellationToken.None);

      var manifest = Assert.Single(manifests);
      Assert.Equal("Contoso.DeveloperTools", manifest.Id);
      Assert.Equal("3.2.0", manifest.Version);
      Assert.Equal("a", manifest.VisualStudioInstanceId);
      Assert.Equal(Path.GetFullPath(selectedPath), manifest.ManifestPath);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void InstallationTargetCompatibility_MapsProfessionalProductToProTarget()
  {
    var instance = Instance("a") with
    {
      ProductId = "Microsoft.VisualStudio.Product.Professional",
      Edition = "Professional"
    };

    var compatible = VsixInstallationTargetCompatibility.IsCompatible(
        [new VsixInstallationTarget("Microsoft.VisualStudio.Pro", "[17.0,18.0)")],
        instance);

    Assert.True(compatible);
  }

  [Theory]
  [MemberData(nameof(InvalidManifests))]
  public async Task ReadSourceAsync_RejectsUnsupportedOrAmbiguousManifestStructure(string xml)
  {
    var path = Path.Combine(Path.GetTempPath(), $"wdem-invalid-{Guid.NewGuid():N}.vsixmanifest");
    await File.WriteAllTextAsync(path, xml);
    try
    {
      var result = await new VsixManifestReader()
          .ReadSourceAsync(path, "a", CancellationToken.None);

      Assert.Null(result.Manifest);
      Assert.NotNull(result.Error);
    }
    finally
    {
      File.Delete(path);
    }
  }

  private static string ValidManifest(string version) =>
      "<PackageManifest xmlns=\"http://schemas.microsoft.com/developer/vsx-schema/2011\" Version=\"2.0.0\">" +
      $"<Metadata><Identity Id=\"Contoso.DeveloperTools\" Version=\"{version}\" /></Metadata>" +
      "<Installation><InstallationTarget Id=\"Microsoft.VisualStudio.Community\" Version=\"[17.0,18.0)\" />" +
      "</Installation></PackageManifest>";

  private static VisualStudioInstance Instance(string id) => new()
  {
    InstanceId = id,
    InstallationPath = Path.Combine(Path.GetTempPath(), "missing-vs"),
    ProductId = "Microsoft.VisualStudio.Product.Community",
    ProductPath = Path.Combine(Path.GetTempPath(), "missing-vs", "Common7", "IDE", "devenv.exe"),
    ProductDisplayVersion = "17.9",
    InstallationVersion = "17.9.0",
    ChannelId = "VisualStudio.17.Release",
    Edition = "Community",
    IsComplete = true,
    IsLaunchable = true
  };
}
