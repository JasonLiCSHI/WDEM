using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Xunit;

namespace Wdem.Core.Tests;

public sealed class TaskGraphTests
{
  [Fact]
  public void BuildForSelection_IncludesRequiredAndSelectedOptionalsAndDependencies()
  {
    var profile = ProfileParser.Parse(ProfileJson);

    var graph = TaskGraph.BuildForSelection(profile, selectedOptionalTaskIds: ["resharper"]);

    Assert.Equal(
        ["dotnet-sdk", "visual-studio", "resharper"],
        graph.OrderedTaskIds);
  }

  [Fact]
  public void BuildForSelection_ThrowsOnUnknownTaskId()
  {
    var profile = ProfileParser.Parse(ProfileJson);

    var exception = Assert.Throws<FormatException>(() =>
        TaskGraph.BuildForSelection(profile, selectedOptionalTaskIds: ["missing"]));

    Assert.Contains("missing", exception.Message);
  }

  [Fact]
  public void Build_ThrowsOnCycleAndIncludesPath()
  {
    const string json = """
      {
        "id": "cycle",
        "version": "1.0.0",
        "displayName": "Cycle",
        "tasks": {
          "a": { "displayName": "A", "required": true, "dependsOn": ["b"], "detect": { "executable": "a", "arguments": [] } },
          "b": { "displayName": "B", "required": true, "dependsOn": ["c"], "detect": { "executable": "b", "arguments": [] } },
          "c": { "displayName": "C", "required": true, "dependsOn": ["a"], "detect": { "executable": "c", "arguments": [] } }
        }
      }
      """;

    var profile = ProfileParser.Parse(json);

    var exception = Assert.Throws<InvalidOperationException>(() =>
        TaskGraph.Build(profile, rootTaskIds: ["a"]));

    Assert.Contains("a", exception.Message);
    Assert.Contains("b", exception.Message);
    Assert.Contains("c", exception.Message);
  }

  private const string ProfileJson = """
    {
      "id": "csharp-developer",
      "version": "1.0.0",
      "displayName": "C# Developer",
      "tasks": {
        "dotnet-sdk": {
          "displayName": ".NET SDK",
          "required": true,
          "detect": { "executable": "dotnet", "arguments": ["--version"] },
          "apply": { "executable": "winget", "arguments": ["install", "--id", "Microsoft.DotNet.SDK.10"] }
        },
        "visual-studio": {
          "displayName": "Visual Studio",
          "required": true,
          "dependsOn": ["dotnet-sdk"],
          "detect": { "executable": "vswhere.exe", "arguments": ["-latest"], "versionPattern": "(?<version>\\d+(?:\\.\\d+)+)" },
          "apply": { "executable": "winget", "arguments": ["install", "--id", "Microsoft.VisualStudio.2022.Community"] }
        },
        "resharper": {
          "displayName": "ReSharper",
          "required": false,
          "dependsOn": ["visual-studio"],
          "detect": { "executable": "resharper", "arguments": ["--version"] },
          "apply": { "executable": "winget", "arguments": ["install", "--id", "JetBrains.ReSharper"] }
        }
      }
    }
    """;
}
