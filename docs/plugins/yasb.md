> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# YASB Plugin

## Overview

The YASB plugin manages YASB configuration using its YAML configuration file.

## Prerequisites

- YASB installed
- PyYAML available
- User profile access

## Configuration Schema

| Key | Purpose |
|------|--------|
| settings | YAML configuration values to merge into `config.yaml` |

## Usage Examples

### Enable configuration watching

```yaml
plugins:
  - name: yasb
    settings:
      watch_config: true
      watch_stylesheet: true
```

### Configure bars

```yaml
plugins:
  - name: yasb
    settings:
      bars:
        status-bar:
          enabled: true
          widgets:
            left:
              - workspaces
              - active_window
            right:
              - cpu
              - memory
              - volume
              - battery
```

## Verification Steps

```bash
dir "%USERPROFILE%\.config\yasb"
```

Verify that `config.yaml` exists and contains the expected values.

## Notes / Caveats

- Existing YAML configuration is merged recursively.
- Dry-run previews are supported.
- Corrupted configuration files are backed up automatically.
- Missing configuration directories are created automatically.
- Configuration updates are written atomically.
