using Wdem.Core.Runs;

namespace Wdem.Core.Reporting;

public interface IRunReportExporter
{
  string ExportJson(ExecutionRun run);

  string ExportMarkdown(ExecutionRun run);

  Task ExportAsync(
      ExecutionRun run,
      string filePath,
      CancellationToken cancellationToken = default);
}
