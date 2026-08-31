using Wdem.Core.Profiles;
using Xunit;

namespace Wdem.Core.Tests;

public sealed class ProfileParserTests
{
  [Fact]
  public void Parse_DeclarativeTaskPreservesSourceCommandsAndVersion()
  {
    const string json = """
      {
        "id": "csharp-developer",
        "version": "1.0.0",
        "displayName": "C# Developer",
        "tasks": {
          "visual-studio": {
            "displayName": "Visual Studio",
            "description": "IDE plus organization configuration",
            "required": true,
            "dependsOn": ["dotnet-sdk"],
            "version": ">= 18.3 < 19.0",
            "preferredVersion": "18.3.2",
            "source": "Microsoft.VisualStudio.2022.Community",
            "detect": {
              "executable": "vswhere.exe",
              "arguments": ["-latest"],
              "versionPattern": "(?<version>\\d+(?:\\.\\d+)+)"
            },
            "pre": [
              {
                "displayName": "Prepare Visual Studio configuration",
                "executable": "powershell",
                "arguments": ["-File", "prepare-vs.ps1"]
              }
            ],
            "apply": {
              "executable": "winget",
              "arguments": ["install", "--id", "{source}"]
            },
            "post": [
              { "executable": "vs-config", "arguments": ["import", "settings.vssettings"] },
              { "executable": "vs-plugin-check", "arguments": ["--required"] }
            ]
          },
          "dotnet-sdk": {
            "displayName": ".NET SDK",
            "required": true,
            "detect": { "executable": "dotnet", "arguments": ["--version"] },
            "apply": { "executable": "winget", "arguments": ["install", "--id", "Microsoft.DotNet.SDK.10"] }
          }
        }
      }
      """;

    var profile = ProfileParser.Parse(json);

    var task = profile.Tasks["visual-studio"];
    Assert.Equal(1, profile.SchemaVersion);
    Assert.Equal("visual-studio", task.Id);
    Assert.Equal("Microsoft.VisualStudio.2022.Community", task.Source);
    Assert.Equal("IDE plus organization configuration", task.Description);
    Assert.Equal(">= 18.3 < 19.0", task.VersionConstraint);
    Assert.Equal("18.3.2", task.PreferredVersion);
    Assert.Equal("vswhere.exe", task.Detect.Executable);
    var pre = Assert.Single(task.Pre);
    Assert.Equal("powershell", pre.Executable);
    Assert.Equal("Prepare Visual Studio configuration", pre.DisplayName);
    Assert.Equal("winget", task.Apply!.Executable);
    Assert.Equal(2, task.Post.Count);
    Assert.Equal("vs-config", task.Post[0].Executable);
  }

  [Fact]
  public void Parse_RejectsDependencyThatIsNotDeclared()
  {
    var json = ValidProfile.Replace("\"dependsOn\": []", "\"dependsOn\": [\"missing\"]");

    var exception = Assert.Throws<FormatException>(() => ProfileParser.Parse(json));

    Assert.Contains("missing", exception.Message);
  }

  [Fact]
  public void Parse_RejectsInvalidVersionConstraint()
  {
    var json = ValidProfile.Replace("\"version\": \">= 2.50\"", "\"version\": \"latest\"");

    Assert.Throws<FormatException>(() => ProfileParser.Parse(json));
  }

  [Fact]
  public void Parse_RejectsCommandWithoutExecutable()
  {
    var json = ValidProfile.Replace("\"executable\": \"git\"", "\"executable\": \"\"");

    Assert.Throws<FormatException>(() => ProfileParser.Parse(json));
  }

  [Fact]
  public void Parse_RejectsVersionPatternWithoutNamedVersionGroup()
  {
    var json = ValidProfile.Replace("(?<version>", "(?:");

    Assert.Throws<FormatException>(() => ProfileParser.Parse(json));
  }

  [Fact]
  public void Parse_RejectsUnsupportedSchemaVersion()
  {
    var document = ValidProfile.TrimStart();
    var json = "{ \"schemaVersion\": 2," + document[1..];

    var exception = Assert.Throws<FormatException>(() => ProfileParser.Parse(json));

    Assert.Contains("schemaVersion", exception.Message);
  }

  private const string ValidProfile = """
    {
      "id": "csharp-developer",
      "version": "1.0.0",
      "displayName": "C# Developer",
      "tasks": {
        "git": {
          "displayName": "Git",
          "required": true,
          "dependsOn": [],
          "version": ">= 2.50",
          "source": "Git.Git",
          "detect": {
            "executable": "git",
            "arguments": ["--version"],
            "versionPattern": "git version (?<version>\\d+(?:\\.\\d+)+)"
          },
          "apply": {
            "executable": "winget",
            "arguments": ["install", "--id", "{source}"]
          }
        }
      }
    }
    """;
}
