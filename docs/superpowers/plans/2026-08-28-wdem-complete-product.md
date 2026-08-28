# WDEM Complete Product Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver WDEM as an independent, private Windows developer-environment product with WDEM-branded .NET 10 desktop, CLI, elevated-host, profiles, and release artifacts.

**Architecture:** `Wdem.Core` remains the UI-free domain/application layer for profiles, versions, graphs, providers, planning, scheduling, runs, and reports. Migrate reusable MIT-derived WinHome implementation into explicitly transitional `Wdem.LegacySource` code behind `Wdem.Windows`; product hosts are `Wdem.Cli`, `Wdem.Desktop`, and the narrowly scoped `Wdem.ElevatedHost`. WDEM is a standalone private repository (`JasonLiCSHI/WDEM`): WinHome remotes are fetch-only provenance, never merge or pull-request targets.

**Tech Stack:** C# 14/.NET 10, WinUI 3 with Microsoft.WindowsAppSDK 2.4.0, BCL MVVM, xUnit 2.9.3, Microsoft.NET.Test.Sdk 18.8.1, System.CommandLine 2.0.10, YamlDotNet 18.1.0, JsonSchema.Net 9.4.0, and MIT-derived transition adapters.

---

## Starting point and hard boundaries

- After Task 1, `Wdem.sln` has a UI-free `src\Wdem.Core` (`net10.0`) with `ResourceDefinition`, `IResourceProvider`, `ResourceProviderRegistry`, and a safe `LegacyPackageManagerProviderAdapter`.
- Before Task 1, the imported source owns a CLI plus `DefaultProcessRunner`, `WindowsProcessJob`, `WingetService`, plugin host, and JSON state files. Task 1 turns its reusable code into a non-product `Wdem.LegacySource` library; preserve the atomic cancellable installer-job assignment while extracting it, but retire its CLI and executable.
- No WinUI, Windows App SDK, `Microsoft.UI.Xaml`, page/view-model, or UI package may be referenced by `Wdem.Core`. The desktop host uses WinUI 3 controls and self-written BCL MVVM helpers; no third-party UI framework is added.
- Only the WDEM command grammar defined in Task 13 is supported. Existing command behavior may be mined or covered by transition regression tests while adapters are extracted, but no permanent WinHome CLI compatibility guarantee applies and `WinHome.exe` is never built for release.
- The solution must build on the existing Ubuntu lint job, so every Windows-targeting project added to the solution sets `<EnableWindowsTargeting>true</EnableWindowsTargeting>`. WinUI execution tests run only on Windows.
- All product provider process invocation uses `ProcessStartInfo.ArgumentList`; profile values never become raw command strings. Persisted reports and displayed logs pass through one redactor before writing or rendering.

## Planned file structure

| Path | Responsibility |
|---|---|
| `src\Wdem.Core\Versions\SemanticVersion.cs` | Parse and compare four-part numeric product versions. |
| `src\Wdem.Core\Versions\VersionConstraint.cs` | Parse and evaluate exact, wildcard, range, and minimum constraints. |
| `src\Wdem.Core\Profiles\*.cs` | YAML/JSON profile schema loading, semantic validation, selection, and resource resolution. |
| `src\Wdem.Core\Graph\*.cs` | Global resource DAG, exact cycle path discovery, and deterministic topological layers. |
| `src\Wdem.Core\Compliance\ComplianceEvaluator.cs` | Convert detected facts into the six required compliance statuses. |
| `src\Wdem.Core\Planning\*.cs` | Executable/non-executable plans and immutable planned-resource records. |
| `src\Wdem.Core\Execution\*.cs` | State/outcome/error models, dispatcher contract, scheduler, and run coordinator. |
| `src\Wdem.Core\Runs\*.cs` | Run/resource/step records, redacted events, persistence interfaces, recovery, and report formatters. |
| `src\Wdem.Windows\Wdem.Windows.csproj` | Windows-only adapters; references `Wdem.Core` and, only during transition, `Wdem.LegacySource`; never references the desktop project. |
| `src\Wdem.Windows\Processes\*.cs` | Adapter over the hardened existing MIT-derived source process runner. |
| `src\Wdem.Windows\Persistence\JsonExecutionRunStore.cs` | Atomic `%LOCALAPPDATA%\WDEM\runs` storage and NDJSON run logs. |
| `src\Wdem.Windows\Providers\*.cs` | WinGet/Git/.NET/Visual Studio/VSIX/ReSharper/configuration providers. |
| `src\Wdem.Windows\Security\*.cs` | Trusted-file verification and one-elevation named-pipe broker. |
| `src\Wdem.ElevatedHost\Program.cs` | Narrow elevated worker that accepts only persisted WDEM resource plans by named pipe. |
| `src\Wdem.Cli\Program.cs` | WDEM inspect/apply/retry/resume/runs CLI host. |
| `src\Wdem.Desktop\App.xaml` and `MainWindow.xaml` | WinUI 3 composition root and navigation shell. |
| `src\Wdem.Desktop\ViewModels\*.cs` | BCL-only MVVM state and commands. |
| `src\Wdem.Desktop\Views\*.xaml` | Profile, resource, plan, monitor, and completion pages. |
| `profiles\csharp-developer.yaml` and `profiles\schemas\developer-profile.schema.json` | Shipped C# Developer profile and formal profile schema. |
| `tests\Wdem.Core.Tests\*.cs` | Fast domain tests with fake providers and fake run stores. |
| `tests\Wdem.Windows.Tests\*.cs` | Windows-adapter/provider tests with fake process, VS discovery, file, and elevation ports. |
| `tests\Wdem.Desktop.Tests\*.cs` | View-model tests without displaying windows. |
| `docs\wdem\*.md` | User, profile-author, provider-author, recovery, and trust documentation. |

## Delivery milestones

1. **M1 — P0 engine:** Tasks 1–13 deliver a buildable independent WDEM product core that can inspect/apply Git and .NET SDK with a complete run history, scheduler, recovery, CLI, and no GUI dependency.
2. **M2 — P1 Windows tooling:** Tasks 14–18 add Visual Studio, workloads/components/`.vsconfig`, one-prompt UAC, VSIX, ReSharper, `.DotSettings`, and `.vssettings`.
3. **M3 — Product experience:** Tasks 19–23 deliver the WinUI 3 workflow, live monitoring/export, documentation/CI/release assets, and clean-VM acceptance.

### Task 1: Establish WDEM identity, provenance, solution, and transitional source boundary

**Files:**
- Move: `WinHome.sln` → `Wdem.sln`
- Move: `src\WinHome.csproj` → `src\Wdem.LegacySource\Wdem.LegacySource.csproj`
- Move: `src\Engine.cs`, `src\Program.cs`, `src\Properties`, `src\Infrastructure`, `src\Interfaces`, `src\Models`, `src\Providers`, and `src\Services` → `src\Wdem.LegacySource\...`
- Move: `tests\WinHome.Tests` → `tests\Wdem.LegacySource.Tests`
- Create: `THIRD-PARTY-NOTICES.md`
- Create: `docs\wdem\source-provenance.md`
- Create: `tools\Configure-WinHomeProvenanceRemotes.ps1`
- Create: `tests\Wdem.Core.Tests\Identity\RepositoryIdentityTests.cs`
- Modify: `src\Wdem.LegacySource\Wdem.LegacySource.csproj`
- Modify: `tests\Wdem.LegacySource.Tests\Wdem.LegacySource.Tests.csproj`
- Modify: all moved `src\Wdem.LegacySource\**\*.cs` and `tests\Wdem.LegacySource.Tests\**\*.cs` namespaces/usings
- Modify: `README.md`

- [ ] **Step 1: Record the source baseline and configure fetch-only provenance remotes**

Run the existing source tests once before moving files, then record the already-configured provenance remote policy:

```powershell
 dotnet restore WinHome.sln
 dotnet test tests\WinHome.Tests\WinHome.Tests.csproj --no-restore --verbosity minimal
 git remote -v
```

Expected: the pre-migration test run exits `0`; afterwards `origin` is `https://github.com/JasonLiCSHI/WDEM.git` for fetch and push, while `winhome-source` fetches `https://github.com/DotDev262/WinHome.git`, `winhome-fork` fetches `https://github.com/JasonLiCSHI/WinHome.git`, and both provenance remotes display `DISABLED` as their push URL. Do not fetch, merge, push, or open a pull request against either provenance remote as part of this plan.

- [ ] **Step 2: Write failing product-identity tests before moving the source**

Create `tests\Wdem.Core.Tests\Identity\RepositoryIdentityTests.cs`:

```csharp
using System.Xml.Linq;

namespace Wdem.Core.Tests.Identity;

public sealed class RepositoryIdentityTests
{
    [Fact]
    public void ProductIdentity_UsesWdemSolutionProjectsAndNotices()
    {
        var root = FindRepositoryRoot();

        Assert.True(File.Exists(Path.Combine(root, "Wdem.sln")));
        Assert.False(File.Exists(Path.Combine(root, "WinHome.sln")));
        Assert.True(File.Exists(Path.Combine(root, "THIRD-PARTY-NOTICES.md")));

        foreach (var projectPath in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var project = XDocument.Load(projectPath);
            Assert.DoesNotContain(project.Descendants().Where(e => e.Name.LocalName is "AssemblyName" or "RootNamespace"),
                e => string.Equals(e.Value, "WinHome", StringComparison.OrdinalIgnoreCase));
        }
        foreach (var sourcePath in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("namespace WinHome", File.ReadAllText(sourcePath), StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinHome.sln")) ||
                File.Exists(Path.Combine(directory.FullName, "Wdem.sln")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
```

- [ ] **Step 3: Run the identity test and confirm it fails for the absent WDEM identity**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~RepositoryIdentityTests --no-restore`

Expected: FAIL because `Wdem.sln` and `THIRD-PARTY-NOTICES.md` do not yet exist and the current main project still declares `WinHome`.

- [ ] **Step 4: Move the source into a non-product, WDEM-named transition library and create provenance records**

Use `git mv` for every tracked path listed above. Delete the moved `src\Wdem.LegacySource\Program.cs`; the old command host is not a product compatibility target and the transitional assembly must be a library, not an executable. Rename the test project file and project/assembly/root namespaces to `Wdem.LegacySource` and `Wdem.LegacySource.Tests`. Apply a semantic rename of `WinHome` namespaces/usings in every moved C# file to `Wdem.LegacySource`; do not rename historical copyright text.

Set the migrated source project to a library with an explicit WDEM identity and leave `Wdem.Core` excluded from its compile glob:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <OutputType>Library</OutputType>
    <AssemblyName>Wdem.LegacySource</AssemblyName>
    <RootNamespace>Wdem.LegacySource</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DefaultItemExcludes>$(DefaultItemExcludes);Wdem.LegacySource.Tests\**;Wdem.Core\**</DefaultItemExcludes>
  </PropertyGroup>
</Project>
```

Create `THIRD-PARTY-NOTICES.md` with this complete attribution, preserving the upstream copyright and MIT license relationship:

```markdown
# Third-Party Notices

## WinHome-derived source

Portions of this repository are derived from [DotDev262/WinHome](https://github.com/DotDev262/WinHome), including code obtained through the `winhome-source` and `winhome-fork` provenance remotes.

Copyright (c) 2025 Aryan Madhusudhanan

Those portions are licensed under the MIT License. The complete MIT license text is retained in [`LICENSE`](LICENSE). WDEM is an independent private repository and is not a branch, pull request, or merge target of either WinHome repository.
```

Create `docs\wdem\source-provenance.md` with the same source URL/copyright, state that WDEM releases, branding, namespaces, state paths, environment variables, documentation, CI, solution and project names are independent, and state that source remotes are fetch-only provenance remotes.

Create `tools\Configure-WinHomeProvenanceRemotes.ps1`:

```powershell
$ErrorActionPreference = 'Stop'
$remotes = @{
    'winhome-source' = 'https://github.com/DotDev262/WinHome.git'
    'winhome-fork' = 'https://github.com/JasonLiCSHI/WinHome.git'
}
foreach ($name in $remotes.Keys) {
    if ((git remote) -contains $name) { git remote set-url $name $remotes[$name] }
    else { git remote add $name $remotes[$name] }
    git remote set-url --push $name DISABLED
}
foreach ($name in $remotes.Keys) {
    if ((git remote get-url --push $name) -ne 'DISABLED') { throw "Push is not disabled for $name." }
}
```

Regenerate the solution as `Wdem.sln` with `dotnet new sln --name Wdem --force`, then add `src\Wdem.LegacySource\Wdem.LegacySource.csproj`, `src\Wdem.Core\Wdem.Core.csproj`, `tests\Wdem.LegacySource.Tests\Wdem.LegacySource.Tests.csproj`, and `tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj`. Update the README title, executable examples, state-path examples, environment-variable examples, links, and repository URL to WDEM; this is the first product-facing rebrand, not an assertion of legacy CLI compatibility.

- [ ] **Step 5: Verify the transitional source is not a product executable and identity tests pass**

Run:

```powershell
.\tools\Configure-WinHomeProvenanceRemotes.ps1
 dotnet restore Wdem.sln
 dotnet build Wdem.sln --no-restore -p:EnableWindowsTargeting=true
 dotnet test Wdem.sln --no-restore --filter "FullyQualifiedName~RepositoryIdentityTests|FullyQualifiedName~Wdem.LegacySource"
 if (Test-Path 'src\Wdem.LegacySource\bin\Debug\net10.0-windows\Wdem.LegacySource.exe') { throw 'The transition library must not produce an executable.' }
```

Expected: all commands exit `0`; the solution and every project identity are WDEM-named; the transition library has no `.exe`; notices/provenance are present; and both WinHome provenance remotes reject push by configuration.

- [ ] **Step 6: Commit the independent product identity boundary**

```powershell
 git add -A
 git commit -m "refactor(wdem): establish independent product identity"
```

### Task 2: Add buildable WDEM product host projects

**Files:**
- Create: `src\Wdem.Windows\Wdem.Windows.csproj`
- Create: `src\Wdem.Windows\WdemWindowsAssemblyMarker.cs`
- Create: `src\Wdem.Cli\Wdem.Cli.csproj`
- Create: `src\Wdem.Cli\Program.cs`
- Create: `src\Wdem.Desktop\Wdem.Desktop.csproj`
- Create: `src\Wdem.Desktop\App.xaml`
- Create: `src\Wdem.Desktop\App.xaml.cs`
- Create: `src\Wdem.Desktop\MainWindow.xaml`
- Create: `src\Wdem.Desktop\MainWindow.xaml.cs`
- Create: `tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj`
- Create: `tests\Wdem.Windows.Tests\ProjectBoundaryTests.cs`
- Create: `tests\Wdem.Desktop.Tests\Wdem.Desktop.Tests.csproj`
- Create: `tests\Wdem.Desktop.Tests\DesktopProjectTests.cs`
- Modify: `Wdem.sln`

- [ ] **Step 1: Record the post-migration baseline**

Run: `dotnet restore Wdem.sln && dotnet test Wdem.sln --no-restore --verbosity minimal`

Expected: restore succeeds and the migrated transition-source and `Wdem.Core` tests report no failures. This baseline documents source extraction only; it is not a compatibility promise for a WinHome executable or command grammar.

- [ ] **Step 2: Add compilation-boundary tests before the new host projects exist**

Create the two xUnit test projects using the repository's current package versions. In `tests\Wdem.Windows.Tests\ProjectBoundaryTests.cs`, write:

```csharp
using Wdem.Windows;

namespace Wdem.Windows.Tests;

public sealed class ProjectBoundaryTests
{
    [Fact]
    public void WindowsAdapterAssembly_IsAvailable()
    {
        Assert.NotNull(typeof(WdemWindowsAssemblyMarker).Assembly);
    }
}
```

In `tests\Wdem.Desktop.Tests\DesktopProjectTests.cs`, write:

```csharp
using Wdem.Desktop;

namespace Wdem.Desktop.Tests;

public sealed class DesktopProjectTests
{
    [Fact]
    public void DesktopAssembly_IsAvailable()
    {
        Assert.NotNull(typeof(App).Assembly);
    }
}
```

- [ ] **Step 3: Verify the boundary tests fail for missing WDEM hosts**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --no-restore`

Expected: FAIL during compilation because the `Wdem.Windows` project/reference does not yet exist.

- [ ] **Step 4: Create dependency-directed WDEM hosts and add them to `Wdem.sln`**

Create `Wdem.Windows` as the one permitted transition-source consumer; it references inward and never exposes the source assembly as a product executable:

```xml
<!-- src\Wdem.Windows\Wdem.Windows.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Wdem.Core\Wdem.Core.csproj" />
    <ProjectReference Include="..\Wdem.LegacySource\Wdem.LegacySource.csproj" />
  </ItemGroup>
