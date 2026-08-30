using System.Text.Json;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Reporting;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Xunit;

namespace Wdem.Core.Tests.Reporting;

public sealed class RunReportExporterTests
{
  [Fact]
  public void ExportMarkdown_ListsEveryTerminalCategoryAndNeverLeaksToken()
  {
    const string secret = "super-secret-token";
    var exporter = new RunReportExporter(new LogRedactor([secret]));

    string markdown = exporter.ExportMarkdown(CreateTerminalRun(secret));

    Assert.Contains("Satisfied: 1", markdown, StringComparison.Ordinal);
    Assert.Contains("Failed: 1", markdown, StringComparison.Ordinal);
    Assert.Contains("Blocked: 1", markdown, StringComparison.Ordinal);
    Assert.Contains("Cancelled / Skipped: 2", markdown, StringComparison.Ordinal);
    Assert.Contains("Restart required", markdown, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(secret, markdown, StringComparison.Ordinal);
  }

  [Fact]
  public void ExportJson_IsCamelCaseDocumentWithResourceIdsAsPropertiesAndRedactedText()
  {
    const string secret = "super-secret-token";
    var exporter = new RunReportExporter(new LogRedactor([secret]));

    string json = exporter.ExportJson(CreateTerminalRun(secret));
    using JsonDocument document = JsonDocument.Parse(json);

    Assert.Equal("apply", document.RootElement.GetProperty("mode").GetString());
    Assert.Equal(
        "failed",
        document.RootElement.GetProperty("resourceResults")
            .GetProperty("failed")
            .GetProperty("outcome")
            .GetString());
    Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ExportAsync_ReplacesExistingFileAndLeavesNoTemporaryFile()
  {
    string directory = Path.Combine(Path.GetTempPath(), $"wdem-report-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "run.md");
    await File.WriteAllTextAsync(path, "old");
    try
    {
      var exporter = new RunReportExporter(new LogRedactor());

      await exporter.ExportAsync(CreateTerminalRun("safe"), path, CancellationToken.None);

      Assert.StartsWith("# WDEM Run Report", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
      Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  private static ExecutionRun CreateTerminalRun(string secret)
  {
    var started = new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);
    return new ExecutionRun
    {
      RunId = Guid.Parse("9d375dee-375d-4ca6-804e-10dde36873e2"),
      Mode = RunMode.Apply,
      ProfileSourcePath = $"C:/profiles/{secret}.yaml",
      ProfileId = "csharp-developer",
      ProfileVersion = "2.0.0",
      SelectedOptionalResourceIds = new HashSet<string>(["succeeded", "failed"]),
      StartedAtUtc = started,
      EndedAtUtc = started.AddMinutes(2),
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Failed,
      Machine = new MachineInformation("Windows 11", "X64", "workstation", "developer"),
      RestartRequirements = [RestartPolicy.RestartRequired],
      RestartReasons = [$"Restart after {secret}"],
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["satisfied"] = Result("satisfied", ExecutionState.Completed, ExecutionOutcome.NotRequired),
        ["succeeded"] = Result("succeeded", ExecutionState.Completed, ExecutionOutcome.Succeeded),
        ["failed"] = Result(
            "failed",
            ExecutionState.Completed,
            ExecutionOutcome.Failed,
            new StructuredError(
                WdemErrorCode.InstallationError,
                $"Install failed {secret}",
                $"Provider returned {secret}")
            {
              SuggestedAction = $"Retry without {secret}",
              ProcessExitCode = 17
            }),
        ["blocked"] = Result("blocked", ExecutionState.Blocked, ExecutionOutcome.Skipped),
        ["cancelled"] = Result("cancelled", ExecutionState.Completed, ExecutionOutcome.Cancelled),
        ["skipped"] = Result("skipped", ExecutionState.Completed, ExecutionOutcome.Skipped),
        ["restart"] = Result(
            "restart",
            ExecutionState.Completed,
            ExecutionOutcome.Succeeded,
            restart: RestartPolicy.RestartRequired)
      }
    };
  }

  private static ResourceResult Result(
      string id,
      ExecutionState state,
      ExecutionOutcome outcome,
      StructuredError? error = null,
      RestartPolicy restart = RestartPolicy.NoRestart) => new()
      {
        ResourceId = id,
        State = state,
        Outcome = outcome,
        FinalCompliance = outcome is ExecutionOutcome.Succeeded or ExecutionOutcome.NotRequired
            ? ComplianceStatus.Satisfied
            : ComplianceStatus.Missing,
        Progress = state == ExecutionState.Completed ? 1 : 0,
        Message = error?.Detail,
        Error = error,
        RestartRequirement = restart,
        StepResults =
        [
          new StepResult
          {
            StepId = "install",
            Name = "Install",
            State = state,
            Outcome = outcome,
            ProcessExitCode = error?.ProcessExitCode,
            Error = error
          }
        ]
      };
}
