using System.Security.Cryptography;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Versions;

namespace Wdem.Core.Compliance;

public sealed class ComplianceEvaluator : IComplianceEvaluator
{
  public ComplianceResult Evaluate(ResourceDefinition desired, DetectedState current)
  {
    ArgumentNullException.ThrowIfNull(desired);
    ArgumentNullException.ThrowIfNull(current);

    if (!string.Equals(desired.Id, current.ResourceId, StringComparison.OrdinalIgnoreCase))
    {
      return new ComplianceResult(
          ComplianceStatus.DetectionFailed,
          "Detected state belongs to a different resource.",
          new StructuredError(
              WdemErrorCode.DetectionError,
              "Detected state does not match the resource.",
              $"State for resource '{current.ResourceId}' cannot evaluate resource '{desired.Id}'.")
          {
            ResourceId = desired.Id
          });
    }

    if (current.Outcome == DetectionOutcome.Cancelled)
    {
      return Failure(
          ComplianceStatus.DetectionFailed,
          "Detection was cancelled.",
          current,
          WdemErrorCode.CancellationError);
    }

    if (current.Outcome == DetectionOutcome.Failed)
    {
      return Failure(
          ComplianceStatus.DetectionFailed,
          "Detection failed.",
          current,
          WdemErrorCode.DetectionError);
    }

    if (current.Outcome == DetectionOutcome.Unsupported)
    {
      return Failure(
          ComplianceStatus.Unsupported,
          "The resource is not supported by this provider.",
          current,
          WdemErrorCode.ProviderError);
    }

    if (current.Outcome != DetectionOutcome.Succeeded)
    {
      return Failure(
          ComplianceStatus.DetectionFailed,
          "The provider returned an unknown detection outcome.",
          current,
          WdemErrorCode.DetectionError);
    }

    if (!current.Exists)
    {
      return new ComplianceResult(
          ComplianceStatus.Missing,
          $"Resource '{desired.Id}' is missing.");
    }

    var versionResult = EvaluateVersion(desired, current);
    if (versionResult is not null)
    {
      return versionResult;
    }

    if (desired.Parameters.TryGetValue("expectedSha256", out var expectedHash) &&
        (!TryDecodeSha256(expectedHash, out var expectedBytes) ||
         !TryDecodeSha256(current.ConfigurationHash, out var actualBytes) ||
         !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes)))
    {
      return new ComplianceResult(
          ComplianceStatus.ConfigurationMismatch,
          $"Resource '{desired.Id}' has a different configuration hash.",
          new StructuredError(
              WdemErrorCode.ConfigurationError,
              "Configuration does not match.",
              $"Resource '{desired.Id}' did not match the expected SHA-256 hash.")
          {
            ResourceId = desired.Id
          });
    }

    return new ComplianceResult(
        ComplianceStatus.Satisfied,
        $"Resource '{desired.Id}' satisfies the desired state.");
  }

  private static bool TryDecodeSha256(string? value, out byte[] bytes)
  {
    bytes = Array.Empty<byte>();
    if (value is null || value.Length != 64)
    {
      return false;
    }

    var decoded = new byte[32];
    for (var index = 0; index < decoded.Length; index++)
    {
      var high = DecodeHex(value[index * 2]);
      var low = DecodeHex(value[(index * 2) + 1]);
      if (high < 0 || low < 0)
      {
        return false;
      }

      decoded[index] = (byte)((high << 4) | low);
    }

    bytes = decoded;
    return true;
  }

  private static int DecodeHex(char value) => value switch
  {
    >= '0' and <= '9' => value - '0',
    >= 'a' and <= 'f' => value - 'a' + 10,
    >= 'A' and <= 'F' => value - 'A' + 10,
    _ => -1
  };

  private static ComplianceResult? EvaluateVersion(
      ResourceDefinition desired,
      DetectedState current)
  {
    if (string.IsNullOrWhiteSpace(desired.VersionConstraint))
    {
      return null;
    }

    VersionConstraint constraint;
    try
    {
      constraint = VersionConstraint.Parse(desired.VersionConstraint);
    }
    catch (FormatException exception)
    {
      return VersionMismatch(
          desired,
          "The desired version constraint is invalid.",
          exception);
    }

    var installedVersions = current.InstalledVersions ?? Array.Empty<SemanticVersion>();
    if (installedVersions.Any(constraint.IsSatisfiedBy))
    {
      return null;
    }

    if (SemanticVersion.TryParse(current.Version, out var installedVersion) &&
        constraint.IsSatisfiedBy(installedVersion))
    {
      return null;
    }

    return VersionMismatch(
        desired,
        string.IsNullOrWhiteSpace(current.Version) && installedVersions.Count == 0
            ? "No parseable installed version was detected."
            : "No installed version satisfies the desired constraint.");
  }

  private static ComplianceResult VersionMismatch(
      ResourceDefinition desired,
      string detail,
      Exception? exception = null) =>
      new(
          ComplianceStatus.VersionMismatch,
          $"Resource '{desired.Id}' does not satisfy version constraint '{desired.VersionConstraint}'.",
          new StructuredError(
              WdemErrorCode.VersionError,
              "Installed version does not match.",
              detail)
          {
            ResourceId = desired.Id,
            UnderlyingException = exception
          });

  private static ComplianceResult Failure(
      ComplianceStatus status,
      string summary,
      DetectedState current,
      WdemErrorCode fallbackCode)
  {
    var error = current.StructuredError ?? new StructuredError(
        fallbackCode,
        summary,
        string.IsNullOrWhiteSpace(current.Error) ? summary : current.Error)
    {
      ResourceId = current.ResourceId
    };

    return new ComplianceResult(status, summary, error);
  }
}
