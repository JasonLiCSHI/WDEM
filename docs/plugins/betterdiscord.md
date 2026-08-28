> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# BetterDiscord Plugin

## Overview

The BetterDiscord plugin manages BetterDiscord configuration settings, allowing you to customize
your Discord client through BetterDiscord's data settings.

It can:

- Apply custom settings to BetterDiscord
- Merge new settings with existing configuration
- Preserve existing values that are not modified

## Prerequisites

- BetterDiscord installed
- Windows environment with `%APPDATA%` configured
- Permission to write to `%APPDATA%\BetterDiscord\data`

## Configuration Schema

| Field    | Type   | Default | Description                                               |
| -------- | ------ | ------- | --------------------------------------------------------- |
| settings | object | none    | Key-value settings written to BetterDiscord settings.json |

### Settings Example

```yaml
settings:
  theme: custom
  fontSize: 14
```

## Usage Examples

### Basic Configuration

```yaml
extensions:
  betterdiscord:
    settings:
      theme: dark
```

### Multiple Settings

```yaml
extensions:
  betterdiscord:
    settings:
      theme: dark
      fontSize: 14
      showChannels: true
```

## Verification Steps

1. Apply the configuration.
2. Open `%APPDATA%\BetterDiscord\data\settings.json`.
3. Verify the configured values exist.
4. Launch Discord with BetterDiscord.
5. Confirm the settings are reflected in the BetterDiscord interface.

## Notes / Caveats

- Existing settings are merged rather than replaced.
- Unknown settings are written without validation.
- The plugin stores configuration in JSON format.
- The `%APPDATA%` environment variable must exist.
- BetterDiscord must be installed separately; this plugin only manages its settings.
