# WDEM product acceptance checklist

Use this matrix for the tagged WDEM source and the three assets from the matching
WDEM product release. Automated results may be recorded on a development machine;
Apply and destructive recovery exercises require a fresh, disposable VM snapshot.

| Area | Procedure | Required result | Evidence |
|---|---|---|---|
| Repository identity | Run `powershell -ExecutionPolicy Bypass -File testing\wdem\assert-product-identity.ps1`. Verify `git remote get-url --push winhome-source` and `git remote get-url --push winhome-fork` both return `DISABLED`. | `Wdem.sln`, WDEM docs and CI, the MIT notice, and only `Wdem.Cli.exe`, `Wdem.Desktop.exe`, and `Wdem.ElevatedHost.exe` in the product hosts pass. The provenance remotes' push URLs are `DISABLED`; `origin` remains the independent WDEM repository. | Record the command output and tagged commit. |
| State and inputs | Unset `WDEM_COMPANY_VSIX_PATH` and `WDEM_COMPANY_VSIX_SHA256`, leave `company-vs-extension` unselected, and run `inspect-smoke.ps1`. | State and redacted logs are below `%LOCALAPPDATA%\WDEM`, the retired `%LOCALAPPDATA%\WinHome` tree is not written, and unresolved variables belonging only to the optional VSIX do not fail Inspect. | Record the smoke result and `%LOCALAPPDATA%\WDEM` tree. The smoke report itself is temporary and removed. |
| One-time migration | On a disposable test account, place representative legacy state below `%LOCALAPPDATA%\WinHome`, start WDEM, close it, and start it a second time. | The first start writes `%LOCALAPPDATA%\WDEM\migration-v1.json`. The second start neither rereads nor writes the old path. Imported state is historical context only and is never accepted as compliance; Detect and Plan run from current machine state. | Manual evidence required: file timestamps, both redacted logs, and both reports. |
| Inspect safety | Run `powershell -ExecutionPolicy Bypass -File testing\wdem\inspect-smoke.ps1`. | The JSON report is redacted. Runtime results contain no executed install/configure steps or post-Apply detection, restart requirements are empty, persistent Registry and Environment fingerprints do not change, and the machine boot time does not change. | Record the passing smoke output. |
| Desktop | Start `Desktop\Wdem.Desktop.exe` from the extracted release and complete an Inspect-only workflow. | The window title identifies WDEM; selection, plan, monitor, completion, live redacted log, and report-save controls work. No Apply is started and no transition-source CLI process is launched. | **NOT EXECUTED in automated validation.** Clean-machine manual evidence is required: screenshots, saved redacted report, and process list. |
| Clean VM apply | Restore a fresh Windows 11 x64 snapshot, configure trusted optional inputs, and run `powershell -ExecutionPolicy Bypass -File testing\wdem\clean-vm-apply.ps1 -Confirmed`. | Exactly one UAC elevated host serves privileged operations; Git, .NET SDK, Visual Studio workload/components/`.vsconfig`, ReSharper/`.DotSettings`, trusted VSIX, and `.vssettings` reach verified final compliance. The release layout contains no `WinHome.exe`. | **NOT EXECUTED on a development machine.** External clean-machine manual evidence is required. |

## Clean-VM P1 evidence

On the snapshotted Windows 11 x64 VM, also record these controlled failure and
recovery cases:

- Reject the UAC request. The affected resource must report an actionable
  `PermissionError`, its dependents must be blocked, and no silent fallback may
  apply privileged work.
- Exercise a required restart and a forced termination. Resume/retry must use a
  fresh Detect and a fresh Plan; a stale plan must not be replayed.
- Save the redacted JSON reports, UAC count, selected resource list, and product
  layout listing with the tagged release identity.
- Restore the snapshot after evidence is copied out. Do not reuse the machine as
  evidence for another release.

The clean-VM P1 run is deliberately not executed by automation or on a developer
workstation. Its result remains **NOT EXECUTED** until an operator attaches the
manual evidence above.
