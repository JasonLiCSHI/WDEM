# Author WDEM developer profiles

WDEM accepts UTF-8 YAML (`.yaml`) and JSON (`.json`) profiles. The
document is validated before any provider runs. Unknown fields, missing
resources, cycles, duplicate references, unsafe IDs, and unsupported providers
are errors.

## Complete document shape

This YAML contains every schema field. JSON uses the same property names and
types; YAML mappings become JSON objects and YAML sequences become JSON arrays.

```yaml
schemaVersion: "1.0"                 # currently exactly 1.0
profile:
  id: example-developer              # non-empty, portable resource-style ID
  version: 1.0.0                     # semantic version: 1 to 4 numeric parts
  displayName: Example Developer
  description: Example WDEM profile
  requiredResources:
    - id: git
      versionConstraint: ">= 2.40.0 < 3.0.0"
      preferredVersion: 2.48.1
      defaultSelected: false
  optionalResources:
    - id: editor
      versionConstraint: 1.95.x
      preferredVersion: 1.95.3
      defaultSelected: true
resources:
  git:
    type: git
    provider: winget
    versionConstraint: ">= 2.40.0"
    preferredVersion: 2.48.1
    privilegeRequirement: CurrentUser # CurrentUser or Administrator
    restartPolicy: NoRestart           # NoRestart, RestartRecommended, RestartRequired
    dependsOn: []
    parameters: {}
  editor:
    type: winget-package
    provider: winget
    dependsOn: [git]
    parameters:
      packageId: Microsoft.VisualStudioCode
      source: winget
```

Equivalent JSON skeleton:

```json
{
  "schemaVersion": "1.0",
  "profile": {
    "id": "example-developer",
    "version": "1.0.0",
    "displayName": "Example Developer",
    "description": "Example WDEM profile",
    "requiredResources": [{ "id": "git" }],
    "optionalResources": [{ "id": "editor", "defaultSelected": true }]
  },
  "resources": {
    "git": { "type": "git", "provider": "winget" },
    "editor": {
      "type": "winget-package",
      "provider": "winget",
      "dependsOn": ["git"],
      "parameters": { "packageId": "Microsoft.VisualStudioCode" }
    }
  }
}
```

`profile.id`, resource keys, and reference IDs must be non-empty portable IDs.
`profile.version` and `preferredVersion` use 1-4 numeric segments such as
`1`, `1.2`, `1.2.3`, or `1.2.3.4`. A `versionConstraint` is one of:

- exact: `= 8.0.100`;
- minimum: `>= 8.0.100`;
- bounded range: `>= 8.0.100 < 9.0.0`; or
- major/minor wildcard: `8.0.x`.

A reference-level version setting overrides the resource-level setting.

## Required, optional, and automatic dependencies

Required resources always enter the graph. Optional resources enter it when
selected or when `defaultSelected: true`. WDEM then automatically includes
every transitive `dependsOn` resource needed by the selected set, even when
that dependency was listed as optional. Dependencies run before their
dependants. A missing dependency or cycle invalidates the whole profile.

## Supported resource types

| `type` / `provider` | Parameters |
| --- | --- |
| `package` / `winget` | `packageId` required; optional `source`; `installerParameters` is rejected when non-empty and versions are not enforceable. |
| `winget-package` / `winget` | `packageId` required; optional `source`. |
| `git` / `winget` | No parameters. |
| `dotnet-sdk` / `winget` | No parameters. |
| `visual-studio` / `visual-studio` | `productId`, `edition`, `channelId`, `workloads`, and `components` required; optional `instanceId`, absolute `installPath`, `vsconfigPath`, `expectedSha256`, `bootstrapperUri`, `bootstrapperSha256`. |
| `resharper` / `winget` | Must depend on Visual Studio; optional `visualStudioResourceId`, `instanceId`, `visualStudioInstanceId`, `productId`, `edition`, `channelId`, and `source`. |
| `visual-studio-extension` / `vsix` | `extensionId`, `sourcePath`, and `expectedSha256` required; must depend on Visual Studio; accepts the same instance selectors plus `visualStudioResourceId`; `privilegeRequirement: Administrator` is mandatory and the provider rejects CurrentUser. |
| `resharper-settings` / `file` | `sourcePath`, `expectedSha256`, and `destinationPath` required; must depend on ReSharper; optional `resharperResourceId`. |
| `visual-studio-settings` / `visual-studio-settings` | `sourcePath`, `expectedSha256`, `settingsStorePath`, `edition`, and `channelId` required; select with `instanceId` or `productId`; must depend on Visual Studio; optional `visualStudioResourceId` and `settingsStoreSha256`. |

Comma- or semicolon-separate Visual Studio workload/component IDs. A
`vsconfigPath` may be absolute or remain below `profiles/assets`; it requires
`expectedSha256`. A custom bootstrapper must be HTTPS and must pair
`bootstrapperUri` with `bootstrapperSha256`.

## Trusted configuration sources

VSIX, `.vsconfig`, `.DotSettings`, and `.vssettings` inputs are content-bound.
Set their expected hash to the 64-hex SHA-256 of the approved bytes. WDEM
refuses a missing, changed, unsafe, redirected, or untrusted source rather than
silently accepting it. Keep repository assets under `profiles/assets`; use an
absolute local path or an approved HTTPS URI only where the provider permits it.

The shipped company extension uses:

```powershell
$env:WDEM_COMPANY_VSIX_PATH = 'C:\Approved\Company.Extension.vsix'
$env:WDEM_COMPANY_VSIX_SHA256 = (Get-FileHash $env:WDEM_COMPANY_VSIX_PATH -Algorithm SHA256).Hash
```

Both variables are required only when `company-vs-extension` is selected. Do
not place a secret in either variable; the hash is an integrity value.

## Visual Studio instance selection

Prefer an exact `instanceId` when a profile targets an existing instance. For
portable profiles, use the tuple `productId`, `edition`, and `channelId`; WDEM
requires a unique complete, launchable match and refuses ambiguity. Dependent
ReSharper, VSIX, and settings resources must select the same instance as their
Visual Studio dependency. `visualStudioInstanceId` is accepted only as an
alias for `instanceId` on ReSharper/VSIX resources and the two values may not
conflict.

Validate with `Wdem.Cli.exe inspect --profile <path>` before requesting Apply.
