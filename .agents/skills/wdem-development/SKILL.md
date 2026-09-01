---
name: wdem-development
description: Develop, diagnose, review, test, package, or release the WDEM declarative Windows environment manager. Use this skill whenever work in the WDEM repository mentions Profiles, Profile Sources, Task DAGs, workflow/state machines, Activities, WPF, CLI, installer scripts, version detection, administrator access, safe cancellation, logging, packaging, or GitHub releases—even when the user does not explicitly ask for the skill.
compatibility: Windows, PowerShell, .NET 10 SDK; Inno Setup 6 is required only for installer builds.
---

# WDEM development

Keep every change aligned with WDEM's central model:

```text
Profile Source → Profile → selected Tasks → validated DAG → Task workflow → report
```

Visual Studio, ReSharper, and future products remain ordinary declarative Tasks. The state machine owns runtime state; GUI and CLI react to projected Task state and capabilities.

## Start with repository context

1. Read [`../../../AGENTS.md`](../../../AGENTS.md) completely.
2. Read [`../../../CONTEXT.md`](../../../CONTEXT.md) completely.
3. For architecture or runtime work, read [`../../../docs/ARCHITECTURE.md`](../../../docs/ARCHITECTURE.md).
4. For scope or behavior decisions, read [`../../../docs/MVP_REQUIREMENTS.md`](../../../docs/MVP_REQUIREMENTS.md).
5. Inspect `git status --short --branch`. Preserve unrelated user changes and generated-file exclusions.

Treat a request to explain or diagnose as read-only. Modify code, external systems, releases, branches, or repository settings only when the user asks for that action.

## Route changes to the right layer

| Concern | Owner | Rule |
| --- | --- | --- |
| Profile parsing, versions, DAG, planning, state machine, reports | `Wdem.Core` | Keep platform and product details out |
| Windows commands, process trees, logs, settings, trust persistence, administrator check | `Wdem.Windows` | Share behavior between both clients |
| Task presentation and interaction | `Wdem.App` | Bind to projected Task state/capabilities |
| Terminal interaction | `Wdem.Cli` | Consume the same Core and Windows reports |
| Product installation/configuration | `profiles/`, `script/`, `settings/` | Do not add product-specific Core providers |
| Packaging | `build/`, `installer/`, GitHub Actions | Package scripts/settings, never Profiles |

If GUI and CLI need the same decision, place it in Core or Windows and make both clients consume it. Do not reproduce state graphs, DAG rules, compliance decisions, or capability logic in a client.

## Preserve workflow semantics

- Compile Schema v1 into `Detect → Pre → Apply → Post → Verify`.
- Allow Schema v2 only through its validated bounded state graph. States can run Entry, Residence, and Exit Activities.
- Let workflow states project stable `TaskExecutionState` and capabilities. UI buttons must bind to those capabilities instead of interpreting state IDs.
- Re-detect before Apply. Skip Tasks already satisfying their version constraint.
- Execute dependencies before dependents. An upstream failure or cancellation blocks unsafe downstream work.
- Start retries from a fresh workflow at its declared initial state.
- Treat Detect's “not installed” result as compliance information when the workflow can install the Task; do not confuse it with an Apply failure.

## Keep commands and cancellation safe

- Model every command as an executable plus an argument array. Never concatenate a shell command string.
- Pass cancellation through Core into the Windows process runner.
- Keep installers in the launched process tree. Do not detach them or fire-and-forget.
- On cancellation, terminate the current process tree, wait for termination, and prevent subsequent Activities and dependent Tasks from starting.
- Preserve stdout, stderr, exit code, Activity identity, runtime state, and timestamps in progress events and JSONL logs.
- Require explicit content-hash trust before Remote or Cache Profile commands execute.
- Require an elevated administrator token before either client loads or runs Tasks. Keep the check in `Wdem.Windows`; clients only present the result.

## Write robust Windows installer Activities

PowerShell Task scripts use strict mode, so avoid implicit automatic-variable assumptions.

For GUI-subsystem installers, use an explicit process handle:

```powershell
$installerProcess = Start-Process `
    -FilePath $installerPath `
    -ArgumentList $installerArguments `
    -NoNewWindow `
    -Wait `
    -PassThru
$installerExitCode = $installerProcess.ExitCode
```

Do not read `$LASTEXITCODE` after launching a GUI installer. Windows PowerShell 5.1 may return without defining or updating it, which can either fail strict mode or falsely reuse an earlier command's exit code.

Handle the installer's documented success codes explicitly. WDEM currently treats `0`, `1641`, and `3010` as successful, with the latter two requiring restart. Keep source-host validation, file hashes where available, preflight checks, and temporary-file cleanup.

## Diagnose from evidence

When a run fails:

1. Read the newest JSONL session log and isolate the Task, workflow state, Activity, command, exit code, stderr, and timestamp.
2. Separate expected Detect non-compliance from the Activity that actually failed.
3. Correlate the Activity with its Profile command and script.
4. Build a deterministic regression test at the real boundary before changing behavior.
5. Confirm dependency outcomes: upstream failure should produce `Blocked`, not accidental execution.

For installer-process regressions, follow the existing harness in `tests/Wdem.Windows.Tests/DefaultProcessRunnerTests.cs`: replace the download with a generated fake GUI executable, assign a nonzero exit code, and assert that the Task reports that exact code without touching the network or machine installation state.

## Validate changes

Add focused tests in the owning test project. At minimum, run:

```powershell
dotnet test Wdem.slnx
dotnet build Wdem.slnx
git diff --check
git status --short
```

Also re-run the smallest regression test that reproduced the reported issue. Confirm cancellation tests whenever process creation or waiting changes. Keep `bin/`, `obj/`, `artifacts/`, logs, caches, and user settings out of source control.

## Package and release deliberately

- The installer includes `script/` and `settings/` beside both executables.
- The installer never bundles `profiles/`; Profiles load remote-first and use only the last-known-good cache locally.
- The release selects one HTTPS Profile Source in code. GUI and CLI do not expose Source editing.
- Building an installer requires Inno Setup 6 in addition to the .NET SDK.
- Do not commit, push, merge, tag, replace a release asset, or delete a branch unless the user explicitly requests it.
- After replacing an installer, report its version, asset name, SHA-256, and CI result.

## Report the result

Lead with the observable outcome. Include:

- what behavior changed;
- the important files or architectural layer affected;
- exact tests/builds run and their result;
- whether an installed package or published release still needs replacement.

If work is incomplete, distinguish a code defect from an environmental prerequisite or missing authority.