</Project>
```

```xml
<!-- src\Wdem.Desktop\Wdem.Desktop.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <UseWinUI>true</UseWinUI>
    <WindowsPackageType>None</WindowsPackageType>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <AssemblyName>Wdem.Desktop</AssemblyName>
    <RootNamespace>Wdem.Desktop</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Wdem.Core\Wdem.Core.csproj" />
    <ProjectReference Include="..\Wdem.Windows\Wdem.Windows.csproj" />
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.4.0" />
  </ItemGroup>
</Project>
```

Make `Wdem.Cli` a `net10.0-windows` console executable with `EnableWindowsTargeting`, `AssemblyName`/`RootNamespace` of `Wdem.Cli`, references to `Wdem.Core` and `Wdem.Windows`, and System.CommandLine 2.0.10. Add the smallest compiling files:

```csharp
// src\Wdem.Windows\WdemWindowsAssemblyMarker.cs
namespace Wdem.Windows;
public sealed class WdemWindowsAssemblyMarker;

// src\Wdem.Cli\Program.cs
return await Task.FromResult(0);
```

Use standard WinUI 3 roots with a WDEM title. `App` derives from `Microsoft.UI.Xaml.Application`; `MainWindow` derives from `Microsoft.UI.Xaml.Window`; `App.OnLaunched` creates and activates the main window:

```xml
<!-- src\Wdem.Desktop\App.xaml -->
<Application x:Class="Wdem.Desktop.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" />
```

```xml
<!-- src\Wdem.Desktop\MainWindow.xaml -->
<Window x:Class="Wdem.Desktop.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="WDEM" />
```

Give `Wdem.Windows.Tests` direct project references to `Wdem.Core`, `Wdem.Windows`, and `Wdem.Cli`; give `Wdem.Desktop.Tests` direct references to `Wdem.Core`, `Wdem.Windows`, and `Wdem.Desktop`. `Wdem.Desktop.Tests` targets `net10.0-windows10.0.19041.0`; the other Windows projects may remain `net10.0-windows`. Both test projects set `EnableWindowsTargeting` and use the existing exact package versions (`Microsoft.NET.Test.Sdk` 18.8.1, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.5). Add every new project with `dotnet sln Wdem.sln add`.

- [ ] **Step 5: Verify product projects compile and boundary tests pass**

Run: `dotnet restore Wdem.sln && dotnet build Wdem.sln --no-restore -p:EnableWindowsTargeting=true && dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --no-restore && dotnet test tests\Wdem.Desktop.Tests\Wdem.Desktop.Tests.csproj --no-restore`

Expected: all commands exit `0`; `Wdem.Core` remains `net10.0`, the desktop test resolves `Wdem.Desktop.App`, and no `WinHome.exe` is produced.

- [ ] **Step 6: Commit the independently buildable WDEM hosts**

```powershell
git add Wdem.sln src\Wdem.Windows src\Wdem.Cli src\Wdem.Desktop tests\Wdem.Windows.Tests tests\Wdem.Desktop.Tests
git commit -m "feat(wdem): add product host projects"
```

### Task 3: Define execution states, structured errors, and version constraints

**Files:**
- Create: `src\Wdem.Core\Execution\ExecutionState.cs`
- Create: `src\Wdem.Core\Execution\StructuredError.cs`
- Create: `src\Wdem.Core\Versions\SemanticVersion.cs`
- Create: `src\Wdem.Core\Versions\VersionConstraint.cs`
- Create: `tests\Wdem.Core.Tests\Versions\VersionConstraintTests.cs`
- Create: `tests\Wdem.Core.Tests\Execution\StructuredErrorTests.cs`
- Modify: `src\Wdem.Core\Providers\ProviderModels.cs`

- [ ] **Step 1: Write failing semantic-version and error-shape tests**

```csharp
[Theory]
[InlineData("18.3.2", "= 18.3.2", true)]
[InlineData("18.3.5", "18.3.x", true)]
[InlineData("18.4.0", "18.3.x", false)]
[InlineData("18.5.0", ">= 18.3 < 19.0", true)]
[InlineData("2.50.0", ">= 2.50", true)]
[InlineData("2.49.9", ">= 2.50", false)]
public void IsSatisfiedBy_EvaluatesSupportedExpressions(
    string installed, string expression, bool expected)
{
    Assert.True(SemanticVersion.TryParse(installed, out var version));
    Assert.Equal(expected, VersionConstraint.Parse(expression).IsSatisfiedBy(version));
}

[Fact]
public void TryParse_RejectsNonNumericVersion()
{
    Assert.False(SemanticVersion.TryParse("release-candidate", out _));
}
```

```csharp
[Fact]
public void StructuredError_PreservesRequiredDiagnosticFields()
{
    var error = new StructuredError(
        WdemErrorCode.InstallationError, "Install failed", "winget returned 1603")
    {
        ResourceId = "git",
        StepId = "git:install",
        ProcessExitCode = 1603,
        LogLocation = @"C:\logs\run.ndjson",
        SuggestedAction = "Review the installer log and retry.",
        IsRetryable = true
    };

    Assert.Equal("git", error.ResourceId);
    Assert.Equal(1603, error.ProcessExitCode);
    Assert.True(error.IsRetryable);
}
```

- [ ] **Step 2: Confirm the tests fail because the WDEM domain types are absent**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter "FullyQualifiedName~VersionConstraintTests|FullyQualifiedName~StructuredErrorTests" --no-restore`

Expected: FAIL with unresolved `SemanticVersion`, `VersionConstraint`, `StructuredError`, and `WdemErrorCode` symbols.

- [ ] **Step 3: Implement the closed state/error vocabulary and deterministic version parser**

Add exactly these lifecycle values, retaining nullable `ExecutionOutcome?` where a resource/run has not completed:

```csharp
namespace Wdem.Core.Execution;

public enum ExecutionState { Pending, Ready, Blocked, Running, Completed }
public enum ExecutionOutcome { Succeeded, Failed, Cancelled, NotRequired, Skipped }
public enum RunMode { Inspect, Apply }
public enum WdemErrorCode
{
    ProfileError, DependencyError, DetectionError, VersionError,
    ConfigurationError, DownloadError, InstallationError, VerificationError,
    PermissionError, ProviderError, CancellationError, RestartRequired
}
```

Implement `StructuredError` as a serializable record with `Code`, `Summary`, `Detail`, nullable `ResourceId`, `StepId`, `ProcessExitCode`, `LogLocation`, `SuggestedAction`, and `IsRetryable`. Keep a runtime-only `[JsonIgnore] Exception? UnderlyingException` plus persisted `UnderlyingExceptionType` and `UnderlyingExceptionMessage`; sanitize exception messages before persistence.

Implement the public API below. Parse one to four dot-separated non-negative integer segments; normalize omitted minor/patch/revision segments to zero. `VersionConstraint.Parse` accepts `= 18.3.2`, `18.3.x`, `>= 18.3 < 19.0`, `>= 2.50`, and the no-space wildcard `10.0.x`; malformed expressions throw `FormatException` rather than silently matching.

```csharp
namespace Wdem.Core.Versions;

public readonly record struct SemanticVersion(
    int Major, int Minor, int Patch, int Revision = 0) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version);
    public int CompareTo(SemanticVersion other);
}

public sealed class VersionConstraint
{
    public static VersionConstraint Parse(string expression);
    public bool IsSatisfiedBy(SemanticVersion installedVersion);
}
```

Extend, rather than rename, provider model records: add `Upgrade` to `PlanAction`; add `MaxConcurrentOperations` (default `1`) and nullable `ConcurrencyGroup` to `ProviderCapabilities`; when it is null the scheduler derives the group from the planned resource’s `Type` and `Provider`; add optional `StepId` and `ProviderLogLevel` to `ProviderProgress`; preserve only constructors needed by WDEM callers during this incremental change.

- [ ] **Step 4: Run the focused tests**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter "FullyQualifiedName~VersionConstraintTests|FullyQualifiedName~StructuredErrorTests" --no-restore`

Expected: PASS; `18.3.x` excludes `18.4.0`, a satisfied range does not imply upgrade, and an unparsable detected version cannot be treated as compliant.

- [ ] **Step 5: Commit the foundational types**

```powershell
git add src\Wdem.Core\Execution src\Wdem.Core\Versions src\Wdem.Core\Providers\ProviderModels.cs tests\Wdem.Core.Tests\Execution tests\Wdem.Core.Tests\Versions
git commit -m "feat(wdem): add execution and version models"
```

### Task 4: Load and validate YAML/JSON Developer Profiles

**Files:**
- Create: `src\Wdem.Core\Profiles\DeveloperProfile.cs`
- Create: `src\Wdem.Core\Profiles\ProfileResourceReference.cs`
- Create: `src\Wdem.Core\Profiles\ProfileDocument.cs`
- Create: `src\Wdem.Core\Profiles\IProfileCatalog.cs`
- Create: `src\Wdem.Core\Profiles\DirectoryProfileCatalog.cs`
- Create: `src\Wdem.Core\Profiles\ProfileValidator.cs`
- Create: `src\Wdem.Core\Profiles\ProfileLoadResult.cs`
- Create: `src\Wdem.Core\Profiles\ProfileValueExpander.cs`
- Create: `profiles\schemas\developer-profile.schema.json`
- Create: `tests\Wdem.Core.Tests\Profiles\ProfileCatalogTests.cs`
- Create: `tests\Wdem.Core.Tests\TestData\Profiles\valid-csharp.yaml`
- Create: `tests\Wdem.Core.Tests\TestData\Profiles\invalid-provider.json`
- Modify: `src\Wdem.Core\Wdem.Core.csproj`

- [ ] **Step 1: Write failing tests for both formats and actionable validation locations**

```csharp
[Fact]
public async Task LoadAsync_YamlProfile_ResolvesRequiredOptionalAndDefaults()
{
    var catalog = CreateCatalog("valid-csharp.yaml",
        new StubProvider("git", "winget"),
        new StubProvider("dotnet-sdk", "winget"),
        new StubProvider("resharper", "winget"),
        new StubProvider("visual-studio-extension", "vsix"));

    var result = await catalog.LoadAsync("valid-csharp", CancellationToken.None);

    Assert.True(result.IsValid);
    Assert.Equal("csharp-developer", result.Profile!.Id);
    Assert.Equal(["git", "dotnet-sdk"], result.Profile.RequiredResources.Select(x => x.Id));
    Assert.True(result.Profile.OptionalResources.Single(x => x.Id == "resharper").DefaultSelected);
}

[Fact]
public async Task LoadAsync_UnknownProvider_ReturnsProfileErrorWithJsonPointer()
{
    var result = await CreateCatalog("invalid-provider.json").LoadAsync("invalid-provider", CancellationToken.None);

    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.ProfileError, error.Code);
    Assert.Contains("/resources/git/provider", error.Detail, StringComparison.Ordinal);
}

[Fact]
public async Task LoadAsync_UnselectedOptionalEnvironmentValue_IsDeferred()
{
    var result = await CreateCatalog("valid-csharp.yaml").LoadAsync("valid-csharp", CancellationToken.None);

    Assert.True(result.IsValid);
    Assert.Contains("${WDEM_COMPANY_VSIX_PATH}",
        result.Profile!.Resources["company-vs-extension"].Parameters["sourcePath"]);
}
```

Add a test-local `StubProvider : IResourceProvider` whose constructor assigns `ResourceType` and `ProviderName`, whose `Capabilities` is `new()`, whose `ValidateAsync` returns `ProviderValidationResult.Valid`, and whose other lifecycle methods throw `NotSupportedException`. `CreateCatalog` constructs `ResourceProviderRegistry` from its provider arguments and points `DirectoryProfileCatalog` at the test-data directory.

- [ ] **Step 2: Run the profile tests to verify the missing catalog fails**

Run: `dotnet restore Wdem.sln && dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~ProfileCatalogTests --no-restore`

Expected: FAIL because profile catalog and profile document types do not exist.

- [ ] **Step 3: Add profile models, schema validation, and semantic validation**

Add existing repository package versions `YamlDotNet` 18.1.0 and `JsonSchema.Net` 9.4.0 to `Wdem.Core`; these are data-format dependencies, not UI dependencies. Embed `profiles\schemas\developer-profile.schema.json` in the core assembly and require this document shape:

```yaml
schemaVersion: "1.0"
profile:
  id: csharp-developer
  version: 1.0.0
  displayName: C# Developer
  description: Standard C# and .NET development environment
  requiredResources:
    - id: git
  optionalResources:
    - id: resharper
      defaultSelected: false
resources:
  git:
    type: git
    provider: winget
    versionConstraint: ">= 2.50"
    preferredVersion: "2.52.1"
    dependsOn: []
    parameters:
      packageId: Git.Git
```

Use these public records and catalog contract:

```csharp
public sealed record DeveloperProfile
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<ProfileResourceReference> RequiredResources { get; init; } = [];
    public IReadOnlyList<ProfileResourceReference> OptionalResources { get; init; } = [];
    public IReadOnlyDictionary<string, ResourceDefinition> Resources { get; init; } =
        new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ProfileResourceReference
{
    public required string Id { get; init; }
    public string? VersionConstraint { get; init; }
    public string? PreferredVersion { get; init; }
    public bool DefaultSelected { get; init; }
}

