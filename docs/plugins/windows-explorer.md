> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Windows Explorer Plugin

## Overview

The Windows Explorer plugin manages Windows Explorer settings through the Windows Registry.

## Prerequisites

- Windows operating system
- Access to the current user's registry settings

## Configuration Schema

| Key | Purpose |
|------|--------|
| HideFileExt | Show or hide file extensions |
| Hidden | Controls hidden file visibility (1 or 2) |
| ShowSuperHidden | Show or hide protected operating system files |
| ShowSyncProviderNotification | Enable or disable sync provider notifications |
| ShowStatusBar | Show or hide the status bar |
| AutoCheckSelect | Enable or disable item check boxes |
| DisableThumbnails | Enable or disable thumbnails |
| DisableThumbsDBOnNetworkFolders | Disable thumbnail cache on network folders |
| SeparateProcess | Launch Explorer windows in separate processes |

## Usage Examples

### Show file extensions

```yaml
plugins:
  - name: windows-explorer
    settings:
      HideFileExt: false
```

### Show hidden files

```yaml
plugins:
  - name: windows-explorer
    settings:
      Hidden: 1
      ShowSuperHidden: true
```

## Verification Steps

```powershell
Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"
```

Verify that the registry values match the configured settings.

## Notes / Caveats

- Settings are applied to the current user only.
- Dry-run previews are supported.
- The `Hidden` setting only accepts values `1` or `2`.
- Registry write failures may occur if permissions are restricted.
