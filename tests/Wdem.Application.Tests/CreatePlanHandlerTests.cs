using Wdem.Application.Execution;
using Wdem.Application.Inspection;
using Wdem.Application.Planning;
using Wdem.Domain.Planning;
using Wdem.Domain.Versions;
using Wdem.Infrastructure.Profiles;
using Xunit;

namespace Wdem.Application.Tests;

public sealed class CreatePlanHandlerTests
{
  [Fact]
  public void BuildForSelection_IncludesRequiredAndSelectedOptionalsAndDependencies()
  {
    var profile = ProfileParser.Parse(ProfileJson);

    var plan = new CreatePlanHandler().CreateForSelection(
        profile,
        selectedOptionalTaskIds: ["resharper"],
        inspection: Report(
            ("dotnet-sdk", ComplianceStatus.Satisfied),
            ("visual-studio", ComplianceStatus.UpgradeRequired),
            ("resharper", ComplianceStatus.Missing)));

    Assert.Equal(
        ["dotnet-sdk", "visual-studio", "resharper"],
        plan.Tasks.Select(task => task.Id.Value));
    Assert.Equal(
        [PlannedTaskAction.NoOp, PlannedTaskAction.Upgrade, PlannedTaskAction.Install],
        plan.Tasks.Select(task => task.Action));
  }

  private static InspectReport Report(params (string Id, ComplianceStatus Status)[] tasks) =>
      new(tasks.ToDictionary(
          task => task.Id,
          task => new TaskInspection(
              task.Id,
              task.Status != ComplianceStatus.DetectionFailed,
              null,
              task.Status,
              null,
              new StepReport("detect", task.Status == ComplianceStatus.DetectionFailed ? 1 : 0, "", "")),
          StringComparer.Ordinal));

  [Fact]
  public void BuildForSelection_ThrowsOnUnknownTaskId()
  {
    var profile = ProfileParser.Parse(ProfileJson);

    var exception = Assert.Throws<FormatException>(() =>
        new CreatePlanHandler().CreateForSelection(
            profile,
            selectedOptionalTaskIds: ["missing"]));

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
        new CreatePlanHandler().CreateForTasks(profile, rootTaskIds: ["a"]));

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
