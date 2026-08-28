> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Audacity Plugin

## Overview

This plugin manages Audacity configuration stored in `%APPDATA%\audacity\audacity.cfg`. Settings are merged into the existing configuration file, allowing Audacity preferences to be managed declaratively through WDEM.

## Prerequisites

* Audacity installed
* Windows system with `%APPDATA%` available
* Permission to write to `%APPDATA%\audacity`

## Configuration Schema

The plugin accepts a top-level YAML object with a single supported field:

| Field      | Type   | Default | Description                                                                                                             |
| ---------- | ------ | ------- | ----------------------------------------------------------------------------------------------------------------------- |
| `settings` | object | none    | Key-value pairs to merge into `audacity.cfg`. Keys may be specified as `Section/Key` to target a configuration section. |

### Merge Behavior

* Existing settings are preserved unless overwritten.
* Keys containing `/` are written into configuration sections.
* Boolean values are converted to `1` or `0`.
* New settings are added automatically.

## Usage Examples

### Configure audio host and playback device

```yaml
extensions:
  audacity:
    settings:
      AudioIO/Host: MME
      AudioIO/PlaybackDevice: Speakers
```

### Configure boolean settings

```yaml
extensions:
  audacity:
    settings:
      GUI/ShowSplashScreen: false
      GUI/ShowExtraMenus: true
```

## Verification Steps

1. Apply your WDEM configuration.
2. Open `%APPDATA%\audacity\audacity.cfg`.
3. Verify the expected keys were added or updated.
4. Launch Audacity and confirm the settings are reflected in Preferences.

## Notes / Caveats

* Existing configuration entries are preserved.
* Unknown keys are written as provided.
* Supports `dryRun` mode.
* The plugin automatically creates the Audacity configuration directory if it does not exist.