public interface IProfileCatalog
{
    Task<ProfileLoadResult> LoadAsync(string profileId, CancellationToken cancellationToken);
    Task<ProfileLoadResult> LoadFileAsync(string profilePath, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(CancellationToken cancellationToken);
}
```

`DirectoryProfileCatalog.LoadAsync` resolves `<profileId>.yaml` then `<profileId>.json` beneath its configured profile directory; `LoadFileAsync` validates an explicit YAML/JSON path and records its canonical full path in `ProfileLoadResult.SourcePath`. `ProfileValidator` must validate schema first, then report semantic errors with file name and JSON pointer for: blank/duplicate profile IDs in a catalog; duplicate or unknown resource references; a resource missing `type`/`provider`; malformed version constraints or preferred versions; unknown provider registry key; unknown dependencies; and each provider’s `ValidateAsync` failure. Do not ignore YAML properties: deserialize into a JSON-compatible tree, reject unknown schema fields, then map to the records. JSON uses the same schema and semantic validator.

`ProfileValueExpander` accepts only `${WDEM_[A-Z0-9_]+}` tokens in parameter values. It leaves an unresolved token on an unselected optional resource so the profile catalog can render its metadata, but `ExpandSelected` returns a `ProfileError` naming the parameter JSON pointer when a selected resource still has an unresolved token. `ResourceGraphBuilder` calls `ExpandSelected` after dependency closure and before it returns executable layers, so no provider receives a literal environment token.

- [ ] **Step 4: Run focused tests, including parse and semantic failures**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~ProfileCatalogTests --no-restore`

Expected: PASS; valid YAML loads, valid JSON loads, an invalid YAML syntax error names its file, and an unknown provider error names `/resources/<id>/provider`.

- [ ] **Step 5: Commit profile ingestion**

```powershell
git add src\Wdem.Core\Profiles src\Wdem.Core\Wdem.Core.csproj profiles\schemas tests\Wdem.Core.Tests\Profiles tests\Wdem.Core.Tests\TestData\Profiles
git commit -m "feat(wdem): validate developer profiles"
```

### Task 5: Resolve Profile selections into one global resource DAG

**Files:**
- Create: `src\Wdem.Core\Graph\ResourceOrigin.cs`
- Create: `src\Wdem.Core\Graph\ResolvedResource.cs`
- Create: `src\Wdem.Core\Graph\ResourceGraph.cs`
- Create: `src\Wdem.Core\Graph\ResourceGraphBuilder.cs`
- Create: `src\Wdem.Core\Profiles\ProfileSelection.cs`
- Create: `tests\Wdem.Core.Tests\Graph\ResourceGraphBuilderTests.cs`

- [ ] **Step 1: Write failing selection, layer, deduplication, and exact-cycle tests**

```csharp
[Fact]
public void Build_SelectingResharperSettings_AddsItsTransitiveDependencies()
{
    var graph = CreateBuilder().Build(Profile(), new ProfileSelection(["resharper-settings"]));

    Assert.Equal(ResourceOrigin.Required, graph.Nodes["visual-studio"].Origin);
    Assert.Equal(ResourceOrigin.AutoDependency, graph.Nodes["resharper"].Origin);
    Assert.Equal(ResourceOrigin.SelectedOptional, graph.Nodes["resharper-settings"].Origin);
    Assert.Equal([["visual-studio"], ["resharper"], ["resharper-settings"]],
        graph.TopologicalLayers.Select(layer => layer.ResourceIds).ToArray());
}

[Fact]
public void Build_Cycle_ReturnsTheClosedDependencyPathAndNoExecutableLayers()
{
    var result = CreateBuilder().TryBuild(CyclicProfile(), new ProfileSelection([]));

    var error = Assert.Single(result.Errors);
    Assert.Equal(WdemErrorCode.DependencyError, error.Code);
    Assert.Contains("a -> b -> c -> a", error.Detail, StringComparison.Ordinal);
    Assert.Empty(result.Graph!.TopologicalLayers);
}
```

- [ ] **Step 2: Verify the missing graph builder fails**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~ResourceGraphBuilderTests --no-restore`

Expected: FAIL with unresolved `ResourceGraphBuilder`, `ProfileSelection`, and `ResourceOrigin`.

- [ ] **Step 3: Implement recursive resolution before graph construction**

Implement:

```csharp
public enum ResourceOrigin { Required, SelectedOptional, AutoDependency }
public sealed record ProfileSelection(
    IReadOnlySet<string>? SelectedOptionalResourceIds = null);
public sealed record ResolvedResource(
    ResourceDefinition Definition,
    ResourceOrigin Origin,
    IReadOnlySet<string> RequiredBy);
public sealed record ResourceGraphLayer(int Index, IReadOnlyList<string> ResourceIds);
public sealed record ResourceGraph(
    IReadOnlyDictionary<string, ResolvedResource> Nodes,
    IReadOnlyList<ResourceGraphLayer> TopologicalLayers);
public sealed record ResourceGraphBuildResult(
    ResourceGraph? Graph,
    IReadOnlyList<StructuredError> Errors);
```

`ResourceGraphBuilder.TryBuild(DeveloperProfile profile, ProfileSelection selection)` must return `ResourceGraphBuildResult`; add `ResourceGraph Build(DeveloperProfile profile, ProfileSelection selection)` as the throwing convenience overload used by happy-path callers and tests. It must:

1. Always seed every required reference. When `SelectedOptionalResourceIds` is `null` (no UI selection has been supplied), seed the profile's default-selected optional references; when it is non-null, treat it as the UI's complete final optional selection, so an explicit empty set can cancel every default-selected optional resource.
2. Reject explicit selection IDs that are not optional. Required resources are independent of the optional selection and can never be removed by omitting them from the explicit set.
3. Visit each `ResourceDefinition.Dependencies` recursively; add absent dependencies as `AutoDependency`; retain the strongest origin in the order `Required`, `SelectedOptional`, `AutoDependency`.
4. Use a case-insensitive ID dictionary, so shared dependencies become one node and remain while any selected node requires them.
5. Expand selected resource parameters through `ProfileValueExpander.ExpandSelected`; return a `ProfileError` with no executable layers when an environment substitution remains unresolved.
6. Run depth-first cycle discovery before Kahn layering, retaining the DFS ancestor stack to emit a closed, ordered path such as `a -> b -> c -> a`.
7. Build deterministic Kahn layers by sorting ready IDs with `StringComparer.OrdinalIgnoreCase`; every edge points from dependency to dependent. Return no execution layers if any graph error exists.

Apply per-reference version/preferred-version overrides with `resource with { VersionConstraint = reference.VersionConstraint ?? resource.VersionConstraint }`, preserving the existing `ResourceDefinitionFingerprint` behavior.

- [ ] **Step 4: Run the global-DAG test class**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~ResourceGraphBuilderTests --no-restore`

Expected: PASS; required IDs cannot be deselected, changing optional selection recomputes automatic dependencies, duplicate resources collapse, and every topological layer follows all predecessor layers.

- [ ] **Step 5: Commit resource selection and dependency resolution**

```powershell
git add src\Wdem.Core\Graph src\Wdem.Core\Profiles\ProfileSelection.cs tests\Wdem.Core.Tests\Graph
git commit -m "feat(wdem): resolve profile resource graphs"
```

### Task 6: Complete provider SDK contracts and compliance evaluation

**Files:**
- Create: `src\Wdem.Core\Compliance\IComplianceEvaluator.cs`
- Create: `src\Wdem.Core\Compliance\ComplianceEvaluator.cs`
- Create: `src\Wdem.Core\Providers\ProviderLogLevel.cs`
- Create: `docs\wdem\provider-sdk.md`
- Create: `tests\Wdem.Core.Tests\Compliance\ComplianceEvaluatorTests.cs`
- Modify: `src\Wdem.Core\Providers\IResourceProvider.cs`
- Modify: `src\Wdem.Core\Providers\ProviderModels.cs`
- Modify: `src\Wdem.Core\Providers\ResourceProviderRegistry.cs`
- Modify: `src\Wdem.LegacySource\Providers\LegacyPackageManagerProviderAdapter.cs`
- Modify: `tests\Wdem.Core.Tests\ResourceDefinitionTests.cs`
- Modify: `tests\Wdem.LegacySource.Tests\LegacyPackageManagerProviderAdapterTests.cs`

- [ ] **Step 1: Write failing compliance and provider-capability tests**

```csharp
[Theory]
[InlineData(false, null, ComplianceStatus.Missing)]
[InlineData(true, "2.49.9", ComplianceStatus.VersionMismatch)]
[InlineData(true, "not-a-version", ComplianceStatus.VersionMismatch)]
[InlineData(true, "2.50.1", ComplianceStatus.Satisfied)]
public void Evaluate_MapsExistenceAndVersionEvidence(
    bool exists, string? version, ComplianceStatus expected)
{
    var state = new DetectedState
    {
        ResourceId = "git", Outcome = DetectionOutcome.Succeeded,
        Exists = exists, Version = version
    };

    Assert.Equal(expected, _evaluator.Evaluate(GitResource(), state).Status);
}

[Fact]
public void Evaluate_DetectionFailure_IsNeverClassifiedAsMissing()
{
    var result = _evaluator.Evaluate(GitResource(), new DetectedState
    {
        ResourceId = "git", Outcome = DetectionOutcome.Failed, Error = "access denied"
    });

    Assert.Equal(ComplianceStatus.DetectionFailed, result.Status);
}
```

- [ ] **Step 2: Run the new tests and observe the absent evaluator API**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~ComplianceEvaluatorTests --no-restore`

Expected: FAIL because `IComplianceEvaluator`, `ComplianceEvaluator`, and `ComplianceResult` are absent.

- [ ] **Step 3: Evolve the SDK without breaking the current adapter**

Keep the existing `IResourceProvider` four-stage methods and registry’s case-insensitive type/provider lookup. Add the following data without deleting `DetectedState.Version`, `DetectedState.Error`, or `ProviderValidationResult.Errors`, so the currently tested `LegacyPackageManagerProviderAdapter` compiles throughout the migration:

```csharp
public sealed record ComplianceResult(
    ComplianceStatus Status,
    string Summary,
    StructuredError? Error = null);

public interface IComplianceEvaluator
{
    ComplianceResult Evaluate(ResourceDefinition desired, DetectedState current);
}
```

Extend `DetectedState` with `IReadOnlyList<SemanticVersion> InstalledVersions`, `string? ConfigurationHash`, `DateTimeOffset DetectedAtUtc`, and `StructuredError? StructuredError`. Extend `ProviderValidationResult` with `IReadOnlyList<StructuredError> StructuredErrors`, while preserving `IsValid` as false if either error collection is non-empty. Extend `ResourceApplyResult` with nullable `StructuredError? Error` and `IReadOnlyList<ProviderStepResult> StepResults`; extend `ResourcePlan` with `IReadOnlyList<StructuredError> StructuredErrors`, retaining its existing string `Error` for transition callers. `ProviderProgress` reports `Stage`, normalized `Percent` in `[0,1]`, `Message`, optional `StepId`, and `ProviderLogLevel`.

```csharp
public sealed record ProviderStepResult
{
    public required string StepId { get; init; }
    public required PlanAction Action { get; init; }
    public double Progress { get; init; }
    public int? ProcessExitCode { get; init; }
    public string? Message { get; init; }
    public StructuredError? Error { get; init; }
}
```

`ComplianceEvaluator` must map detection `Failed` to `DetectionFailed`, `Unsupported` to `Unsupported`, `Exists == false` to `Missing`, any unparsable/mismatching required version to `VersionMismatch`, a mismatched `expectedSha256` parameter versus `ConfigurationHash` to `ConfigurationMismatch`, and otherwise `Satisfied`. When multiple SDK versions are supplied, any parsed installed version satisfying the expression is sufficient. It must never infer satisfaction from an installer exit code.

Document the exact provider SDK lifecycle in `docs\wdem\provider-sdk.md`, including this complete provider signature and the requirement to return structured diagnostics:

```csharp
ValueTask<ProviderValidationResult> ValidateAsync(ResourceDefinition resource, CancellationToken cancellationToken);
ValueTask<DetectedState> DetectAsync(ResourceDefinition resource, CancellationToken cancellationToken);
ValueTask<ResourcePlan> PlanAsync(ResourceDefinition resource, DetectedState currentState, CancellationToken cancellationToken);
ValueTask<ResourceApplyResult> ApplyAsync(ResourceDefinition resource, ResourcePlan plan,
    IProgress<ProviderProgress>? progress, CancellationToken cancellationToken);
ValueTask<VerificationResult> VerifyAsync(ResourceDefinition resource, CancellationToken cancellationToken);
```

Update transition-adapter tests only to assert the added metadata; do not claim that the transition adapter enforces versions. Its existing rejection of version-constrained generic packages remains intentional until product-specific providers arrive in Task 12.

- [ ] **Step 4: Run core and transition adapter tests**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter "FullyQualifiedName~ComplianceEvaluatorTests|FullyQualifiedName~ResourceDefinitionTests" --no-restore && dotnet test tests\Wdem.LegacySource.Tests\Wdem.LegacySource.Tests.csproj --filter FullyQualifiedName~LegacyPackageManagerProviderAdapterTests --no-restore`

Expected: PASS; the transition adapter exposes the required metadata and no detection failure is categorized as `Missing` or `Satisfied`.

- [ ] **Step 5: Commit the provider SDK increment**

```powershell
git add src\Wdem.Core\Compliance src\Wdem.Core\Providers src\Wdem.LegacySource\Providers\LegacyPackageManagerProviderAdapter.cs tests\Wdem.Core.Tests tests\Wdem.LegacySource.Tests\LegacyPackageManagerProviderAdapterTests.cs docs\wdem\provider-sdk.md
git commit -m "feat(wdem): add compliance and provider SDK"
```

### Task 7: Generate auditable executable plans

**Files:**
- Create: `src\Wdem.Core\Planning\ExecutionPlan.cs`
- Create: `src\Wdem.Core\Planning\PlannedResource.cs`
- Create: `src\Wdem.Core\Planning\IExecutionPlanner.cs`
- Create: `src\Wdem.Core\Planning\ExecutionPlanner.cs`
- Create: `tests\Wdem.Core.Tests\Planning\ExecutionPlannerTests.cs`
- Modify: `src\Wdem.Core\Providers\ProviderModels.cs`

- [ ] **Step 1: Write failing plan eligibility tests**

```csharp
[Fact]
public async Task CreateAsync_SatisfiedResource_IsCompletedWithoutApply()
{
    var plan = await _planner.CreateAsync(Graph("git"), States("git", exists: true, "2.52.1"));

    var git = Assert.Single(plan.Resources);
    Assert.Equal(ComplianceStatus.Satisfied, git.ResourcePlan.Compliance);
    Assert.False(git.ResourcePlan.RequiresApply);
    Assert.True(plan.IsExecutable);
}

[Fact]
public async Task CreateAsync_MissingProvider_MakesTheWholePlanNonExecutable()
{
    var plan = await _planner.CreateAsync(Graph("git"), States("git", exists: false));

    Assert.False(plan.IsExecutable);
    Assert.Contains(plan.Errors, error => error.Code == WdemErrorCode.ProviderError);
}
```

- [ ] **Step 2: Run to verify the planner types are missing**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~ExecutionPlannerTests --no-restore`

Expected: FAIL with unresolved `IExecutionPlanner`, `ExecutionPlan`, and `PlannedResource`.

- [ ] **Step 3: Implement immutable plan records and planner rules**

Use these records:

```csharp
public sealed record PlannedResource(
    ResourceDefinition Definition,
    ResourceOrigin Origin,
    IReadOnlyList<string> Dependencies,
    ResourcePlan ResourcePlan);

public sealed record ExecutionPlan
{
    public required Guid PlanId { get; init; }
    public required string ProfileId { get; init; }
    public required string ProfileVersion { get; init; }
    public required IReadOnlyList<ResourceGraphLayer> Layers { get; init; }
    public required IReadOnlyList<PlannedResource> Resources { get; init; }
    public required bool IsExecutable { get; init; }
    public IReadOnlyList<StructuredError> Errors { get; init; } = [];
}

public interface IExecutionPlanner
{
    Task<ExecutionPlan> CreateAsync(
        ResourceGraph graph,
        IReadOnlyDictionary<string, DetectedState> detectedStates,
        string profileId,
        string profileVersion,
        CancellationToken cancellationToken);
}
```

For every graph resource, resolve its provider, run provider validation, run `PlanAsync`, then overwrite neither the resource ID nor its desired-state fingerprint. A plan is executable only when the graph has layers, every provider exists, all parameters validate, each resource plan is executable, and its source validation has passed. Include every dependency, provider name, action, `PrivilegeRequirement`, `RestartPolicy`, and block relation in `ExecutionPlan`; use `PlanAction.None` for satisfied resources. Only `Missing`, `VersionMismatch`, and `ConfigurationMismatch` may retain modifying steps. Do not schedule a plan that has a cycle, provider error, invalid parameters, or unavailable/trust-invalid installation source.


- [ ] **Step 4: Run planner tests**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~ExecutionPlannerTests --no-restore`

Expected: PASS; satisfied resources require no action, only remediable compliance statuses produce modifying steps, and invalid inputs produce a non-executable plan with structured errors.

- [ ] **Step 5: Commit execution-plan creation**

```powershell
git add src\Wdem.Core\Planning src\Wdem.Core\Providers\ProviderModels.cs tests\Wdem.Core.Tests\Planning
git commit -m "feat(wdem): generate execution plans"
```

### Task 8: Persist execution runs, resource results, steps, and redacted logs

**Files:**
- Create: `src\Wdem.Core\Runs\ExecutionRun.cs`
- Create: `src\Wdem.Core\Runs\ResourceResult.cs`
- Create: `src\Wdem.Core\Runs\StepResult.cs`
- Create: `src\Wdem.Core\Runs\RunLogEntry.cs`
- Create: `src\Wdem.Core\Runs\RunEvent.cs`
- Create: `src\Wdem.Core\Runs\IRunEventSink.cs`
- Create: `src\Wdem.Core\Runs\IExecutionRunStore.cs`
- Create: `src\Wdem.Core\Runs\LogRedactor.cs`
- Create: `src\Wdem.Windows\Persistence\WdemDataPaths.cs`
- Create: `src\Wdem.Windows\Persistence\JsonExecutionRunStore.cs`
- Create: `src\Wdem.Windows\Persistence\WindowsMachineInformationProvider.cs`
- Create: `tests\Wdem.Core.Tests\Runs\LogRedactorTests.cs`
- Create: `tests\Wdem.Windows.Tests\Persistence\WdemDataPathsTests.cs`
- Create: `tests\Wdem.Windows.Tests\Persistence\JsonExecutionRunStoreTests.cs`

- [ ] **Step 1: Write failing persistence and sensitive-output tests**

```csharp
[Theory]
[InlineData("Authorization: Bearer abc.def.ghi", "Authorization: Bearer ***")]
[InlineData("password=correct-horse", "password=***")]
[InlineData("thumbprint=0123456789ABCDEF", "thumbprint=***")]
public void Redact_RemovesSecretsBeforePersistence(string input, string expected)
{
    Assert.Equal(expected, _redactor.Redact(input));
}
```

```csharp
[Fact]
public void DefaultRoot_UsesWdemLocalAppDataDirectory()
{
    var paths = new WdemDataPaths(@"C:\Users\Test\AppData\Local");

    Assert.Equal(@"C:\Users\Test\AppData\Local\WDEM", paths.Root);
    Assert.Equal(@"C:\Users\Test\AppData\Local\WDEM\runs", paths.RunsDirectory);
}

[Fact]
public async Task SaveAsync_RoundTripsRunAndAppendsRedactedLog()
{
    var run = SampleRun();
    await _store.CreateAsync(run, CancellationToken.None);
    await _store.AppendLogAsync(run.RunId, new RunLogEntry(
        1, DateTimeOffset.UtcNow, ProviderLogLevel.Info, "git", "git:install",
        "Authorization: Bearer abc.def.ghi"), CancellationToken.None);

    var restored = await _store.GetAsync(run.RunId, CancellationToken.None);
    var log = await File.ReadAllTextAsync(_store.LogPath(run.RunId));

    Assert.Equal(run.ProfileId, restored!.ProfileId);
    Assert.DoesNotContain("abc.def.ghi", log, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Confirm that run records and store ports are absent**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~LogRedactorTests --no-restore && dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~WdemDataPathsTests|FullyQualifiedName~JsonExecutionRunStoreTests" --no-restore`

Expected: FAIL because `ExecutionRun`, `IRunEventSink`, `WdemDataPaths`, and `JsonExecutionRunStore` do not exist.

- [ ] **Step 3: Implement the durable run model and core store contracts**

Persist all required fields in these records: run ID/mode/profile ID/profile version/selected optionals/start/end/state/outcome/machine information/final compliance/graph/plan/resource results/restart information. A `ResourceResult` contains resource ID, `ExecutionState`, nullable `ExecutionOutcome`, before/after `DetectedState`, normalized progress, message, start/end, structured error, restart policy, and `IReadOnlyList<StepResult>`. A `StepResult` contains step ID/name, state/outcome/progress, redacted log sequence range, nullable exit code, start/end, and structured error.

```csharp
public sealed record ExecutionRun
{
    public required Guid RunId { get; init; }
    public required RunMode Mode { get; init; }
    public required string ProfileSourcePath { get; init; }
    public required string ProfileId { get; init; }
    public required string ProfileVersion { get; init; }
    public required IReadOnlySet<string> SelectedOptionalResourceIds { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public required ExecutionState State { get; init; }
    public ExecutionOutcome? Outcome { get; init; }
    public Guid? RetriedFromRunId { get; init; }
    public required MachineInformation Machine { get; init; }
    public ResourceGraph? Graph { get; init; }
    public ExecutionPlan? Plan { get; init; }
    public IReadOnlyDictionary<string, ResourceResult> ResourceResults { get; init; } =
        new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<RestartPolicy> RestartRequirements { get; init; } = [];
    public IReadOnlyList<string> RestartReasons { get; init; } = [];
}

public sealed record ResourceResult
{
    public required string ResourceId { get; init; }
    public required ExecutionState State { get; init; }
    public ExecutionOutcome? Outcome { get; init; }
    public ComplianceStatus? FinalCompliance { get; init; }
    public DetectedState? DetectedBefore { get; init; }
    public DetectedState? DetectedAfter { get; init; }
    public double Progress { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public StructuredError? Error { get; init; }
    public RestartPolicy RestartRequirement { get; init; }
    public IReadOnlyList<StepResult> StepResults { get; init; } = [];
}

public sealed record StepResult
{
    public required string StepId { get; init; }
    public required string Name { get; init; }
    public required ExecutionState State { get; init; }
    public ExecutionOutcome? Outcome { get; init; }
    public double Progress { get; init; }
    public long FirstLogSequence { get; init; }
    public long LastLogSequence { get; init; }
    public int? ProcessExitCode { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public StructuredError? Error { get; init; }
}

public sealed record MachineInformation(
    string OperatingSystem, string Architecture, string ComputerName, string UserName);

public interface IExecutionRunStore
{
    Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken);
    Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(CancellationToken cancellationToken);
    Task SaveAsync(ExecutionRun run, CancellationToken cancellationToken);
    Task AppendLogAsync(Guid runId, RunLogEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<RunLogEntry>> ReadLogPageAsync(Guid runId, long afterSequence, int take,
        CancellationToken cancellationToken);
}
```

`IRunEventSink.PublishAsync(RunEvent runEvent, CancellationToken)` publishes the same already-redacted event to UI/CLI observers after the store operation succeeds. `LogRedactor` removes bearer tokens, password/token/API-key assignments, private-key blocks, certificate bodies, and profile values marked sensitive; preserve non-sensitive provider diagnostics.

Define `RunEvent(Guid RunId, long Sequence, DateTimeOffset TimestampUtc, RunEventKind Kind, string? ResourceId, string? StepId, double? Progress, string Message, StructuredError? Error)` and use `RunEventKind` values `RunStateChanged`, `ResourceStateChanged`, `StepProgress`, `Log`, and `Completed`.

Implement `WdemDataPaths` with a root of `Path.Combine(localApplicationData, "WDEM")`, a `RunsDirectory` of `Path.Combine(Root, "runs")`, and no WinHome fallback for normal product I/O. Implement `JsonExecutionRunStore` under `%LOCALAPPDATA%\WDEM\runs`. Serialize snapshots with `JsonNamingPolicy.CamelCase`, store each snapshot as `<runId>.json`, append logs as `<runId>.ndjson`, write to a sibling `.tmp` file, flush, then atomically replace/move as the existing `StateService` and `StateWriter` do. On malformed snapshots, move the corrupt file to `<runId>.json.corrupted.<utc>.<random>` and return a `ProfileError`/`DetectionError` diagnostic instead of throwing from run discovery. `WindowsMachineInformationProvider` captures Windows version, architecture, computer name, and current username, never secrets.

- [ ] **Step 4: Run the new focused tests**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~LogRedactorTests --no-restore && dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~WdemDataPathsTests|FullyQualifiedName~JsonExecutionRunStoreTests" --no-restore`

Expected: PASS; the default state root is exactly `%LOCALAPPDATA%\WDEM`, snapshots round-trip, corruption is preserved for diagnosis, logs are paged by sequence, and secret material is absent from the stored NDJSON.

- [ ] **Step 5: Commit durable runs and logs**

```powershell
git add src\Wdem.Core\Runs src\Wdem.Windows\Persistence tests\Wdem.Core.Tests\Runs tests\Wdem.Windows.Tests\Persistence
git commit -m "feat(wdem): persist execution runs and logs"
```

### Task 9: Schedule plan layers with concurrency, blocking, cancellation, and outcomes

**Files:**
- Create: `src\Wdem.Core\Execution\IResourceScheduler.cs`
- Create: `src\Wdem.Core\Execution\ResourceScheduler.cs`
- Create: `src\Wdem.Core\Execution\SchedulerResult.cs`
- Create: `tests\Wdem.Core.Tests\Execution\ResourceSchedulerTests.cs`

- [ ] **Step 1: Write failing scheduler behavior tests**

```csharp
[Fact]
public async Task ExecuteAsync_FailedDependency_BlocksAllDownstreamResources()
{
    var result = await _scheduler.ExecuteAsync(
        DiamondPlan(),
        (_, _) => Task.FromResult(Result("a", ExecutionOutcome.Failed)),
        _ => new ProviderCapabilities { MaxConcurrentOperations = 2 },
        maximumConcurrency: 4, CancellationToken.None);

    Assert.Equal(ExecutionState.Blocked, result.Results["b"].State);
    Assert.Equal(ExecutionOutcome.Skipped, result.Results["b"].Outcome);
}

[Fact]
public async Task ExecuteAsync_RespectsGlobalAndProviderConcurrency()
{
    var observed = 0;
    var peak = 0;
    await _scheduler.ExecuteAsync(
        IndependentPlan("a", "b", "c"),
        async (resource, token) =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref observed));
            await Task.Delay(50, token);
            Interlocked.Decrement(ref observed);
            return Result(resource.Definition.Id, ExecutionOutcome.Succeeded);
        },
        _ => new ProviderCapabilities { MaxConcurrentOperations = 1, ConcurrencyGroup = "vs-installer" },
        maximumConcurrency: 3, CancellationToken.None);

    Assert.Equal(1, peak);
}
```

- [ ] **Step 2: Verify scheduler tests fail before implementation**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~ResourceSchedulerTests --no-restore`

Expected: FAIL because `IResourceScheduler`, `ResourceScheduler`, and `SchedulerResult` do not exist.

- [ ] **Step 3: Implement a stateful layer scheduler**

Implement this contract:

```csharp
public interface IResourceScheduler
{
    Task<SchedulerResult> ExecuteAsync(
        ExecutionPlan plan,
        Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
        Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
        int maximumConcurrency,
        CancellationToken cancellationToken);
}
```

Validate `maximumConcurrency` in `[1, 32]`. Set all planned resources `Pending`, then `Ready` only when every declared dependency has `Completed + Succeeded` or `Completed + NotRequired`. Execute ready resources within the global semaphore and a per-`ConcurrencyGroup` semaphore sized by `MaxConcurrentOperations`. Persist/publish state transitions through the run coordinator in Task 10; the scheduler itself returns `SchedulerResult.Results` keyed case-insensitively.

When an upstream result is `Failed`, `Cancelled`, or `Skipped`, mark every not-started dependent `Blocked + Skipped`, attach a non-retryable `DependencyError` naming the failed predecessor, and never invoke its delegate. Use `capabilities.ConcurrencyGroup ?? $"{resource.Definition.Type}\0{resource.Definition.Provider}"` for the per-provider semaphore. When cancellation is requested, stop launching new resources, allow currently executing delegates to observe the token, and complete unstarted resources as `Completed + Cancelled`. Do not treat a canceled or non-zero installer process as success.

- [ ] **Step 4: Run scheduler tests**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~ResourceSchedulerTests --no-restore`

Expected: PASS; all valid predecessors finish before dependents, independent work runs concurrently only within both limits, failed predecessors block descendants, and cancellation produces no newly started work.

- [ ] **Step 5: Commit scheduler behavior**

```powershell
git add src\Wdem.Core\Execution tests\Wdem.Core.Tests\Execution\ResourceSchedulerTests.cs
git commit -m "feat(wdem): schedule resource plans safely"
```

### Task 10: Orchestrate Inspect, Apply, retry, crash detection, and restart recovery

**Files:**
- Create: `src\Wdem.Core\Execution\RunRequest.cs`
- Create: `src\Wdem.Core\Execution\IResourceApplyDispatcher.cs`
- Create: `src\Wdem.Core\Execution\DirectResourceApplyDispatcher.cs`
- Create: `src\Wdem.Core\Execution\IEnvironmentRunService.cs`
- Create: `src\Wdem.Core\Execution\EnvironmentRunService.cs`
- Create: `src\Wdem.Core\Runs\RecoveryCandidate.cs`
- Create: `tests\Wdem.Core.Tests\Execution\EnvironmentRunServiceTests.cs`

- [ ] **Step 1: Write failing end-to-end core orchestration tests with fake providers**

```csharp
[Fact]
public async Task InspectAsync_DetectsAndPlansButNeverCallsApply()
{
    var provider = new FakeProvider { Detected = Missing("git") };
    var service = CreateService(provider);

    var run = await service.InspectAsync(Request(), CancellationToken.None);

    Assert.Equal(RunMode.Inspect, run.Mode);
    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(0, provider.ApplyCalls);
    Assert.Equal(ComplianceStatus.Missing, run.ResourceResults["git"].FinalCompliance);
}

[Fact]
public async Task RetryAsync_CreatesNewRunAndRedetectsBeforePlanning()
{
    var provider = new FakeProvider { Detected = Missing("git") };
    var service = CreateService(provider);
    var failed = await service.ApplyAsync(Request(), CancellationToken.None);
    provider.Detected = Satisfied("git", "2.52.1");

    var retry = await service.RetryAsync(failed.RunId, ["git"], CancellationToken.None);

    Assert.NotEqual(failed.RunId, retry.RunId);
    Assert.True(provider.DetectCalls >= 2);
    Assert.Equal(ExecutionOutcome.NotRequired, retry.ResourceResults["git"].Outcome);
}
```

- [ ] **Step 2: Confirm orchestration tests fail**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~EnvironmentRunServiceTests --no-restore`

Expected: FAIL because `IEnvironmentRunService`, `RunRequest`, and recovery records do not exist.

- [ ] **Step 3: Implement the run coordinator and direct dispatcher**

Define:

```csharp
public sealed record RunRequest(
    string ProfilePath,
    IReadOnlySet<string> SelectedOptionalResourceIds,
    int MaximumConcurrency = 4,
    Guid? RetriedFromRunId = null);

public interface IResourceApplyDispatcher
{
    Task<ResourceApplyResult> ApplyAsync(
        IResourceProvider provider, ResourceDefinition resource, ResourcePlan plan,
        IProgress<ProviderProgress>? progress, CancellationToken cancellationToken);
}

public interface IEnvironmentRunService
{
    Task<ExecutionRun> InspectAsync(RunRequest request, CancellationToken cancellationToken);
    Task<ExecutionRun> ApplyAsync(RunRequest request, CancellationToken cancellationToken);
    Task<ExecutionRun> RetryAsync(Guid priorRunId, IReadOnlySet<string> resourceIds,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<RecoveryCandidate>> FindRecoveryCandidatesAsync(CancellationToken cancellationToken);
    Task<ExecutionRun> RecoverAsync(Guid priorRunId, CancellationToken cancellationToken);
    Task AbandonAsync(Guid priorRunId, CancellationToken cancellationToken);
}
```

`EnvironmentRunService` performs, in order: `IProfileCatalog.LoadFileAsync(request.ProfilePath)` validation; required/optional resolution; global DAG build; provider `DetectAsync`; centralized compliance evaluation; execution-plan creation; persistence of the pre-apply snapshot; scheduling for Apply; provider `VerifyAsync` after every successful apply; final compliance and completed outcome calculation. It persists the canonical profile source path as `ExecutionRun.ProfileSourcePath`. `DirectResourceApplyDispatcher` calls the existing provider `ApplyAsync`; Task 15 replaces only the Windows composition with an elevation-aware dispatcher.

Inspect performs only the first five stages plus planning/report generation: it must not call dispatcher, modify a file/registry/environment variable, install software/extensions, or request restart. Apply treats `ApplyOutcome.Succeeded` as provisional and records `Succeeded` only after `VerifyAsync` returns `ComplianceStatus.Satisfied`; it maps failed verification to `VerificationError`.

Write the snapshot before every result/state transition. On a new host start, `FindRecoveryCandidatesAsync` lists snapshots whose state is `Pending`, `Ready`, `Running`, or whose restart record has pending resources. `RecoverAsync` creates a new run using stored `ProfileSourcePath` and optional selection, sets `RetriedFromRunId`, then always reloads the profile, rebuilds the graph, re-runs Detect, and produces a new plan before applying remaining remediations. It never replays a historical command. `RetryAsync` follows the same rule for only failed/blocked requested resources. `AbandonAsync` marks the historical incomplete run completed/cancelled and does not apply anything.

- [ ] **Step 4: Run coordinator tests**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~EnvironmentRunServiceTests --no-restore`

Expected: PASS; Inspect has zero apply calls, Apply verifies before success, retry/recovery creates a fresh run and Detect/Plan cycle, and incomplete records are discoverable after simulated process termination.

- [ ] **Step 5: Commit the P0 application lifecycle**

```powershell
git add src\Wdem.Core\Execution src\Wdem.Core\Runs\RecoveryCandidate.cs tests\Wdem.Core.Tests\Execution\EnvironmentRunServiceTests.cs
git commit -m "feat(wdem): orchestrate inspect apply and recovery"
```

### Task 11: Bridge MIT-derived transition services without product compatibility coupling

**Files:**
- Create: `src\Wdem.Core\Processes\ProcessExecutionRequest.cs`
- Create: `src\Wdem.Core\Processes\ProcessExecutionResult.cs`
- Create: `src\Wdem.Core\Processes\IProcessExecutor.cs`
- Create: `src\Wdem.Windows\Processes\LegacySourceProcessExecutorAdapter.cs`
- Create: `src\Wdem.Windows\Composition\WdemWindowsFactory.cs`
- Create: `src\Wdem.Windows\Persistence\LegacyStateMigrationAdapter.cs`
- Create: `src\Wdem.LegacySource\Models\ProcessRunResult.cs`
- Create: `src\Wdem.LegacySource\Models\ProcessOutputLine.cs`
- Create: `tests\Wdem.Windows.Tests\Processes\LegacySourceProcessExecutorAdapterTests.cs`
- Create: `tests\Wdem.Windows.Tests\Persistence\LegacyStateMigrationAdapterTests.cs`
- Modify: `src\Wdem.LegacySource\Interfaces\IProcessRunner.cs`
- Modify: `src\Wdem.LegacySource\Services\System\DefaultProcessRunner.cs`
- Modify: `src\Wdem.LegacySource\Services\System\WindowsProcessJob.cs`
- Modify: `tests\Wdem.LegacySource.Tests\Services\System\DefaultProcessRunnerTests.cs`

- [ ] **Step 1: Write failing adapter tests for precise process evidence and legacy state import**

```csharp
[Fact]
public async Task ExecuteAsync_MapsExitCodeOutputAndCancellation()
{
    _legacy.Setup(x => x.RunCommandDetailedAsync(
            "winget", It.IsAny<IEnumerable<string>>(), It.IsAny<Action<ProcessOutputLine>?>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(new ProcessRunResult(true, 1603, ["stdout"], ["stderr"]));

    var result = await _adapter.ExecuteAsync(
        new ProcessExecutionRequest("winget", ["install", "--id", "Git.Git"]), null, CancellationToken.None);

    Assert.True(result.Started);
    Assert.Equal(1603, result.ExitCode);
    Assert.Equal(["stdout"], result.StandardOutput);
    Assert.Equal(["stderr"], result.StandardError);
}
```

- [ ] **Step 2: Run adapter tests before the detailed process contract exists**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~LegacySourceProcessExecutorAdapterTests|FullyQualifiedName~LegacyStateMigrationAdapterTests" --no-restore`

Expected: FAIL with missing `IProcessExecutor`, `ProcessExecutionResult`, and `RunCommandDetailedAsync`.

- [ ] **Step 3: Add an additive detailed process API and adapter**

Define the UI-free process port:

```csharp
public sealed record ProcessExecutionRequest(
    string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null);
public sealed record ProcessExecutionResult(
    bool Started, int? ExitCode, IReadOnlyList<string> StandardOutput,
    IReadOnlyList<string> StandardError, StructuredError? Error = null);
public interface IProcessExecutor
{
    Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request, IProgress<string>? output,
        CancellationToken cancellationToken);
}
```

Add, rather than replace, this method and `ProcessRunResult`/`ProcessOutputLine` to the transition-source `IProcessRunner`:

```csharp
Task<ProcessRunResult> RunCommandDetailedAsync(
    string fileName, IEnumerable<string> arguments,
    Action<ProcessOutputLine>? onOutput, CancellationToken cancellationToken);

public sealed record ProcessRunResult(
    bool Started, int? ExitCode, IReadOnlyList<string> StandardOutput,
    IReadOnlyList<string> StandardError);
public sealed record ProcessOutputLine(bool IsStandardError, string Text);
```

Implement it in `DefaultProcessRunner` using the current `WindowsProcessJob.Start` path on Windows, preserving atomic `STARTUPINFOEX` job assignment and killing the job on cancellation. Capture stdout/stderr separately, exit code, failed start, and output drain completion. Keep the existing bool/string APIs only until `Wdem.Windows` has consumed the detailed method; source-derived regression tests protect the extraction, not a permanent external command contract.

`LegacySourceProcessExecutorAdapter` maps the detailed transition-source result to WDEM’s process result and emits each output line through the supplied progress object. `LegacyStateMigrationAdapter` reads `%LOCALAPPDATA%\WinHome` only once when `%LOCALAPPDATA%\WDEM\migration-v1.json` is absent, imports the discovered step names as a clearly labelled migration record, writes that marker atomically, and never writes to the old directory. It does not convert imported success into current compliance; every WDEM plan still Detects. `WdemWindowsFactory` is the sole Windows composition point: instantiate/reuse `DefaultProcessRunner`, `WingetService`, legacy plugin manager/runner, `StateService`, JSON run store, profile catalog, providers, and `EnvironmentRunService`.

- [ ] **Step 4: Run transition-source regression and new bridge tests**

Run: `dotnet test tests\Wdem.LegacySource.Tests\Wdem.LegacySource.Tests.csproj --filter FullyQualifiedName~DefaultProcessRunnerTests --no-restore && dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~LegacySourceProcessExecutorAdapterTests|FullyQualifiedName~LegacyStateMigrationAdapterTests" --no-restore`

Expected: PASS; Windows cancellation still terminates child processes, existing `RunCommandAsync` behavior is unchanged, detailed process failures retain exit/output evidence, and legacy state is never blindly replayed.

- [ ] **Step 5: Commit the one-time transition-source bridge**

```powershell
git add src\Wdem.Core\Processes src\Wdem.Windows src\Wdem.LegacySource\Interfaces\IProcessRunner.cs src\Wdem.LegacySource\Services\System\DefaultProcessRunner.cs src\Wdem.LegacySource\Services\System\WindowsProcessJob.cs tests\Wdem.Windows.Tests tests\Wdem.LegacySource.Tests\Services\System\DefaultProcessRunnerTests.cs
git commit -m "feat(wdem): bridge MIT-derived source process and state services"
```

### Task 12: Deliver P0 WinGet, Git, and .NET SDK providers

**Files:**
- Create: `src\Wdem.Windows\Providers\WinGetCommandClient.cs`
- Create: `src\Wdem.Windows\Providers\WinGetPackageProvider.cs`
- Create: `src\Wdem.Windows\Providers\GitProvider.cs`
- Create: `src\Wdem.Windows\Providers\DotNetSdkProvider.cs`
- Create: `src\Wdem.Windows\Providers\CommandVersionParser.cs`
- Create: `tests\Wdem.Windows.Tests\Providers\WinGetPackageProviderTests.cs`
- Create: `tests\Wdem.Windows.Tests\Providers\GitProviderTests.cs`
- Create: `tests\Wdem.Windows.Tests\Providers\DotNetSdkProviderTests.cs`
- Modify: `src\Wdem.Windows\Composition\WdemWindowsFactory.cs`

- [ ] **Step 1: Write failing product-provider tests**

```csharp
[Fact]
public async Task DetectAsync_GitVersionSatisfiesConstraintWithoutPlanningUpgrade()
{
    _process.Enqueue("git", ["--version"], Success("git version 2.52.1.windows.1"));

    var state = await _provider.DetectAsync(GitResource(">= 2.50"), CancellationToken.None);
    var plan = await _provider.PlanAsync(GitResource(">= 2.50"), state, CancellationToken.None);

    Assert.True(state.Exists);
    Assert.Equal("2.52.1", state.Version);
    Assert.False(plan.RequiresApply);
}

[Fact]
public async Task ApplyAsync_DotNetUsesExactPreferredVersionAndTokenizedArguments()
{
    var plan = InstallPlan(DotNetResource("10.0.x", "10.0.105"));

    await _dotnet.ApplyAsync(DotNetResource("10.0.x", "10.0.105"), plan, null, CancellationToken.None);

    Assert.Equal(
        ["install", "--id", "Microsoft.DotNet.SDK.10", "--exact", "--version", "10.0.105",
         "--silent", "--accept-package-agreements", "--accept-source-agreements",
         "--disable-interactivity"],
        _process.LastRequest!.Arguments);
}
```

- [ ] **Step 2: Run P0 provider tests to verify they fail**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~WinGetPackageProviderTests|FullyQualifiedName~GitProviderTests|FullyQualifiedName~DotNetSdkProviderTests" --no-restore`

Expected: FAIL because product-specific providers and `WinGetCommandClient` are absent.

- [ ] **Step 3: Implement source-aware WinGet and local detection providers**

Implement `WinGetCommandClient` on `IProcessExecutor`. It runs only tokenized invocations, captures source/exit evidence, and produces `DownloadError` or `InstallationError` with exit code and log location. Its install invocation is:

```csharp
new ProcessExecutionRequest("winget",
[
    "install", "--id", packageId, "--exact", "--version", preferredVersion,
    "--silent", "--accept-package-agreements", "--accept-source-agreements",
    "--disable-interactivity"
]);
```

Omit `--version` only when the profile has no preferred version. Before applying, query source availability; if the exact requested version is unavailable, return a non-executable plan with `DownloadError` and do not substitute a newer/older package.

Register these providers under the current `IResourceProviderRegistry`:

| Resource type | Provider name | Detect source | Package ID |
|---|---|---|---|
| `winget-package` | `winget` | `winget list --id <id> --exact` | profile parameter `packageId` |
| `git` | `winget` | `git --version` | `Git.Git` |
| `dotnet-sdk` | `winget` | `dotnet --list-sdks` | `Microsoft.DotNet.SDK.10` |

`GitProvider` parses `git version 2.52.1.windows.1` as `2.52.1`. `DotNetSdkProvider` parses every first column from `dotnet --list-sdks`, fills `InstalledVersions`, and reports satisfaction when any installed SDK matches the constraint. A missing executable is a successful detection with `Exists == false`; a launched command that returns malformed output is `DetectionFailed`. Verify reruns the same local detection and centralized compliance evaluation. All three providers set progress stages `Detect`, `Plan`, `Apply`, and `Verify`.

- [ ] **Step 4: Run all P0 provider tests**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~WinGetPackageProviderTests|FullyQualifiedName~GitProviderTests|FullyQualifiedName~DotNetSdkProviderTests" --no-restore`

Expected: PASS; satisfied versions produce `NotRequired`, a nonparseable version is not satisfied, exact preferred versions are supplied to WinGet, and verification—not process exit—determines success.

- [ ] **Step 5: Commit P0 providers**

```powershell
git add src\Wdem.Windows\Providers src\Wdem.Windows\Composition\WdemWindowsFactory.cs tests\Wdem.Windows.Tests\Providers
git commit -m "feat(wdem): add Git and dotnet SDK providers"
```

### Task 13: Expose WDEM CLI as the sole supported command surface

**Files:**
- Create: `src\Wdem.Cli\WdemCliBuilder.cs`
- Create: `src\Wdem.Cli\WdemCommandHandler.cs`
- Create: `tests\Wdem.Windows.Tests\Cli\WdemCliBuilderTests.cs`
- Modify: `src\Wdem.Cli\Program.cs`
- Modify: `README.md`

- [ ] **Step 1: Write failing WDEM command binding tests**

```csharp
[Fact]
public async Task Inspect_BindsProfileSelectionsAndJson()
{
    var handler = new CapturingHandler();
    var command = WdemCliBuilder.Build(handler);

    var exitCode = await command.Parse(
        ["inspect", "--profile", @"profiles\csharp-developer.yaml",
         "--select", "resharper", "--json"]).InvokeAsync();

    Assert.Equal(0, exitCode);
    Assert.Equal("inspect", handler.Command);
    Assert.Contains("resharper", handler.Request!.SelectedOptionalResourceIds);
    Assert.True(handler.Json);
}
```

Do not add a compatibility test for the retired source root command. Exercise the WDEM command builder and its exit mapping only; source-derived tests remain temporary coverage for extracted adapters, not a user-facing contract.

- [ ] **Step 2: Run the new WDEM CLI test**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter FullyQualifiedName~WdemCliBuilderTests --no-restore`

Expected: FAIL because `WdemCliBuilder` and `IWdemCommandHandler` do not exist.

- [ ] **Step 3: Implement the command surface and exact exit mapping**

Implement:

```text
wdem inspect --profile <file> [--select <resourceId> ...] [--json]
wdem apply --profile <file> [--select <resourceId> ...] [--max-concurrency 1..32] [--json]
wdem retry --run <guid> --resource <resourceId> ... [--json]
wdem resume --run <guid> [--json]
wdem runs list [--json]
```

`WdemCommandHandler` composes `WdemWindowsFactory`, passes the supplied canonical profile path to `RunRequest`, streams `RunEvent`s to console or JSON Lines, and calls the `IEnvironmentRunService` method matching the command. Return `0` only for a completed run with no failed/blocked requested resource; return `2` for profile/plan validation error, `3` for an execution failure, `130` for cancellation, and `1` for unexpected host errors. Task 21 adds report export to this established command grammar after the exporter exists.

Do not restore a `Program.cs` or executable for transition source. Keep reusable process/state code behind `Wdem.Windows` only, and document `Wdem.Cli.exe` as the sole profile-driven developer-environment CLI.

- [ ] **Step 4: Run CLI binding and WDEM CLI regressions**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter FullyQualifiedName~WdemCliBuilderTests --no-restore`

Expected: PASS; all five WDEM commands bind expected options and the test suite requires no retired command surface.

- [ ] **Step 5: Commit WDEM command-line hosting**

```powershell
git add src\Wdem.Cli tests\Wdem.Windows.Tests\Cli README.md
git commit -m "feat(wdem): add profile-driven CLI"
```

### Task 14: Detect Visual Studio instances, editions, channels, workloads, and components

**Files:**
- Create: `src\Wdem.Windows\VisualStudio\VisualStudioInstance.cs`
- Create: `src\Wdem.Windows\VisualStudio\IVisualStudioDiscovery.cs`
- Create: `src\Wdem.Windows\VisualStudio\VsWhereVisualStudioDiscovery.cs`
- Create: `src\Wdem.Windows\VisualStudio\VisualStudioResourceOptions.cs`
- Create: `src\Wdem.Windows\Providers\VisualStudioProvider.cs`
- Create: `tests\Wdem.Windows.Tests\VisualStudio\VsWhereVisualStudioDiscoveryTests.cs`
- Create: `tests\Wdem.Windows.Tests\Providers\VisualStudioProviderDetectionTests.cs`

- [ ] **Step 1: Write failing instance-selection and installed-component tests**

```csharp
[Fact]
public async Task DetectAsync_MultipleMatchingInstancesWithoutSelector_ReturnsConflict()
{
    _discovery.Instances =
    [
        Instance("a", "18.3.2", "Community", "VisualStudio.18.Release"),
        Instance("b", "18.3.2", "Community", "VisualStudio.18.Release")
    ];

    var state = await _provider.DetectAsync(VisualStudioResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.Equal(WdemErrorCode.DetectionError, state.StructuredError!.Code);
    Assert.Contains("instanceId", state.StructuredError.Detail, StringComparison.Ordinal);
}

[Fact]
public async Task DetectAsync_ReportsInstanceVersionEditionChannelWorkloadsAndComponents()
{
    _discovery.Instances = [Instance("17.0_abc", "18.3.2", "Community", "VisualStudio.18.Release",
        workloads: ["Microsoft.VisualStudio.Workload.ManagedDesktop"],
        components: ["Microsoft.NetCore.Component.Runtime.10.0"])];

    var state = await _provider.DetectAsync(VisualStudioResource(), CancellationToken.None);

    Assert.Equal("17.0_abc", state.Evidence["instanceId"]);
    Assert.Equal("18.3.2", state.Version);
    Assert.Equal("Community", state.Evidence["edition"]);
    Assert.Equal("VisualStudio.18.Release", state.Evidence["channel"]);
}
```

- [ ] **Step 2: Run Visual Studio detection tests**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~VsWhereVisualStudioDiscoveryTests|FullyQualifiedName~VisualStudioProviderDetectionTests" --no-restore`

Expected: FAIL because Visual Studio discovery/options/provider types are absent.

- [ ] **Step 3: Implement read-only Visual Studio discovery**

`VsWhereVisualStudioDiscovery` finds `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe`, invokes:

```text
vswhere.exe -products * -format json -utf8 -prerelease
```

and maps `instanceId`, `installationPath`, `productId`, `productPath`, `catalog.productDisplayVersion`, `installationVersion`, `channelId`, `isComplete`, and `isLaunchable`. It discovers workload/component membership by querying `vswhere -products * -requires <componentId> -format json -utf8` for each requested ID and matching the returned `instanceId`; it does not launch Visual Studio or ReSharper.

Parse `VisualStudioResourceOptions` from `ResourceDefinition.Parameters`: `productId`, optional `instanceId`, `edition`, `channelId`, optional `installPath`, `workloads`, `components`, optional `vsconfigPath`, optional `bootstrapperUri`, and optional `bootstrapperSha256`. Require exactly one explicit instance selection when more than one installed instance satisfies product/edition/channel/version constraints; otherwise return a structured conflict with the candidate IDs. Record all required evidence in `DetectedState.Evidence`.

Register `VisualStudioProvider` as resource type `visual-studio`, provider `visual-studio`. In this task its `PlanAsync` may describe planned actions but `ApplyAsync` must return an explicit provider error until Task 16 adds installer execution; read-only detection remains useful and buildable.

- [ ] **Step 4: Run focused discovery tests**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~VsWhereVisualStudioDiscoveryTests|FullyQualifiedName~VisualStudioProviderDetectionTests" --no-restore`

Expected: PASS; instance/version/edition/channel/workload/component facts are detected, ambiguous selection is actionable, and no test starts Visual Studio.

- [ ] **Step 5: Commit Visual Studio detection**

```powershell
git add src\Wdem.Windows\VisualStudio src\Wdem.Windows\Providers\VisualStudioProvider.cs tests\Wdem.Windows.Tests\VisualStudio tests\Wdem.Windows.Tests\Providers\VisualStudioProviderDetectionTests.cs
git commit -m "feat(wdem): detect Visual Studio installations"
```

### Task 15: Centralize UAC elevation in a constrained one-prompt broker

**Files:**
- Create: `src\Wdem.Core\Execution\IPrivilegeBroker.cs`
- Create: `src\Wdem.Core\Execution\ElevatedResourceRequest.cs`
- Create: `src\Wdem.Windows\Security\IElevatedHostLauncher.cs`
- Create: `src\Wdem.Windows\Security\NamedPipePrivilegeBroker.cs`
- Create: `src\Wdem.Windows\Security\ElevatedHostLauncher.cs`
- Create: `src\Wdem.Windows\Execution\PrivilegeAwareResourceApplyDispatcher.cs`
- Create: `src\Wdem.ElevatedHost\Wdem.ElevatedHost.csproj`
- Create: `src\Wdem.ElevatedHost\Program.cs`
- Create: `tests\Wdem.Windows.Tests\Security\NamedPipePrivilegeBrokerTests.cs`
- Modify: `Wdem.sln`
- Modify: `src\Wdem.Windows\Composition\WdemWindowsFactory.cs`

- [ ] **Step 1: Write failing tests for one UAC launch and refusal mapping**

```csharp
[Fact]
public async Task ApplyAsync_TwoAdministratorResources_StartsOneElevatedHost()
{
    await _broker.ApplyAsync(Request("visual-studio"), null, CancellationToken.None);
    await _broker.ApplyAsync(Request("vsix"), null, CancellationToken.None);

    Assert.Equal(1, _launcher.StartCalls);
    Assert.Equal(2, _launcher.SentRequests.Count);
}

[Fact]
public async Task ApplyAsync_UacDeclined_ReturnsPermissionError()
{
    _launcher.StartException = new Win32Exception(1223);

    var result = await _broker.ApplyAsync(Request("visual-studio"), null, CancellationToken.None);

    Assert.Equal(ApplyOutcome.Cancelled, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
}
```

- [ ] **Step 2: Run the broker tests**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter FullyQualifiedName~NamedPipePrivilegeBrokerTests --no-restore`

Expected: FAIL with missing privilege-broker contracts and elevated host launcher.

- [ ] **Step 3: Implement restricted elevation IPC**

Define the core seam:

```csharp
public interface IPrivilegeBroker
{
    Task<ResourceApplyResult> ApplyAsync(
        ElevatedResourceRequest request, IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record ElevatedResourceRequest(
    Guid RunId, string ResourceId, string PlanFingerprint, string PipeName);
```

`NamedPipePrivilegeBroker` starts `Wdem.ElevatedHost.exe` once per run with `ProcessStartInfo.Verb = "runas"` and one random current-user ACL-protected pipe name. It sends only `RunId`, `ResourceId`, and the approved resource-plan fingerprint; it does not serialize arbitrary command text, PowerShell, profile parameters, or secrets through command-line arguments. The elevated worker reads the persisted run snapshot, recomputes the fingerprint, resolves the registered provider, and refuses any mismatched/unapproved request. It returns redacted progress, structured errors, exit codes, and apply result over the pipe. Keep the elevated worker alive to process later administrator resources in the same run, then terminate it through the existing job-aware process mechanism.

Add `PrivilegeAwareResourceApplyDispatcher` in `Wdem.Windows`: non-administrator plans call `DirectResourceApplyDispatcher`; plans whose steps require `Administrator` use the broker. A Win32 UAC refusal (`1223`) maps to `PermissionError`; the coordinator marks the resource failed/cancelled and the scheduler blocks its downstream resources. Current-user operations must never be elevated. On cancellation, cancel the pipe request and terminate the worker job.

- [ ] **Step 4: Run broker tests**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter FullyQualifiedName~NamedPipePrivilegeBrokerTests --no-restore`

Expected: PASS; only one launcher call serves multiple privileged resources, requests cannot carry arbitrary commands, refusal is structured, and non-admin requests bypass the broker.

- [ ] **Step 5: Commit elevation handling**

```powershell
git add Wdem.sln src\Wdem.Core\Execution src\Wdem.Windows\Security src\Wdem.Windows\Composition src\Wdem.ElevatedHost tests\Wdem.Windows.Tests\Security
git commit -m "feat(wdem): centralize privileged execution"
```

### Task 16: Install/modify/verify Visual Studio, workloads, components, and `.vsconfig`

**Files:**
- Create: `src\Wdem.Windows\Security\TrustedFileVerifier.cs`
- Create: `src\Wdem.Windows\VisualStudio\VisualStudioInstallerClient.cs`
- Create: `tests\Wdem.Windows.Tests\VisualStudio\VisualStudioInstallerClientTests.cs`
- Create: `tests\Wdem.Windows.Tests\Providers\VisualStudioProviderApplyTests.cs`
- Modify: `src\Wdem.Windows\Providers\VisualStudioProvider.cs`
- Modify: `src\Wdem.Windows\Composition\WdemWindowsFactory.cs`

- [ ] **Step 1: Write failing installer-plan tests**

```csharp
[Fact]
public async Task ApplyAsync_ExistingInstance_ModifiesMissingWorkloadAndComponent()
{
    _discovery.Instances = [Instance("17.0_a", "18.3.2", "Community", "VisualStudio.18.Release")];
    var plan = ModifyPlan("17.0_a",
        "Microsoft.VisualStudio.Workload.ManagedDesktop",
        "Microsoft.NetCore.Component.Runtime.10.0");

    await _provider.ApplyAsync(Resource(), plan, null, CancellationToken.None);

    Assert.Equal(
        ["modify", "--installPath", @"C:\VS", "--add", "Microsoft.VisualStudio.Workload.ManagedDesktop",
         "--add", "Microsoft.NetCore.Component.Runtime.10.0", "--passive", "--wait", "--norestart"],
        _installer.LastArguments);
}

[Fact]
public async Task PlanAsync_VsconfigHashMismatch_IsNonExecutable()
{
    var plan = await _provider.PlanAsync(Resource(vsconfigPath: @"C:\profile.vsconfig", sha256: new string('A', 64)),
        MissingState(), CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.ConfigurationError, plan.StructuredErrors.Single().Code);
}
```

- [ ] **Step 2: Run Visual Studio apply tests**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~VisualStudioInstallerClientTests|FullyQualifiedName~VisualStudioProviderApplyTests" --no-restore`

Expected: FAIL because installer client/trusted verifier and modifying behavior are absent.

- [ ] **Step 3: Implement trusted installer and configuration execution**

`TrustedFileVerifier.VerifySha256Async(path, expectedHash, token)` must reject a missing file, a non-64-hex expected hash, or a case-insensitive hash mismatch with a `ConfigurationError`; it may additionally validate Authenticode when a profile supplies publisher thumbprint. Verify the downloaded Visual Studio bootstrapper before launch, and retain its local verified path in the step evidence.

`VisualStudioInstallerClient` uses only the VS setup executable/verified bootstrapper and tokenized arguments. For a missing instance it runs `install --productId <id> [--channelUri <uri>] --installPath <path> --passive --wait --norestart` plus every requested `--add` workload/component. For a selected installed instance it runs the tested `modify` form. For a verified `.vsconfig`, append `--config <absolutePath>` to the same install/modify invocation; its workload/component contents are still re-detected after execution.

Update `VisualStudioProvider.PlanAsync` to distinguish install, modify, and no-op. It must request administrator privilege for install/modify, report progress for bootstrapper verification, install/modify, workload/component application, and verification, and return `RestartRequired`/`RestartRecommended` only from actual installer evidence. `VerifyAsync` reuses Task 14 read-only discovery and requires version, edition, channel, every workload, every component, and `.vsconfig` hash/source evidence to satisfy before reporting success.

- [ ] **Step 4: Run provider apply tests**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~VisualStudioInstallerClientTests|FullyQualifiedName~VisualStudioProviderApplyTests" --no-restore`

Expected: PASS; no arbitrary installer argument is accepted, hash failure blocks the plan, an existing instance is modified rather than reinstalled, and post-install verification is mandatory.

- [ ] **Step 5: Commit Visual Studio modification**

```powershell
git add src\Wdem.Windows\Security\TrustedFileVerifier.cs src\Wdem.Windows\VisualStudio\VisualStudioInstallerClient.cs src\Wdem.Windows\Providers\VisualStudioProvider.cs src\Wdem.Windows\Composition\WdemWindowsFactory.cs tests\Wdem.Windows.Tests\VisualStudio tests\Wdem.Windows.Tests\Providers\VisualStudioProviderApplyTests.cs
git commit -m "feat(wdem): install and configure Visual Studio"
```

### Task 17: Add VSIX and ReSharper integration providers

**Files:**
- Create: `src\Wdem.Windows\VisualStudio\VsixManifestReader.cs`
- Create: `src\Wdem.Windows\Providers\VisualStudioExtensionProvider.cs`
- Create: `src\Wdem.Windows\Providers\ReSharperProvider.cs`
- Create: `tests\Wdem.Windows.Tests\Providers\VisualStudioExtensionProviderTests.cs`
- Create: `tests\Wdem.Windows.Tests\Providers\ReSharperProviderTests.cs`
- Modify: `src\Wdem.Windows\Composition\WdemWindowsFactory.cs`

- [ ] **Step 1: Write failing stable-ID and target-instance tests**

```csharp
[Fact]
public async Task DetectAsync_UsesManifestIdentityAndTargetVisualStudioInstance()
{
    _manifestReader.Add(@"C:\Extensions\company\extension.vsixmanifest",
        id: "Contoso.DeveloperTools", version: "3.2.0", instanceId: "17.0_a");

    var state = await _provider.DetectAsync(ExtensionResource("Contoso.DeveloperTools", "3.2.x", "17.0_a"),
        CancellationToken.None);

    Assert.True(state.Exists);
    Assert.Equal("3.2.0", state.Version);
    Assert.Equal("17.0_a", state.Evidence["visualStudioInstanceId"]);
}

[Fact]
public async Task ReSharperPlan_RequiresVisualStudioDependency()
{
    var validation = await _resharper.ValidateAsync(ReSharperResource(dependsOn: []), CancellationToken.None);

    Assert.False(validation.IsValid);
    Assert.Contains(validation.StructuredErrors, e => e.Code == WdemErrorCode.DependencyError);
}
```

- [ ] **Step 2: Run extension/provider tests**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~VisualStudioExtensionProviderTests|FullyQualifiedName~ReSharperProviderTests" --no-restore`

Expected: FAIL because VSIX manifest reader and the extension/ReSharper providers are absent.

- [ ] **Step 3: Implement VSIX and ReSharper lifecycle providers**

`VsixManifestReader` reads installed and source `.vsixmanifest` XML without launching Visual Studio and returns stable `Identity@Id`, `Identity@Version`, and target instance location. `VisualStudioExtensionProvider` registers `visual-studio-extension`/`vsix`; it validates `extensionId`, source path/URI, expected SHA-256, and a dependency on the specified `visual-studio` resource. Detect evidence includes extension ID, version, source manifest path, and Visual Studio instance ID. Apply invokes the selected instance’s `Common7\IDE\VSIXInstaller.exe` with tokenized `/quiet`, `/admin`, and verified VSIX path via the privilege broker; Verify rereads the installed manifest and version constraint.

`ReSharperProvider` registers `resharper`/`winget`. It requires `visual-studio` in `Dependencies`, detects the target instance’s JetBrains ReSharper extension manifest, evaluates its version, and installs the profile’s preferred `JetBrains.ReSharper` package through the tested WinGet client. Its verification requires both a matching version and a manifest integrated with the selected Visual Studio instance. Do not use an application launch to test integration.

- [ ] **Step 4: Run extension and ReSharper tests**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~VisualStudioExtensionProviderTests|FullyQualifiedName~ReSharperProviderTests" --no-restore`

Expected: PASS; extension identity is stable across paths, instance binding is checked, hash/source validation precedes install, and ReSharper cannot plan without Visual Studio.

- [ ] **Step 5: Commit extensions support**

```powershell
git add src\Wdem.Windows\VisualStudio\VsixManifestReader.cs src\Wdem.Windows\Providers\VisualStudioExtensionProvider.cs src\Wdem.Windows\Providers\ReSharperProvider.cs src\Wdem.Windows\Composition\WdemWindowsFactory.cs tests\Wdem.Windows.Tests\Providers
git commit -m "feat(wdem): add VSIX and ReSharper providers"
```

### Task 18: Import and verify `.DotSettings` and `.vssettings` configuration

**Files:**
- Create: `src\Wdem.Windows\Configuration\ConfigurationSourceResolver.cs`
- Create: `src\Wdem.Windows\Configuration\ConfigurationImporter.cs`
- Create: `src\Wdem.Windows\Providers\ReSharperSettingsProvider.cs`
- Create: `src\Wdem.Windows\Providers\VisualStudioSettingsProvider.cs`
- Create: `tests\Wdem.Windows.Tests\Configuration\ConfigurationSourceResolverTests.cs`
- Create: `tests\Wdem.Windows.Tests\Providers\ConfigurationProviderTests.cs`
- Create: `profiles\csharp-developer.yaml`
- Create: `profiles\assets\csharp-developer.vsconfig`

- [ ] **Step 1: Write failing source/hash/import/dependency tests**

```csharp
[Fact]
public async Task ApplyAsync_DotSettingsCopiesAtomicallyAndVerifiesDestinationHash()
{
    await _resharperSettings.ApplyAsync(DotSettingsResource(), CopyPlan(), null, CancellationToken.None);

    Assert.Equal(ExpectedHash, await HashFileAsync(_destinationPath));
    var verification = await _resharperSettings.VerifyAsync(DotSettingsResource(), CancellationToken.None);
    Assert.Equal(ComplianceStatus.Satisfied, verification.Compliance);
}

[Fact]
public async Task ValidateAsync_VsSettingsRequiresVisualStudioAndTrustedSource()
{
    var result = await _vsSettings.ValidateAsync(VsSettingsResource(
        dependsOn: [], expectedSha256: "not-a-hash"), CancellationToken.None);

    Assert.False(result.IsValid);
    Assert.Contains(result.StructuredErrors, e => e.Code == WdemErrorCode.DependencyError);
    Assert.Contains(result.StructuredErrors, e => e.Code == WdemErrorCode.ConfigurationError);
}
```

- [ ] **Step 2: Run configuration-provider tests**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~ConfigurationSourceResolverTests|FullyQualifiedName~ConfigurationProviderTests" --no-restore`

Expected: FAIL because source resolver/importer/settings providers are absent.

- [ ] **Step 3: Implement allowed sources, import behavior, and final verification**

`ConfigurationSourceResolver` accepts exactly: an application content path below the deployed `profiles\assets` directory; a profile-relative path; an absolute local path; or an absolute UNC path. It rejects traversal escaping application/profile roots, non-file URI schemes, and missing files. Every source needs `expectedSha256`; the resolver computes and verifies the hash before any copy/import.

`ReSharperSettingsProvider` registers `resharper-settings`/`file`, requires `resharper` as a dependency, atomically copies the verified `.DotSettings` source to the profile-specified destination, records source/destination hashes, and verifies destination existence/hash without starting ReSharper.

`VisualStudioSettingsProvider` registers `visual-studio-settings`/`visual-studio-settings`, requires `visual-studio`, verifies source hash, snapshots the source to a profile-specified `settingsStorePath` under the chosen Visual Studio user settings directory, and invokes the chosen instance’s documented `devenv.exe /Command File.ImportSettings <snapshotPath>` only during Apply. Detect/Verify never launch Visual Studio: they check the stored snapshot path, source hash, and target instance evidence. If the profile declares a target settings-store hash and it differs, report `ConfigurationMismatch`; if it declares incompatible edition/channel, block import with `ConfigurationError`.

Ship a valid C# profile using real P0/Visual Studio resource types and relationships:

```yaml
profile:
  id: csharp-developer
  version: 1.0.0
  displayName: C# Developer
  description: Standard C# and .NET development environment
  requiredResources:
    - id: visual-studio
    - id: dotnet-sdk
    - id: git
  optionalResources:
    - id: resharper
      defaultSelected: false
    - id: resharper-settings
      defaultSelected: false
    - id: company-vs-extension
      defaultSelected: false
    - id: visual-studio-settings
      defaultSelected: false
```

Set `resharper-settings.dependsOn: [resharper]`, `resharper.dependsOn: [visual-studio]`, and both extension/settings resources to depend on `visual-studio`. The company extension source is an optional, explicitly named environment substitution (`${WDEM_COMPANY_VSIX_PATH}` plus `${WDEM_COMPANY_VSIX_SHA256}`); selecting it while either value is absent must return a `ProfileError` before any action. This is a deliberate enterprise input contract, not a default download source. The shipped `.vsconfig` contains concrete managed-desktop workload/component IDs and its checked-in SHA-256 is written into the profile.

- [ ] **Step 4: Run configuration and complete-profile tests**

Run: `dotnet test tests\Wdem.Windows.Tests\Wdem.Windows.Tests.csproj --filter "FullyQualifiedName~ConfigurationSourceResolverTests|FullyQualifiedName~ConfigurationProviderTests" --no-restore && dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~ProfileCatalogTests --no-restore`

Expected: PASS; untrusted/missing/hash-mismatched files cannot import, both settings resources verify final hashes, and the shipped profile parses with all required/optional/dependency relationships.

- [ ] **Step 5: Commit configuration import**

```powershell
git add src\Wdem.Windows\Configuration src\Wdem.Windows\Providers\ReSharperSettingsProvider.cs src\Wdem.Windows\Providers\VisualStudioSettingsProvider.cs tests\Wdem.Windows.Tests\Configuration tests\Wdem.Windows.Tests\Providers\ConfigurationProviderTests.cs profiles
git commit -m "feat(wdem): import developer tool settings"
```

### Task 19: Build WinUI 3 profile and resource-selection pages with BCL MVVM

**Files:**
- Create: `src\Wdem.Desktop\ViewModels\ObservableObject.cs`
- Create: `src\Wdem.Desktop\ViewModels\AsyncRelayCommand.cs`
- Create: `src\Wdem.Desktop\ViewModels\MainWindowViewModel.cs`
- Create: `src\Wdem.Desktop\ViewModels\ProfileSelectionViewModel.cs`
- Create: `src\Wdem.Desktop\ViewModels\ResourceSelectionViewModel.cs`
- Create: `src\Wdem.Desktop\ViewModels\ResourceSelectionItemViewModel.cs`
- Create: `src\Wdem.Desktop\Views\ProfileSelectionView.xaml`
- Create: `src\Wdem.Desktop\Views\ResourceSelectionView.xaml`
- Create: `tests\Wdem.Desktop.Tests\ViewModels\ResourceSelectionViewModelTests.cs`
- Modify: `src\Wdem.Desktop\App.xaml`
- Modify: `src\Wdem.Desktop\App.xaml.cs`
- Modify: `src\Wdem.Desktop\MainWindow.xaml`
- Modify: `src\Wdem.Desktop\MainWindow.xaml.cs`

- [ ] **Step 1: Write failing required/optional/auto-dependency view-model tests**

```csharp
[Fact]
public void RequiredResource_CannotBeDeselected()
{
    var item = new ResourceSelectionItemViewModel("git", "Git", ResourceOrigin.Required, isSelected: true);

    item.IsSelected = false;

    Assert.True(item.IsSelected);
    Assert.False(item.CanChangeSelection);
}

[Fact]
public void SelectingSettings_RecomputesAutoDependencyRows()
{
    var viewModel = CreateViewModel();

    viewModel.Items.Single(x => x.Id == "resharper-settings").IsSelected = true;

    Assert.Equal(ResourceOrigin.AutoDependency, viewModel.Items.Single(x => x.Id == "resharper").Origin);
    Assert.True(viewModel.Items.Single(x => x.Id == "visual-studio").IsSelected);
}
```

- [ ] **Step 2: Run desktop view-model tests**

Run: `dotnet test tests\Wdem.Desktop.Tests\Wdem.Desktop.Tests.csproj --filter FullyQualifiedName~ResourceSelectionViewModelTests --no-restore`

Expected: FAIL with missing MVVM/view-model types.

- [ ] **Step 3: Implement WinUI 3 navigation and selection bindings without third-party UI packages**

`ObservableObject` implements `INotifyPropertyChanged`; `AsyncRelayCommand` implements `ICommand`, exposes `Task ExecuteAsync(object? parameter)`, prevents reentrancy, reports exceptions to a supplied error callback, and raises `CanExecuteChanged`. `MainWindowViewModel` owns `object CurrentPage`, `ProfileSelectionViewModel`, `ResourceSelectionViewModel`, and navigation commands. It obtains profiles through the core catalog, but exposes only the shipped `C# Developer` profile as enabled; do not render future C++/Python/Web/Company profile choices as selectable MVP options.

`ResourceSelectionItemViewModel` has `Id`, `DisplayName`, `Description`, `Origin`, `IsSelected`, and `CanChangeSelection`. Required rows always remain selected and disabled. Optional rows are selectable. Auto dependencies are selected, disabled, and labelled “Auto dependency”; recomputation calls the core `ResourceGraphBuilder`, not duplicate UI graph logic.

Use stock WinUI 3 `NavigationView`, `ListView`, `CheckBox`, `Button`, `TextBlock`, bindings, and data templates. `ProfileSelectionView.xaml` displays name/description/version. `ResourceSelectionView.xaml` separates Required, Optional, and Auto Dependency groups and offers exactly `[检查环境]` and `[开始配置]` actions; the actions navigate to the plan page introduced in Task 20. Set the `MainWindow.DataContext` in code-behind through a factory, leaving no service locator in XAML.

- [ ] **Step 4: Run desktop view-model tests and build the WinUI 3 app**

Run: `dotnet test tests\Wdem.Desktop.Tests\Wdem.Desktop.Tests.csproj --filter FullyQualifiedName~ResourceSelectionViewModelTests --no-restore && dotnet build src\Wdem.Desktop\Wdem.Desktop.csproj --no-restore`

Expected: PASS and build succeeds. The test confirms required resources cannot be removed and optional changes are resolved by the core graph.

- [ ] **Step 5: Commit WinUI 3 selection pages**

```powershell
git add src\Wdem.Desktop\ViewModels src\Wdem.Desktop\Views src\Wdem.Desktop\App.xaml src\Wdem.Desktop\App.xaml.cs src\Wdem.Desktop\MainWindow.xaml src\Wdem.Desktop\MainWindow.xaml.cs tests\Wdem.Desktop.Tests\ViewModels
git commit -m "feat(wdem): add profile and resource selection UI"
```

### Task 20: Add WinUI 3 plan review and real-time execution monitoring

**Files:**
- Create: `src\Wdem.Desktop\ViewModels\PlanViewModel.cs`
- Create: `src\Wdem.Desktop\ViewModels\ExecutionMonitorViewModel.cs`
- Create: `src\Wdem.Desktop\ViewModels\ResourceProgressViewModel.cs`
- Create: `src\Wdem.Desktop\ViewModels\LogEntryViewModel.cs`
- Create: `src\Wdem.Desktop\Views\PlanView.xaml`
- Create: `src\Wdem.Desktop\Views\ExecutionMonitorView.xaml`
- Create: `tests\Wdem.Desktop.Tests\ViewModels\ExecutionMonitorViewModelTests.cs`
- Modify: `src\Wdem.Core\Runs\IRunEventSink.cs`
- Modify: `src\Wdem.Windows\Composition\WdemWindowsFactory.cs`

- [ ] **Step 1: Write failing live-event/cancel/retry monitor tests**

```csharp
[Fact]
public void Publish_UpdatesResourceStepProgressAndLogOnDispatcher()
{
    _monitor.Publish(new RunEvent(_runId, 5, DateTimeOffset.UtcNow, RunEventKind.StepProgress,
        "visual-studio", "visual-studio:install", 0.65, "Install Visual Studio", null));

    Assert.Equal(65, _monitor.Resources.Single(x => x.ResourceId == "visual-studio").Percent);
    Assert.Equal("Install Visual Studio", _monitor.Resources.Single(x => x.ResourceId == "visual-studio").Message);
    Assert.Single(_monitor.LogEntries);
}

[Fact]
public async Task RetryFailedAsync_UsesRunServiceRetryInsteadOfReplayingStep()
{
    await _monitor.RetryFailedAsync.ExecuteAsync(null);

    _runService.Verify(x => x.RetryAsync(_runId, It.Is<IReadOnlySet<string>>(ids => ids.SetEquals(["git"])),
        It.IsAny<CancellationToken>()), Times.Once);
}
```

- [ ] **Step 2: Run monitor tests**

Run: `dotnet test tests\Wdem.Desktop.Tests\Wdem.Desktop.Tests.csproj --filter FullyQualifiedName~ExecutionMonitorViewModelTests --no-restore`

Expected: FAIL because plan/monitor view models and run event subscription are absent.

- [ ] **Step 3: Implement plan review, bounded live event stream, and controls**

Make `IRunEventSink` expose a subscription API returning `IDisposable`; `RunEvent` contains run ID, monotonically increasing sequence, timestamp, kind, optional resource/step ID, progress, message, and optional structured error. `JsonExecutionRunStore` remains the source of truth; the event sink publishes after durable save. The desktop adapter subscribes and marshals events through `DispatcherQueue.TryEnqueue`.

`PlanViewModel` invokes `InspectAsync` to render plan layers, resource action, provider, privilege, restart policy, dependencies, and non-executable errors before allowing Apply. `ExecutionMonitorViewModel` starts `ApplyAsync` in an awaited background task, exposes total progress, elapsed duration, current resource, resource/step state, cancel command, retry-failed command, error-detail selection, and restart requirement. Its log collection keeps the newest 5,000 redacted rows; bind the list with `VirtualizingStackPanel.IsVirtualizing="True"` and `ScrollViewer.CanContentScroll="True"`.

`CancelCommand` cancels only the current run’s `CancellationTokenSource`; it remains enabled until terminal completion. `RetryFailedAsync` calls `IEnvironmentRunService.RetryAsync` with failed resource IDs, thereby enforcing Detect/Plan before applying again. No UI code runs providers or installer processes directly.

- [ ] **Step 4: Run view-model tests**

Run: `dotnet test tests\Wdem.Desktop.Tests\Wdem.Desktop.Tests.csproj --filter FullyQualifiedName~ExecutionMonitorViewModelTests --no-restore`

Expected: PASS; event data updates monitoring state, logs are bounded, cancellation reaches the coordinator, and retry delegates to the fresh-run API.

- [ ] **Step 5: Commit plan and monitoring UI**

```powershell
git add src\Wdem.Core\Runs\IRunEventSink.cs src\Wdem.Windows\Composition\WdemWindowsFactory.cs src\Wdem.Desktop\ViewModels src\Wdem.Desktop\Views tests\Wdem.Desktop.Tests\ViewModels\ExecutionMonitorViewModelTests.cs
git commit -m "feat(wdem): monitor execution in real time"
```

### Task 21: Complete runs and export redacted reports from GUI and CLI

**Files:**
- Create: `src\Wdem.Core\Reporting\IRunReportExporter.cs`
- Create: `src\Wdem.Core\Reporting\RunReportExporter.cs`
- Create: `src\Wdem.Desktop\ViewModels\CompletionViewModel.cs`
- Create: `src\Wdem.Desktop\Views\CompletionView.xaml`
- Create: `tests\Wdem.Core.Tests\Reporting\RunReportExporterTests.cs`
- Create: `tests\Wdem.Desktop.Tests\ViewModels\CompletionViewModelTests.cs`
- Modify: `src\Wdem.Cli\WdemCliBuilder.cs`
- Modify: `src\Wdem.Cli\WdemCommandHandler.cs`

- [ ] **Step 1: Write failing report and completion-summary tests**

```csharp
[Fact]
public void ExportMarkdown_ListsEveryTerminalCategoryAndNeverLeaksToken()
{
    var markdown = _exporter.ExportMarkdown(RunWithSatisfiedSucceededFailedBlockedAndRestart());

    Assert.Contains("Satisfied: 1", markdown, StringComparison.Ordinal);
    Assert.Contains("Failed: 1", markdown, StringComparison.Ordinal);
    Assert.Contains("Blocked: 1", markdown, StringComparison.Ordinal);
    Assert.Contains("Restart required", markdown, StringComparison.Ordinal);
    Assert.DoesNotContain("super-secret-token", markdown, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run exporter tests**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~RunReportExporterTests --no-restore`

Expected: FAIL because report exporter and completion view model are absent.

- [ ] **Step 3: Implement report formats and completion navigation**

`IRunReportExporter` exports one `ExecutionRun` as redacted JSON and Markdown. The Markdown report contains profile/version/run ID/timestamps/machine facts, selected options, graph/plan summary, resource state/outcome/compliance, detected-before/after versions, step exit codes, structured error summaries/details/suggested actions, blocked/unexecuted IDs, and restart requirements. Use `LogRedactor` on all user-visible text. Write report files atomically.

`CompletionViewModel` groups terminal resources into Satisfied (`NotRequired`), Succeeded, Failed, Blocked, Cancelled/Skipped, and Restart Required. Its heading is exactly `C# Developer Environment Ready` only when no selected resource failed/blocked/cancelled; otherwise use `Environment Partially Configured`. `CompletionView.xaml` uses the Windows App SDK file picker initialized with the current window handle for `.json` and `.md`, provides a link back to plan/profile selection, and never restarts automatically. Add `[--report <file>]` to each completed WDEM CLI command and wire it through the same exporter.

- [ ] **Step 4: Run report and completion tests**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~RunReportExporterTests --no-restore && dotnet test tests\Wdem.Desktop.Tests\Wdem.Desktop.Tests.csproj --filter FullyQualifiedName~CompletionViewModelTests --no-restore`

Expected: PASS; reports enumerate all required terminal categories and completion UI uses the full outcome model.

- [ ] **Step 5: Commit completion and reports**

```powershell
git add src\Wdem.Core\Reporting src\Wdem.Desktop\ViewModels\CompletionViewModel.cs src\Wdem.Desktop\Views\CompletionView.xaml src\Wdem.Cli\WdemCliBuilder.cs src\Wdem.Cli\WdemCommandHandler.cs tests\Wdem.Core.Tests\Reporting tests\Wdem.Desktop.Tests\ViewModels\CompletionViewModelTests.cs
git commit -m "feat(wdem): export completion reports"
```

### Task 22: Complete WDEM documentation, CI, provenance, and product-only release

**Files:**
- Create: `testing\wdem\assert-product-identity.ps1`
- Create: `docs\wdem\getting-started.md`
- Create: `docs\wdem\profile-authoring.md`
- Create: `docs\wdem\recovery-and-security.md`
- Modify: `README.md`, `index.md`, `toc.yml`, `docfx.json`, `CHANGELOG.md`, `RELEASE_NOTES.md`, `CONTRIBUTING.md`, `SECURITY.md`, and every tracked user-facing `docs\**\*.md`
- Modify: `.github\ISSUE_TEMPLATE.md`, `.github\ISSUE_TEMPLATE\*.yml`, `.github\PULL_REQUEST_TEMPLATE.md`, `.github\dependabot.yml`, `.github\workflows\ci.yml`, `.github\workflows\docs.yml`, and `.github\workflows\release.yaml`
- Modify: `docs\toc.yml`
- Modify: `tests\Wdem.Core.Tests\Profiles\ProfileCatalogTests.cs`

- [ ] **Step 1: Write failing product-identity, documentation, and release-layout checks**

Add this profile assertion to `tests\Wdem.Core.Tests\Profiles\ProfileCatalogTests.cs`:

```csharp
[Fact]
public async Task LoadAsync_ShippedCSharpProfile_HasTheCompleteMvpResourceSet()
{
    var result = await CreateProductionCatalog().LoadAsync("csharp-developer", CancellationToken.None);

    Assert.True(result.IsValid);
    Assert.Equal(["visual-studio", "dotnet-sdk", "git"],
        result.Profile!.RequiredResources.Select(x => x.Id));
    Assert.Equal(["resharper", "resharper-settings", "company-vs-extension", "visual-studio-settings"],
        result.Profile.OptionalResources.Select(x => x.Id));
    Assert.Equal(["visual-studio"], result.Profile.Resources["resharper"].Dependencies);
    Assert.Equal(["resharper"], result.Profile.Resources["resharper-settings"].Dependencies);
    Assert.Equal(["visual-studio"], result.Profile.Resources["company-vs-extension"].Dependencies);
    Assert.Equal(["visual-studio"], result.Profile.Resources["visual-studio-settings"].Dependencies);
}
```

Create `testing\wdem\assert-product-identity.ps1`:

```powershell
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
$release = Get-Content (Join-Path $root '.github\workflows\release.yaml') -Raw
foreach ($executable in 'Wdem.Cli.exe', 'Wdem.Desktop.exe', 'Wdem.ElevatedHost.exe') {
    if ($release -notmatch [regex]::Escape($executable)) { throw "Release workflow does not publish $executable." }
}
foreach ($required in 'Wdem.sln', 'Wdem-win-x64.zip', 'SHA256SUMS.txt', 'THIRD-PARTY-NOTICES.md', 'WindowsAppSDKSelfContained') {
    if ($release -notmatch [regex]::Escape($required)) { throw "Release workflow does not contain $required." }
}
if ($release -match 'PublishSingleFile[^\r\n]*Wdem\.Desktop') {
    throw 'The WinUI desktop host must not be published as a single-file executable.'
}
foreach ($forbidden in 'WinHome.exe', 'WinHome.sln', 'src\WinHome.csproj') {
    if ($release -match [regex]::Escape($forbidden)) { throw "Release workflow must not contain $forbidden." }
}
$allowed = @('THIRD-PARTY-NOTICES.md', 'docs/wdem/source-provenance.md', 'docs/superpowers/plans/2026-08-28-wdem-complete-product.md')
$matches = @(git -C $root grep -Il -e WinHome -e 'DotDev262/WinHome' -- '*.md' '*.yml' '*.yaml')
$unexpected = $matches | Where-Object { $_ -notin $allowed }
if ($unexpected) { throw "User-facing branding is not fully WDEM: $($unexpected -join ', ')" }
```

- [ ] **Step 2: Run the checks and confirm they fail before branding and workflow migration**

Run: `dotnet test tests\Wdem.Core.Tests\Wdem.Core.Tests.csproj --filter FullyQualifiedName~ProfileCatalogTests --no-restore && powershell -ExecutionPolicy Bypass -File testing\wdem\assert-product-identity.ps1`

Expected: the profile test passes after Task 18; the identity script FAILS because workflows and user-facing documentation still name WinHome or publish its executable.

- [ ] **Step 3: Finish the product-wide WDEM migration and restrict release artifacts**

Write the WDEM docs with these non-negotiable product statements:

- `getting-started.md` verifies and extracts `Wdem-win-x64.zip`, then runs `Desktop\Wdem.Desktop.exe` or `Cli\Wdem.Cli.exe`; it uses `%LOCALAPPDATA%\WDEM`, describes `WDEM_*` inputs only, explains that every file in the extracted archive must be retained, and documents the one-time import of old `%LOCALAPPDATA%\WinHome` state as non-authoritative.
- `profile-authoring.md` gives the complete YAML/JSON schema, version syntax, required/optional/automatic dependency behavior, supported resource types, hashes/source requirements, VS instance selection, and the `WDEM_COMPANY_VSIX_PATH` / `WDEM_COMPANY_VSIX_SHA256` contract.
- `recovery-and-security.md` describes the UAC broker, trust/hash/signature policy, redaction, cancellation, restart recovery, the mandatory new Detect/Plan cycle, the one-time state migration marker, and fetch-only WinHome provenance.
- Replace every user-facing product name, repository link, executable, default state path, environment-variable prefix, solution/project identifier, issue template, and CI job label with WDEM. The only allowed WinHome references in Markdown/YAML are the attribution files listed in Step 1; those must state MIT provenance and must not present WinHome as a supported product.

Update CI so its Ubuntu job restores, formats, and builds WDEM; the Windows job tests the WDEM solution and can retain Python/plugin checks only as source-derived validation:

```powershell
dotnet restore Wdem.sln
dotnet format --verify-no-changes --verbosity diagnostic Wdem.sln
dotnet build Wdem.sln --no-restore -p:EnableWindowsTargeting=true
dotnet test Wdem.sln --no-restore --verbosity normal --collect "XPlat Code Coverage"
```

Replace the release workflow with product-only publishing. It must not call `dotnet publish` for `Wdem.LegacySource`. Publish an unpackaged, self-contained WinUI 3 distribution: do not request `PublishSingleFile` for `Wdem.Desktop`, because Windows App SDK framework/bootstrapper files must remain beside the executable. The release asset list contains the distribution ZIP, its SHA-256 checksum file, and `THIRD-PARTY-NOTICES.md`:

```powershell
$root = Join-Path $PWD 'staging\WDEM'
$publish = Join-Path $PWD 'publish'
New-Item -ItemType Directory -Force -Path $root, $publish | Out-Null

dotnet publish src\Wdem.Cli\Wdem.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $root 'Cli')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed: Wdem.Cli' }
dotnet publish src\Wdem.Desktop\Wdem.Desktop.csproj -c Release -r win-x64 --self-contained true -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -o (Join-Path $root 'Desktop')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed: Wdem.Desktop' }
dotnet publish src\Wdem.ElevatedHost\Wdem.ElevatedHost.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $root 'ElevatedHost')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed: Wdem.ElevatedHost' }

$expected = @(
    (Join-Path $root 'Cli\Wdem.Cli.exe'),
    (Join-Path $root 'Desktop\Wdem.Desktop.exe'),
    (Join-Path $root 'ElevatedHost\Wdem.ElevatedHost.exe')
)
foreach ($executable in $expected) {
    if (-not (Test-Path $executable -PathType Leaf)) { throw "Missing product host: $executable" }
}
Copy-Item THIRD-PARTY-NOTICES.md (Join-Path $root 'THIRD-PARTY-NOTICES.md')
$archive = Join-Path $publish 'Wdem-win-x64.zip'
Compress-Archive -Path (Join-Path $root '*') -DestinationPath $archive -Force
Get-FileHash $archive -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  Wdem-win-x64.zip" } |
    Set-Content (Join-Path $publish 'SHA256SUMS.txt') -Encoding ascii
Copy-Item THIRD-PARTY-NOTICES.md (Join-Path $publish 'THIRD-PARTY-NOTICES.md')
```

Configure the release action to upload only `publish\Wdem-win-x64.zip`, `publish\SHA256SUMS.txt`, and `publish\THIRD-PARTY-NOTICES.md`. It must have no loose executable assets, no `WinHome.exe`, no `WinHome.sln`, and no link to a WinHome release. The archive must retain the `Cli`, `Desktop`, and `ElevatedHost` directories exactly as published.

- [ ] **Step 4: Run format, build, product-identity, and release-layout validation**

Run:

```powershell
dotnet format --verify-no-changes --verbosity diagnostic Wdem.sln
dotnet build Wdem.sln --no-restore -p:EnableWindowsTargeting=true
dotnet test Wdem.sln --no-restore --verbosity normal
powershell -ExecutionPolicy Bypass -File testing\wdem\assert-product-identity.ps1
```

Expected: all commands exit `0`; every solution/project/namespace and user-facing document is WDEM-branded except explicit MIT attribution; Windows and Ubuntu CI use `Wdem.sln`; and release configuration produces only the self-contained `Wdem-win-x64.zip`, `SHA256SUMS.txt`, and `THIRD-PARTY-NOTICES.md` assets. The ZIP contains the three WDEM product hosts and no transition-source executable.

- [ ] **Step 5: Commit documentation and product-only automation**

```powershell
git add README.md index.md toc.yml docfx.json CHANGELOG.md RELEASE_NOTES.md CONTRIBUTING.md SECURITY.md THIRD-PARTY-NOTICES.md docs .github testing\wdem\assert-product-identity.ps1 tests\Wdem.Core.Tests\Profiles\ProfileCatalogTests.cs
git commit -m "docs(wdem): complete independent product identity"
```

### Task 23: Run final WDEM automated and clean-machine acceptance

**Files:**
- Create: `testing\wdem\acceptance-checklist.md`
- Create: `testing\wdem\inspect-smoke.ps1`
- Create: `testing\wdem\clean-vm-apply.ps1`
- Modify: `docs\wdem\getting-started.md`

- [ ] **Step 1: Write the acceptance matrix and non-mutating smoke script before execution**

Create `testing\wdem\acceptance-checklist.md` with this acceptance matrix:

| Area | Procedure | Required result |
|---|---|---|
| Repository identity | Run `powershell -ExecutionPolicy Bypass -File testing\wdem\assert-product-identity.ps1` | `Wdem.sln`, WDEM docs/CI, MIT notice, and only permitted WDEM release executables pass; provenance remotes have `DISABLED` push URLs. |
| State and inputs | Inspect with `WDEM_COMPANY_VSIX_PATH`/`WDEM_COMPANY_VSIX_SHA256` unset and the optional VSIX unselected | State/logs use `%LOCALAPPDATA%\WDEM`; no `WinHome` directory is written; unresolved optional WDEM variables do not fail inspection. |
| One-time migration | Place a representative old state under `%LOCALAPPDATA%\WinHome`, then start WDEM twice | First start writes `%LOCALAPPDATA%\WDEM\migration-v1.json`; second start does not reread or write the old path; imported state is never treated as compliance. |
| Inspect safety | Run the smoke script | JSON report is redacted and no install, registry, environment, or restart operation occurs. |
| Desktop | Complete Inspect-only workflow in `Wdem.Desktop.exe` | WDEM title, plan, monitor, completion, and report-save controls appear; no source CLI is launched. |
| Clean VM apply | Run the confirmed script on Windows 11 x64 snapshot | One UAC host, verified selected resources, actionable refusal/recovery, and no `WinHome.exe` artifact. |

Create `testing\wdem\inspect-smoke.ps1`:

```powershell
$ErrorActionPreference = 'Stop'
$report = Join-Path $PSScriptRoot 'inspect-report.json'
Remove-Item $report -ErrorAction SilentlyContinue
dotnet run --project src\Wdem.Cli\Wdem.Cli.csproj -- inspect --profile profiles\csharp-developer.yaml --json --report $report
if ($LASTEXITCODE -ne 0) { throw "WDEM inspect failed with exit code $LASTEXITCODE." }
$run = Get-Content $report -Raw | ConvertFrom-Json
if ($run.mode -ne 'Inspect') { throw 'Expected Inspect report mode.' }
if (-not $run.resourceResults.git) { throw 'Git result was not reported.' }
if ((Get-Content $report -Raw) -match '(?i)authorization:\s*bearer|password=|token=') { throw 'Inspect report is not redacted.' }
if (Test-Path (Join-Path $env:LOCALAPPDATA 'WinHome\Wdem\runs')) { throw 'WDEM wrote the retired state path.' }
```

- [ ] **Step 2: Run the acceptance artifacts and confirm missing scripts fail before implementation**

Run: `powershell -ExecutionPolicy Bypass -File testing\wdem\inspect-smoke.ps1`

Expected: FAIL because the WDEM CLI, report export, and acceptance script do not yet all exist. Do not execute an Apply operation on a developer workstation.

- [ ] **Step 3: Write an explicitly gated clean-VM Apply script**

Create `testing\wdem\clean-vm-apply.ps1`:

```powershell
[CmdletBinding()]
param([switch]$Confirmed)
$ErrorActionPreference = 'Stop'
if (-not $Confirmed) { throw 'Refusing to apply outside an explicitly confirmed disposable VM.' }
$os = Get-CimInstance Win32_OperatingSystem
if ($os.Caption -notmatch 'Windows 11' -or [Environment]::Is64BitOperatingSystem -ne $true) {
    throw 'This acceptance script requires a Windows 11 x64 VM.'
}
$root = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
$cli = Join-Path $root 'publish\Wdem.Cli\Wdem.Cli.exe'
if (-not (Test-Path $cli)) { throw "Published Wdem.Cli.exe not found at $cli." }
$work = Join-Path $PSScriptRoot 'clean-vm-work'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$profile = Join-Path $work 'csharp-developer.yaml'
$report = Join-Path $work 'apply-report.json'
Copy-Item (Join-Path $root 'profiles\csharp-developer.yaml') $profile -Force
& $cli apply --profile $profile --json --report $report
if ($LASTEXITCODE -ne 0) { throw "WDEM apply failed with exit code $LASTEXITCODE." }
$run = Get-Content $report -Raw | ConvertFrom-Json
if ($run.mode -ne 'Apply') { throw 'Expected Apply report mode.' }
if (-not $run.resourceResults.git -or -not $run.resourceResults.'dotnet-sdk') { throw 'Required resource results were not reported.' }
if (Get-ChildItem (Join-Path $root 'publish') -Recurse -Filter 'WinHome.exe' -ErrorAction SilentlyContinue) {
    throw 'A retired executable was included in the release layout.'
}
```

Update `docs\wdem\getting-started.md` to require a disposable Windows 11 x64 VM snapshot and the `-Confirmed` switch for this script; it must state that it uses the WDEM product release and has no upstream merge/PR workflow.

- [ ] **Step 4: Run all automated validation and the desktop inspection path**

Run:

```powershell
dotnet restore Wdem.sln
dotnet format --verify-no-changes --verbosity diagnostic Wdem.sln
dotnet build Wdem.sln --no-restore -p:EnableWindowsTargeting=true
dotnet test Wdem.sln --no-restore --verbosity normal --collect "XPlat Code Coverage"
powershell -ExecutionPolicy Bypass -File testing\wdem\assert-product-identity.ps1
powershell -ExecutionPolicy Bypass -File testing\wdem\inspect-smoke.ps1
dotnet run --project src\Wdem.Desktop\Wdem.Desktop.csproj
```

Expected: every command exits `0`; the desktop opens a responsive `WDEM` window with C# Developer selection, grouped required/optional resources, `检查环境`, and `开始配置`. Complete Inspect only, confirm plan/monitor/completion/live-redacted-log/report-save controls, then close without applying.

- [ ] **Step 5: Execute documented clean-VM P1 acceptance and commit assets**

On a fresh, snapshotted Windows 11 x64 VM, run:

```powershell
powershell -ExecutionPolicy Bypass -File testing\wdem\clean-vm-apply.ps1 -Confirmed
```

Expected: one UAC prompt serves privileged Visual Studio/extension operations; Git/.NET/Visual Studio are verified after installation; selected workload/component/`.vsconfig`, ReSharper/`.DotSettings`, trusted VSIX, and `.vssettings` attain final compliance; rejected elevation yields `PermissionError` and blocks dependents; forced restart/abnormal termination is recoverable only after a fresh Detect/Plan cycle. Restore the VM snapshot after recording the acceptance matrix.

```powershell
git add testing\wdem docs\wdem\getting-started.md
git commit -m "test(wdem): add product acceptance checks"
```

## Plan self-review

| Requirement | Covered by |
|---|---|
| Independent private `JasonLiCSHI/WDEM` identity; no upstream merge/PR; fetch-only, push-disabled provenance remotes | Task 1, Task 22, Task 23 |
| MIT source attribution and retained upstream copyright | Task 1, Task 22 |
| WDEM solution/project/assembly/root namespaces and retired source executable | Tasks 1–2, Task 22 |
| WDEM default `%LOCALAPPDATA%\WDEM` state, `WDEM_*` inputs, and one-time old-state migration | Tasks 4, 8, 11, 18, 22–23 |
| Exact/wildcard/range/minimum version constraints and preferred versions | Tasks 3, 6, 12 |
| Required/optional/automatic dependency selection; global DAG/cycle path/layers | Tasks 4–5 |
| YAML/JSON schema plus semantic provider/parameter validation | Task 4 |
| Detect/compliance/Inspect prohibition on modifications | Tasks 6, 10, 12 |
| Plan, independent state/outcome, scheduler, blocked/cancel/retry | Tasks 3, 7, 9–10 |
| Run/resource/step/error/log/persistence/reports | Tasks 3, 8, 21 |
| Exit/restart recovery with mandatory fresh Detect/Plan | Tasks 8, 10, 15, 23 |
| Provider SDK and temporary MIT-derived transition adapters | Tasks 6, 11 |
| WinGet, Git, .NET SDK | Task 12 |
| Visual Studio, VSIX, ReSharper, `.DotSettings`, `.vssettings`, hashes, and UAC | Tasks 14–18 |
| WinUI 3 profile/resource/plan/monitor/completion workflow | Tasks 19–21 |
| WDEM-only CLI, docs, CI, solution commands, and self-contained WinUI release archive (`Wdem-win-x64.zip` with the three product hosts) | Tasks 13, 22–23 |

Self-check completed: the plan contains 23 ordered checkbox tasks; product identity migration precedes all new hosts; WDEM is the sole release brand and command contract; every intentional WinHome mention is constrained to migration, provenance, or MIT attribution; no task treats upstream mergeability or legacy CLI compatibility as a hard boundary.
