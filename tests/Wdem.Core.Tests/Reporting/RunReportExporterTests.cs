using System.Reflection;
using System.Text.Json;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Reporting;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Core.Versions;
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
  public void ReportsIncludeResourceRetryCount()
  {
    var run = CreateTerminalRun("safe");
    var results = run.ResourceResults.ToDictionary(
        pair => pair.Key,
        pair => pair.Key == "failed" ? pair.Value with { RetryCount = 3 } : pair.Value,
        StringComparer.OrdinalIgnoreCase);
    run = run with { ResourceResults = results };
    var exporter = new RunReportExporter(new LogRedactor());

    string json = exporter.ExportJson(run);
    string markdown = exporter.ExportMarkdown(run);
    using JsonDocument document = JsonDocument.Parse(json);

    Assert.Equal(
        3,
        document.RootElement.GetProperty("resourceResults")
            .GetProperty("failed")
            .GetProperty("retryCount")
            .GetInt32());
    Assert.Contains("Retry count: 3", markdown, StringComparison.Ordinal);
  }

  [Fact]
  public void ReportsIncludeRedactedResourcePresentationMetadata()
  {
    const string secret = "resource-presentation-secret";
    ExecutionRun run = CreatePlannedRun(secret);
    var exporter = new RunReportExporter(new LogRedactor([secret]));

    string json = exporter.ExportJson(run);
    string markdown = exporter.ExportMarkdown(run);
    using var document = JsonDocument.Parse(json);
    JsonElement definition = document.RootElement.GetProperty("plan")
        .GetProperty("resources")[0]
        .GetProperty("definition");

    Assert.Equal("display-***", definition.GetProperty("displayName").GetString());
    Assert.Equal("resource-description-***", definition.GetProperty("description").GetString());
    Assert.Contains(
        "Resource display-\\*\\*\\* display name: display-\\*\\*\\*",
        markdown,
        StringComparison.Ordinal);
    Assert.Contains(
        "Resource display-\\*\\*\\* description: resource-description-\\*\\*\\*",
        markdown,
        StringComparison.Ordinal);
    Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
    Assert.DoesNotContain(secret, markdown, StringComparison.Ordinal);
  }

  [Fact]
  public void ExportMarkdown_PreservesEveryResourcePresentationWhenRedactedIdsCollide()
  {
    const string firstSecret = "alpha-resource-secret";
    const string secondSecret = "beta-resource-secret";
    ExecutionRun run = CreatePlannedRun("safe");
    PlannedResource template = Assert.Single(run.Plan!.Resources);
    ResourceDefinition firstDefinition = template.Definition with
    {
      Id = firstSecret,
      DisplayName = $"First {firstSecret}",
      Description = $"First details {firstSecret}"
    };
    ResourceDefinition secondDefinition = template.Definition with
    {
      Id = secondSecret,
      DisplayName = $"Second {secondSecret}",
      Description = $"Second details {secondSecret}"
    };
    run = run with
    {
      Plan = run.Plan with
      {
        Resources =
        [
          template with
          {
            Definition = firstDefinition,
            ResourcePlan = template.ResourcePlan with { ResourceId = firstSecret }
          },
          template with
          {
            Definition = secondDefinition,
            ResourcePlan = template.ResourcePlan with { ResourceId = secondSecret }
          }
        ]
      }
    };

    string markdown = new RunReportExporter(new LogRedactor([firstSecret, secondSecret]))
        .ExportMarkdown(run);

    Assert.Contains(
        "Resource First \\*\\*\\* display name: First \\*\\*\\*",
        markdown,
        StringComparison.Ordinal);
    Assert.Contains(
        "Resource First \\*\\*\\* description: First details \\*\\*\\*",
        markdown,
        StringComparison.Ordinal);
    Assert.Contains(
        "Resource Second \\*\\*\\* display name: Second \\*\\*\\*",
        markdown,
        StringComparison.Ordinal);
    Assert.Contains(
        "Resource Second \\*\\*\\* description: Second details \\*\\*\\*",
        markdown,
        StringComparison.Ordinal);
    Assert.DoesNotContain(firstSecret, markdown, StringComparison.Ordinal);
    Assert.DoesNotContain(secondSecret, markdown, StringComparison.Ordinal);
  }

  [Fact]
  public void ExportMarkdown_PreservesEveryUnexecutedResourceWhenRedactedIdsCollide()
  {
    const string firstSecret = "alpha-unexecuted-secret";
    const string secondSecret = "beta-unexecuted-secret";
    ExecutionRun run = CreateTerminalRun("safe") with
    {
      ResourceResults = new Dictionary<string, ResourceResult>
      {
        [firstSecret] = Result(
            firstSecret,
            ExecutionState.Pending,
            ExecutionOutcome.Skipped),
        [secondSecret] = Result(
            secondSecret,
            ExecutionState.Pending,
            ExecutionOutcome.Skipped)
      }
    };

    string markdown = new RunReportExporter(new LogRedactor([firstSecret, secondSecret]))
        .ExportMarkdown(run);

    Assert.Contains(
        "Unexecuted IDs: \\*\\*\\*, \\*\\*\\*",
        markdown,
        StringComparison.Ordinal);
    Assert.DoesNotContain(firstSecret, markdown, StringComparison.Ordinal);
    Assert.DoesNotContain(secondSecret, markdown, StringComparison.Ordinal);
  }

  [Fact]
  public void ExportMarkdown_FlattensAndEscapesResourceMetadataInjection()
  {
    const string payload = "Friendly\n# heading\n- list [link](https://evil) `code` <script>";
    ExecutionRun run = CreatePlannedRun("safe");
    PlannedResource planned = Assert.Single(run.Plan!.Resources);
    ResourceDefinition definition = planned.Definition with
    {
      DisplayName = payload,
      Description = payload
    };
    run = run with
    {
      Plan = run.Plan with
      {
        Resources = [planned with { Definition = definition }]
      }
    };

    string markdown = new RunReportExporter(new LogRedactor()).ExportMarkdown(run);

    Assert.DoesNotContain("\n# heading", markdown, StringComparison.Ordinal);
    Assert.DoesNotContain("\n- list", markdown, StringComparison.Ordinal);
    Assert.DoesNotContain("[link](https://evil)", markdown, StringComparison.Ordinal);
    Assert.DoesNotContain("`code`", markdown, StringComparison.Ordinal);
    Assert.DoesNotContain("<script>", markdown, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("Friendly # heading - list", markdown, StringComparison.Ordinal);
  }

  [Fact]
  public void ReportsIncludeInitialApprovalFingerprintAndDeferredSummary()
  {
    var fingerprint = new string('E', 64);
    var run = CreateTerminalRun("safe") with
    {
      PlanApproval = new PlanApproval
      {
        InitialPlanFingerprint = fingerprint,
        ConfirmedAtUtc = DateTimeOffset.Parse("2026-08-30T08:00:00Z"),
        Source = PlanApprovalSource.DesktopReviewedPlan,
        DeferredAuthorizations =
        [
          new DeferredAuthorizationProof
          {
            ResourceId = "dynamic-tool",
            ResourceType = "package",
            ProviderName = "fake",
            DefinitionFingerprint = new string('F', 64),
            Origin = ResourceOrigin.Required,
            Dependencies = ["runtime"],
            AllowedActions = [PlanAction.Install],
            MaximumPrivilege = PrivilegeRequirement.Administrator,
            MaximumRestartPolicy = RestartPolicy.NoRestart,
            MaximumRisk = PlanRisk.Elevated,
            AllowDestructive = false
          }
        ]
      }
    };
    var exporter = new RunReportExporter(new LogRedactor());

    var markdown = exporter.ExportMarkdown(run);
    using var json = JsonDocument.Parse(exporter.ExportJson(run));

    Assert.Contains($"Approved plan fingerprint: {fingerprint}", markdown, StringComparison.Ordinal);
    Assert.Contains("Deferred approvals: 1", markdown, StringComparison.Ordinal);
    Assert.Contains("dynamic-tool", markdown, StringComparison.Ordinal);
    var approval = json.RootElement.GetProperty("planApproval");
    Assert.Equal(fingerprint, approval.GetProperty("initialPlanFingerprint").GetString());
    Assert.Equal(1, approval.GetProperty("deferredAuthorizations").GetArrayLength());
  }

  [Fact]
  public void ExecutionRunRedactor_RedactsDeferredApprovalIdentities()
  {
    const string secret = "approval-identity-secret";
    var run = CreateTerminalRun("safe") with
    {
      PlanApproval = new PlanApproval
      {
        InitialPlanFingerprint = new string('E', 64),
        ConfirmedAtUtc = DateTimeOffset.Parse("2026-08-30T08:00:00Z"),
        Source = PlanApprovalSource.DesktopReviewedPlan,
        DeferredAuthorizations =
        [
          new DeferredAuthorizationProof
          {
            ResourceId = secret,
            ResourceType = secret,
            ProviderName = secret,
            DefinitionFingerprint = new string('F', 64),
            Origin = ResourceOrigin.Required,
            Dependencies = [secret],
            AllowedActions = [PlanAction.Install],
            MaximumPrivilege = PrivilegeRequirement.Administrator,
            MaximumRestartPolicy = RestartPolicy.NoRestart,
            MaximumRisk = PlanRisk.Elevated,
            AllowDestructive = false
          }
        ]
      }
    };

    var redacted = new ExecutionRunRedactor(new LogRedactor([secret])).Redact(run);
    var proof = Assert.Single(redacted.PlanApproval!.DeferredAuthorizations);

    Assert.DoesNotContain(secret, proof.ResourceId, StringComparison.Ordinal);
    Assert.DoesNotContain(secret, proof.ResourceType, StringComparison.Ordinal);
    Assert.DoesNotContain(secret, proof.ProviderName, StringComparison.Ordinal);
    Assert.DoesNotContain(secret, Assert.Single(proof.Dependencies), StringComparison.Ordinal);
  }

  [Fact]
  public void ExecutionRunRedactor_RedactsGraphAndPlanDefinitionPresentationMetadata()
  {
    const string secret = "definition-presentation-secret";
    ExecutionRun redacted = new ExecutionRunRedactor(new LogRedactor([secret]))
        .Redact(CreatePlannedRun(secret));

    ResourceDefinition graphDefinition = Assert.Single(redacted.Graph!.Nodes).Value.Definition;
    ResourceDefinition planDefinition = Assert.Single(redacted.Plan!.Resources).Definition;

    Assert.Equal("display-***", graphDefinition.DisplayName);
    Assert.Equal("resource-description-***", graphDefinition.Description);
    Assert.Equal("display-***", planDefinition.DisplayName);
    Assert.Equal("resource-description-***", planDefinition.Description);
  }

  [Fact]
  public void ExportJson_PreservesEveryDictionaryEntryWhenRedactedKeysCollide()
  {
    const string firstSecret = "alpha-secret";
    const string secondSecret = "beta-secret";
    ResourceDefinition Definition(string id) => new()
    {
      Id = id,
      Type = "package",
      Provider = "fake",
      Parameters = new Dictionary<string, string?>
      {
        [firstSecret] = firstSecret,
        [secondSecret] = secondSecret
      }
    };
    DetectedState Detected(string id) => new()
    {
      ResourceId = id,
      Outcome = DetectionOutcome.Succeeded,
      Evidence = new Dictionary<string, string>
      {
        [firstSecret] = firstSecret,
        [secondSecret] = secondSecret
      }
    };
    ResourceResult ResultWithEvidence(string id) => Result(
        id,
        ExecutionState.Completed,
        ExecutionOutcome.Succeeded) with
    {
      DetectedBefore = Detected(id)
    };
    var firstDefinition = Definition(firstSecret);
    var secondDefinition = Definition(secondSecret);
    var resourcePlan = new ResourcePlan
    {
      ResourceId = firstSecret,
      ResourceType = "package",
      ProviderName = "fake",
      DesiredStateFingerprint = "fingerprint",
      Compliance = ComplianceStatus.Satisfied,
      IsExecutable = true
    };
    ExecutionRun run = CreateTerminalRun("safe") with
    {
      Graph = new ResourceGraph(
          new Dictionary<string, ResolvedResource>
          {
            [firstSecret] = new(firstDefinition, ResourceOrigin.Required, new HashSet<string>()),
            [secondSecret] = new(secondDefinition, ResourceOrigin.Required, new HashSet<string>())
          },
          []),
      Plan = new ExecutionPlan
      {
        PlanId = Guid.NewGuid(),
        Fingerprint = "fingerprint",
        ProfileId = "profile",
        ProfileVersion = "1.0.0",
        Layers = [],
        Resources =
        [
          new PlannedResource
          {
            Definition = firstDefinition,
            Origin = ResourceOrigin.Required,
            Dependencies = [],
            ResourcePlan = resourcePlan,
            Status = PlannedResourceStatus.Ready,
            Risk = PlanRisk.None,
            RequiresElevation = false,
            IsDestructive = false,
            RestartPolicy = RestartPolicy.NoRestart
          }
        ],
        IsExecutable = true
      },
      ResourceResults = new Dictionary<string, ResourceResult>
      {
        [firstSecret] = ResultWithEvidence(firstSecret),
        [secondSecret] = ResultWithEvidence(secondSecret)
      }
    };

    string json = new RunReportExporter(new LogRedactor([firstSecret, secondSecret]))
        .ExportJson(run);
    using JsonDocument document = JsonDocument.Parse(json);

    AssertCollisionSafeKeys(document.RootElement.GetProperty("resourceResults"));
    AssertCollisionSafeKeys(document.RootElement.GetProperty("graph").GetProperty("nodes"));
    AssertCollisionSafeKeys(document.RootElement.GetProperty("plan").GetProperty("resources")[0]
        .GetProperty("definition").GetProperty("parameters"));
    foreach (JsonProperty result in document.RootElement.GetProperty("resourceResults")
                 .EnumerateObject())
    {
      AssertCollisionSafeKeys(result.Value.GetProperty("detectedBefore").GetProperty("evidence"));
    }
    Assert.DoesNotContain(firstSecret, json, StringComparison.Ordinal);
    Assert.DoesNotContain(secondSecret, json, StringComparison.Ordinal);
  }

  [Fact]
  public void ExportJson_UsesExplicitStableSchemaWithoutDomainObjectsOrSecretSentinels()
  {
    const string secret = "schema-secret";
    ExecutionRun run = CreatePlannedRun(secret);

    string json = new RunReportExporter(new LogRedactor([secret])).ExportJson(run);
    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement root = document.RootElement;
    JsonElement graphNode = root.GetProperty("graph").GetProperty("nodes")
        .EnumerateObject().Single().Value;
    JsonElement definition = graphNode.GetProperty("definition");
    JsonElement plan = root.GetProperty("plan");
    JsonElement plannedResource = plan.GetProperty("resources")[0];
    JsonElement resourcePlan = plannedResource.GetProperty("resourcePlan");
    JsonElement result = root.GetProperty("resourceResults").EnumerateObject().Single().Value;
    JsonElement detected = result.GetProperty("detectedBefore");

    AssertPropertyNames(root,
        "runId", "mode", "profileSourcePath", "profileId", "profileVersion",
        "selectedOptionalResourceIds", "startedAtUtc", "endedAtUtc", "state", "outcome",
        "retriedFromRunId", "recoveredFromRunId", "machine", "graph", "plan", "planApproval",
        "resourceResults", "restartRequirements", "restartReasons",
        "acknowledgedRestartResourceIds", "blockedResourceIds", "unexecutedResourceIds");
    AssertPropertyNames(root.GetProperty("machine"),
        "operatingSystem", "architecture", "computerName", "userName");
    AssertPropertyNames(root.GetProperty("graph"), "nodes", "topologicalLayers");
    AssertPropertyNames(graphNode, "definition", "origin", "requiredBy");
    AssertPropertyNames(definition,
        "id", "type", "provider", "displayName", "description", "versionConstraint", "preferredVersion",
        "dependencies", "parameters", "privilegeRequirement", "restartPolicy");
    AssertPropertyNames(plan,
        "planId", "fingerprint", "profileId", "profileVersion", "layers", "resources",
        "isExecutable", "errors");
    AssertPropertyNames(plannedResource,
        "definition", "origin", "dependencies", "resourcePlan", "status", "risk",
        "requiresElevation", "isDestructive", "restartPolicy", "reason", "blockedBy",
        "diagnostics");
    AssertPropertyNames(resourcePlan,
        "resourceId", "resourceType", "providerName", "desiredStateFingerprint",
        "executionPreconditionFingerprint", "compliance", "isExecutable", "steps", "error",
        "structuredErrors");
    AssertPropertyNames(resourcePlan.GetProperty("steps")[0],
        "id", "description", "action", "privilegeRequirement", "restartPolicy",
        "isDestructive", "reason");
    AssertPropertyNames(result,
        "resourceId", "state", "outcome", "retryCount", "finalCompliance", "detectedBefore",
        "detectedAfter", "progress", "message", "startedAtUtc", "endedAtUtc", "error",
        "restartRequirement", "stepResults");
    AssertPropertyNames(detected,
        "resourceId", "outcome", "exists", "version", "installedVersions",
        "configurationHash", "detectedAtUtc", "evidence", "error", "structuredError");
    AssertPropertyNames(detected.GetProperty("installedVersions")[0],
        "major", "minor", "patch", "revision");
    AssertPropertyNames(result.GetProperty("stepResults")[0],
        "stepId", "name", "state", "outcome", "progress", "firstLogSequence",
        "lastLogSequence", "processExitCode", "processSucceeded", "startedAtUtc",
        "endedAtUtc", "error");
    AssertPropertyNames(result.GetProperty("error"),
        "code", "summary", "detail", "resourceId", "stepId", "processExitCode",
        "logLocation", "suggestedAction", "isRetryable", "underlyingExceptionType",
        "underlyingExceptionMessage");

    Type[] domainTypes = typeof(RunReportExporter).GetNestedTypes(BindingFlags.NonPublic)
        .Where(type => type.Name.StartsWith("Report", StringComparison.Ordinal))
        .SelectMany(type => type.GetProperties())
        .Select(property => UnwrapCollectionType(property.PropertyType))
        .Where(type => type.Namespace?.StartsWith("Wdem.Core", StringComparison.Ordinal) == true)
        .Where(type => type.DeclaringType != typeof(RunReportExporter))
        .Where(type => !type.IsEnum)
        .Distinct()
        .ToArray();
    Assert.Empty(domainTypes);
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

  [Fact]
  public async Task ExportAsync_CreatesAbsentFileAndLeavesNoTemporaryFile()
  {
    string directory = Path.Combine(Path.GetTempPath(), $"wdem-report-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "run.json");
    try
    {
      var exporter = new RunReportExporter(new LogRedactor());

      await exporter.ExportAsync(CreateTerminalRun("safe"), path, CancellationToken.None);

      using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
      Assert.Equal("apply", document.RootElement.GetProperty("mode").GetString());
      Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task ExportAsync_TargetCreatedDuringWriteIsAtomicallyOverwrittenWithoutTemporaryFiles()
  {
    string directory = Path.Combine(Path.GetTempPath(), $"wdem-report-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "run.json");
    try
    {
      var exporter = new RunReportExporter(new LogRedactor());
      using var destinationCreated = new ManualResetEventSlim();
      using var watcher = new FileSystemWatcher(directory, "*.tmp")
      {
        EnableRaisingEvents = true
      };
      watcher.Created += (_, _) =>
      {
        File.WriteAllText(path, "concurrent destination");
        destinationCreated.Set();
      };
      ExecutionRun run = CreateTerminalRun("safe") with
      {
        RestartReasons = [new string('x', 16 * 1024 * 1024)]
      };

      await exporter.ExportAsync(run, path);

      Assert.True(destinationCreated.IsSet);
      using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
      Assert.Equal("apply", document.RootElement.GetProperty("mode").GetString());
      Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task ExportAsync_CancellationLeavesDestinationAndNoTemporaryFile()
  {
    string directory = Path.Combine(Path.GetTempPath(), $"wdem-report-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "run.json");
    await File.WriteAllTextAsync(path, "original");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
      var exporter = new RunReportExporter(new LogRedactor());

      await Assert.ThrowsAnyAsync<OperationCanceledException>(
          () => exporter.ExportAsync(CreateTerminalRun("safe"), path, cancellation.Token));

      Assert.Equal("original", await File.ReadAllTextAsync(path));
      Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Theory]
  [InlineData(unchecked((int)0x80070020), true)]
  [InlineData(unchecked((int)0x80070021), true)]
  [InlineData(unchecked((int)0x80070070), false)]
  [InlineData(unchecked((int)0x80131620), false)]
  public void MoveRetryPredicate_RetriesOnlyWindowsSharingAndLockViolations(
      int hResult,
      bool isWindowsLockCode)
  {
    var exception = new IOException("move failed", hResult);
    bool actual = RunReportExporter.IsRetryableMoveFailure(exception);

    Assert.Equal(isWindowsLockCode && OperatingSystem.IsWindows(), actual);
    Assert.False(RunReportExporter.IsRetryableMoveFailure(
        new UnauthorizedAccessException("denied")));
  }

  [Fact]
  public void ExportMarkdown_IncludesEveryStructuredErrorSourceAndRedactsIt()
  {
    const string secret = "super-secret-token";
    StructuredError Error(string source) => new(
        WdemErrorCode.ConfigurationError,
        $"{source} summary {secret}",
        $"{source} details {secret}")
    {
      SuggestedAction = $"{source} action {secret}"
    };
    var definition = new ResourceDefinition
    {
      Id = "failed",
      Type = "package",
      Provider = "fake"
    };
    var resourcePlan = new ResourcePlan
    {
      ResourceId = "failed",
      ResourceType = "package",
      ProviderName = "fake",
      DesiredStateFingerprint = "fingerprint",
      Compliance = ComplianceStatus.DetectionFailed,
      IsExecutable = false,
      StructuredErrors = [Error("resource-plan")]
    };
    var plan = new ExecutionPlan
    {
      PlanId = Guid.NewGuid(),
      Fingerprint = "plan-fingerprint",
      ProfileId = "csharp-developer",
      ProfileVersion = "2.0.0",
      Layers = [],
      Resources =
      [
        new PlannedResource
        {
          Definition = definition,
          Origin = ResourceOrigin.Required,
          Dependencies = [],
          ResourcePlan = resourcePlan,
          Status = PlannedResourceStatus.DetectionFailed,
          Risk = PlanRisk.None,
          RequiresElevation = false,
          IsDestructive = false,
          RestartPolicy = RestartPolicy.NoRestart,
          Diagnostics = [Error("planned-resource")]
        }
      ],
      IsExecutable = false,
      Errors = [Error("plan")]
    };
    ExecutionRun original = CreateTerminalRun(secret);
    var failed = original.ResourceResults["failed"] with
    {
      DetectedBefore = new DetectedState
      {
        ResourceId = "failed",
        Outcome = DetectionOutcome.Failed,
        StructuredError = Error("detected-state")
      }
    };
    var run = original with
    {
      Plan = plan,
      ResourceResults = original.ResourceResults.ToDictionary(
          pair => pair.Key,
          pair => pair.Key == "failed" ? failed : pair.Value,
          StringComparer.OrdinalIgnoreCase)
    };

    string markdown = new RunReportExporter(new LogRedactor([secret]))
        .ExportMarkdown(run);

    foreach (string source in new[] { "plan", "planned-resource", "resource-plan", "detected-state" })
    {
      Assert.Contains($"{source} summary", markdown, StringComparison.Ordinal);
      Assert.Contains($"{source} details", markdown, StringComparison.Ordinal);
      Assert.Contains($"{source} action", markdown, StringComparison.Ordinal);
    }
    Assert.DoesNotContain(secret, markdown, StringComparison.Ordinal);
  }

  [Fact]
  public void ExportMarkdown_IncludesPrimaryAndEveryInstalledVersion()
  {
    ExecutionRun original = CreateTerminalRun("safe");
    var succeeded = original.ResourceResults["succeeded"] with
    {
      DetectedBefore = new DetectedState
      {
        ResourceId = "succeeded",
        Outcome = DetectionOutcome.Succeeded,
        Exists = true,
        Version = "9.0.100",
        InstalledVersions =
        [
          new SemanticVersion(8, 0, 204),
          new SemanticVersion(9, 0, 100)
        ]
      }
    };
    var run = original with
    {
      ResourceResults = original.ResourceResults.ToDictionary(
          pair => pair.Key,
          pair => pair.Key == "succeeded" ? succeeded : pair.Value,
          StringComparer.OrdinalIgnoreCase)
    };

    string markdown = new RunReportExporter(new LogRedactor()).ExportMarkdown(run);

    Assert.Contains("Detected before: 9.0.100", markdown, StringComparison.Ordinal);
    Assert.Contains("8.0.204", markdown, StringComparison.Ordinal);
    Assert.Contains("9.0.100", markdown, StringComparison.Ordinal);
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

  private static ExecutionRun CreatePlannedRun(string secret)
  {
    var error = new StructuredError(
        WdemErrorCode.ConfigurationError,
        $"summary {secret}",
        $"detail {secret}")
    {
      ResourceId = $"resource-{secret}",
      StepId = $"step-{secret}",
      ProcessExitCode = 12,
      LogLocation = $"log-{secret}",
      SuggestedAction = $"action-{secret}",
      IsRetryable = true,
      UnderlyingException = new InvalidOperationException($"exception-{secret}")
    };
    var definition = new ResourceDefinition
    {
      Id = $"resource-{secret}",
      Type = $"type-{secret}",
      Provider = $"provider-{secret}",
      DisplayName = $"display-{secret}",
      Description = $"resource-description-{secret}",
      VersionConstraint = $">={secret}",
      PreferredVersion = $"preferred-{secret}",
      Dependencies = [$"dependency-{secret}"],
      Parameters = new Dictionary<string, string?>
      {
        [$"parameter-{secret}"] = $"value-{secret}"
      },
      PrivilegeRequirement = PrivilegeRequirement.Administrator,
      RestartPolicy = RestartPolicy.RestartRequired
    };
    var resourcePlan = new ResourcePlan
    {
      ResourceId = definition.Id,
      ResourceType = definition.Type,
      ProviderName = definition.Provider,
      DesiredStateFingerprint = $"desired-{secret}",
      ExecutionPreconditionFingerprint = $"precondition-{secret}",
      Compliance = ComplianceStatus.Missing,
      IsExecutable = true,
      Steps =
      [
        new PlanStep
        {
          Id = $"step-{secret}",
          Description = $"description-{secret}",
          Action = PlanAction.Install,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
          RestartPolicy = RestartPolicy.RestartRequired,
          IsDestructive = true,
          Reason = $"reason-{secret}"
        }
      ],
      Error = $"plan-error-{secret}",
      StructuredErrors = [error]
    };
    var detected = new DetectedState
    {
      ResourceId = definition.Id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = true,
      Version = $"version-{secret}",
      InstalledVersions = [new SemanticVersion(1, 2, 3, 4)],
      ConfigurationHash = $"hash-{secret}",
      DetectedAtUtc = new DateTimeOffset(2026, 8, 30, 8, 1, 0, TimeSpan.Zero),
      Evidence = new Dictionary<string, string>
      {
        [$"evidence-{secret}"] = $"evidence-value-{secret}"
      },
      Error = $"detection-error-{secret}",
      StructuredError = error
    };
    ResourceResult result = Result(
        definition.Id,
        ExecutionState.Completed,
        ExecutionOutcome.Failed,
        error,
        RestartPolicy.RestartRequired) with
    {
      DetectedBefore = detected,
      DetectedAfter = detected,
      StartedAtUtc = new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero),
      EndedAtUtc = new DateTimeOffset(2026, 8, 30, 8, 2, 0, TimeSpan.Zero)
    };
    ExecutionRun run = CreateTerminalRun(secret);
    return run with
    {
      Graph = new ResourceGraph(
          new Dictionary<string, ResolvedResource>
          {
            [definition.Id] = new(
                definition,
                ResourceOrigin.Required,
                new HashSet<string>([$"required-by-{secret}"]))
          },
          [new ResourceGraphLayer(0, [definition.Id])]),
      Plan = new ExecutionPlan
      {
        PlanId = Guid.NewGuid(),
        Fingerprint = $"plan-{secret}",
        ProfileId = $"profile-{secret}",
        ProfileVersion = $"profile-version-{secret}",
        Layers = [new ResourceGraphLayer(0, [definition.Id])],
        Resources =
        [
          new PlannedResource
          {
            Definition = definition,
            Origin = ResourceOrigin.Required,
            Dependencies = [$"dependency-{secret}"],
            ResourcePlan = resourcePlan,
            Status = PlannedResourceStatus.Ready,
            Risk = PlanRisk.Destructive,
            RequiresElevation = true,
            IsDestructive = true,
            RestartPolicy = RestartPolicy.RestartRequired,
            Reason = $"planned-reason-{secret}",
            BlockedBy = [$"blocked-by-{secret}"],
            Diagnostics = [error]
          }
        ],
        IsExecutable = true,
        Errors = [error]
      },
      ResourceResults = new Dictionary<string, ResourceResult>
      {
        [definition.Id] = result
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

  private static void AssertCollisionSafeKeys(JsonElement value)
  {
    Assert.Equal(["***", "*** (2)"], value.EnumerateObject().Select(property => property.Name));
  }

  private static void AssertPropertyNames(JsonElement value, params string[] expected)
  {
    Assert.Equal(expected, value.EnumerateObject().Select(property => property.Name));
  }

  private static Type UnwrapCollectionType(Type type)
  {
    if (type.IsArray)
    {
      return type.GetElementType()!;
    }

    if (!type.IsGenericType)
    {
      return type;
    }

    Type definition = type.GetGenericTypeDefinition();
    return definition == typeof(IReadOnlyDictionary<,>)
        ? type.GetGenericArguments()[1]
        : definition == typeof(IReadOnlyList<>)
            ? type.GetGenericArguments()[0]
            : type;
  }
}
