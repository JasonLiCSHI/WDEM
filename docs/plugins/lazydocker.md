> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Lazydocker Plugin

## Overview

The Lazydocker plugin manages Lazydocker configuration using its YAML configuration file.

## Prerequisites

- Lazydocker installed
- PyYAML available
- Windows AppData access

## Configuration Schema

| Key | Purpose |
|------|--------|
| settings | YAML configuration values to merge into `config.yml` |

## Usage Examples

### GUI settings

```yaml
plugins:
  - name: lazydocker
    settings:
      gui:
        language: auto
        returnImmediately: false
```

### Logs configuration

```yaml
plugins:
  - name: lazydocker
    settings:
      logs:
        since: "60m"
```

## Verification Steps

```bash
lazydocker --version
```

Verify that `%APPDATA%\lazydocker\config.yml` exists and contains the expected values.

## Notes / Caveats

- Existing YAML configuration is merged recursively.
- Dry-run previews are supported.
- Corrupted configuration files are backed up automatically.
- Configuration updates are written atomically.
- Reapplying the same configuration does not produce changes.
