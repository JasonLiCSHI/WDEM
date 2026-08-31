# WDEM contributor guide

## Product boundary

- WDEM is a declarative Windows environment manager built around Profile, Task DAG, and Workflow.
- Visual Studio, ReSharper, and every other product are ordinary Profile Tasks. Do not add product-specific providers to Core.
- CLI and WPF must share `Wdem.Core` and `Wdem.Windows`; business rules must not be duplicated in either UI.
- MVP commands are direct executable plus argument arrays. Do not introduce shell command-string concatenation.

## Required behavior

- The release selects one HTTPS Profile Source in code; GUI and CLI do not expose Source editing or selection.
- Profiles are fetched remote-first and only the last-known-good cache is local; the installer must not bundle `profiles/`.
- Remote Profiles require explicit user trust before Detect or Apply executes commands.
- Schema v1 uses the default Detect, Pre, Apply, Post, Verify workflow. Schema v2 may compose a bounded state graph whose states run Entry, Residence, and Exit Activities. A retry creates a fresh workflow at its declared initial state.
- Runtime state belongs to the Core state machine and projects stable Task state and capabilities to CLI and WPF; clients must not interpret or recreate the state graph.
- Cancellation must stop the current process tree and prevent unsafe downstream execution.

## Validation

- Add focused tests for Core behavior and Windows process/runtime behavior.
- Run `dotnet test Wdem.slnx` and `dotnet build Wdem.slnx` before completion.
- Keep generated `bin/`, `obj/`, logs, and user configuration out of source control.
