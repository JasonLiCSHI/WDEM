using System.Security.Cryptography;
using System.Text;
using Wdem.Core.Providers;

namespace Wdem.Core.Resources;

public static class ApprovedResourceFingerprint
{
  public static string Create(ResourceDefinition resource, ResourcePlan plan)
  {
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(plan);

    var canonical = new StringBuilder();
    Append(canonical, ResourceDefinitionFingerprint.Create(resource));
    Append(canonical, plan.ResourceId);
    Append(canonical, plan.ResourceType);
    Append(canonical, plan.ProviderName);
    Append(canonical, plan.DesiredStateFingerprint);
    Append(canonical, plan.ExecutionPreconditionFingerprint);
    Append(canonical, plan.Compliance.ToString());
    Append(canonical, plan.IsExecutable.ToString());
    Append(canonical, plan.Steps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    foreach (var step in plan.Steps)
    {
      Append(canonical, step.Id);
      Append(canonical, step.Action.ToString());
      Append(canonical, step.PrivilegeRequirement.ToString());
      Append(canonical, step.RestartPolicy.ToString());
      Append(canonical, step.IsDestructive.ToString());
    }

    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
  }

  private static void Append(StringBuilder builder, string? value)
  {
    if (value is null)
    {
      builder.Append("-1:");
      return;
    }

    builder.Append(value.Length).Append(':').Append(value);
  }
}
