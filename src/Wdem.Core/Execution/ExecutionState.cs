namespace Wdem.Core.Execution;

public enum ExecutionState
{
  Pending,
  Ready,
  Blocked,
  Running,
  Completed
}

public enum ExecutionOutcome
{
  Succeeded,
  Failed,
  Cancelled,
  NotRequired,
  Skipped
}

public enum RunMode
{
  Inspect,
  Apply
}

public enum WdemErrorCode
{
  ProfileError,
  DependencyError,
  DetectionError,
  VersionError,
  ConfigurationError,
  DownloadError,
  InstallationError,
  VerificationError,
  PermissionError,
  ProviderError,
  CancellationError,
  RestartRequired
}
