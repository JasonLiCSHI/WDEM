> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Windows Sandbox Plugin

## Overview

The Windows Sandbox plugin manages Windows Sandbox configuration using a `.wsb` configuration file.

## Prerequisites

- Windows Sandbox installed
- Windows Documents folder access

## Configuration Schema

| Key | Purpose |
|------|--------|
| vGPU | Enable or disable virtual GPU support |
| networking | Enable or disable networking |
| audioInput | Enable or disable audio input |
| videoInput | Enable or disable video input |
| protectedClient | Enable or disable protected client mode |
| printerRedirection | Enable or disable printer redirection |
| clipboardRedirection | Enable or disable clipboard redirection |
| memoryInMB | Configure memory allocation in megabytes |
| mappedFolders | Configure folders shared with the sandbox |

## Usage Examples

### Basic configuration

```yaml
plugins:
  - name: windows-sandbox
    settings:
      vGPU: true
      networking: true
      memoryInMB: 2048
```

### Mapped folders

```yaml
plugins:
  - name: windows-sandbox
    settings:
      mappedFolders:
        - hostFolder: "C:\\Projects"
          readOnly: true
```

## Verification Steps

```powershell
Test-Path "$env:USERPROFILE\Documents\sandbox.wsb"
```

Verify that `sandbox.wsb` exists and contains the expected settings.

## Notes / Caveats

- Configuration is stored in `Documents\sandbox.wsb`.
- Default settings are created automatically if no configuration exists.
- Dry-run previews are supported.
- Settings must be provided as a dictionary.
- Configuration updates are written atomically.
