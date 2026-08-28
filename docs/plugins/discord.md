> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Discord Plugin

## Overview

The Discord plugin manages Discord client configuration settings.

It can:

- Create or update the Discord configuration file
- Apply custom settings
- Merge new settings with existing configuration
- Preserve existing values that are not modified

## Prerequisites

- Discord installed
- Windows environment with `%APPDATA%` configured
- Permission to write to `%APPDATA%\discord`

## Configuration Schema

| Field    | Type   | Default | Description                                         |
| -------- | ------ | ------- | --------------------------------------------------- |
| settings | object | none    | Key-value settings written to Discord settings.json |

### Settings Example

```yaml
settings:
  SKIP_HOST_UPDATE: true
  minimizeToTray: true
```

## Usage Examples

### Basic Configuration

```yaml
extensions:
  discord:
    settings:
      minimizeToTray: true
```

### Multiple Settings

```yaml
extensions:
  discord:
    settings:
      SKIP_HOST_UPDATE: true
      minimizeToTray: true
      autoclear: false
```

## Verification Steps

1. Apply the configuration.
2. Open `%APPDATA%\discord\settings.json`.
3. Verify the configured values exist.
4. Launch Discord.
5. Confirm the settings are reflected in the application.

## Notes / Caveats

- Existing settings are merged rather than replaced.
- Unknown settings are written without validation.
- The plugin stores configuration in JSON format.
- The `%APPDATA%` environment variable must exist.
- Invalid values may not be recognized by Discord.
