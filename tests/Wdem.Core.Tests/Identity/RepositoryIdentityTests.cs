using System.Xml.Linq;
using Xunit;

namespace Wdem.Core.Tests.Identity;

public sealed class RepositoryIdentityTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void UsesWdemSolutionInsteadOfWinHomeSolution()
    {
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "Wdem.sln")));
        Assert.False(File.Exists(Path.Combine(RepositoryRoot, "WinHome.sln")));
    }

    [Fact]
    public void IncludesThirdPartyNotices()
    {
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "THIRD-PARTY-NOTICES.md")));
    }

    [Fact]
    public void ProvenanceDocumentsDefineTheStandaloneRepositoryBoundary()
    {
        var notices = NormalizeWhitespace(File.ReadAllText(Path.Combine(RepositoryRoot, "THIRD-PARTY-NOTICES.md")));
        var provenance = NormalizeWhitespace(File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "wdem", "source-provenance.md")));

        Assert.Contains("not a branch, pull request, or merge target of either WinHome repository", notices);
        Assert.Contains("fetch-only", provenance);
    }

    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void ProjectIdentitiesDoNotUseWinHome()
    {
        foreach (var project in Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(project);
            var assemblyName = document.Descendants("AssemblyName").Select(element => element.Value);
            var rootNamespace = document.Descendants("RootNamespace").Select(element => element.Value);

            Assert.DoesNotContain("WinHome", assemblyName);
            Assert.DoesNotContain("WinHome", rootNamespace);
        }
    }

    [Fact]
    public void SourceNamespacesDoNotUseWinHome()
    {
        var sourceDirectory = Path.Combine(RepositoryRoot, "src");

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(sourceFile);
            Assert.DoesNotMatch(@"(?m)^\s*namespace\s+WinHome(?:\.|;)", source);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wdem.sln")) ||
                File.Exists(Path.Combine(directory.FullName, "WinHome.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
