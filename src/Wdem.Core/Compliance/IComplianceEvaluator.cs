using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Compliance;

public sealed record ComplianceResult(
    ComplianceStatus Status,
    string Summary,
    StructuredError? Error = null);

public interface IComplianceEvaluator
{
  ComplianceResult Evaluate(ResourceDefinition desired, DetectedState current);
}
