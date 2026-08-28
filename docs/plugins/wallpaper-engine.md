> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Wallpaper Engine Plugin

## Overview

This plugin manages Wallpaper Engine's `config.json` file. Configuration values are merged into the existing JSON configuration, allowing Wallpaper Engine settings to be managed declaratively through WDEM.

## Prerequisites

* Wallpaper Engine installed
* Steam installation available
* Permission to write to the Wallpaper Engine configuration directory

## Configuration File Location

| Platform | Path                                                                             |
| -------- | -------------------------------------------------------------------------------- |
| Windows  | `%ProgramFiles(x86)%\Steam\steamapps\common\wallpaper_engine\config\config.json` |

The plugin also checks the equivalent path under `%ProgramFiles%`.

## Configuration Schema

The plugin accepts a top-level YAML object with a single supported field:

| Field      | Type   | Default | Description                                                                          |
| ---------- | ------ | ------- | ------------------------------------------------------------------------------------ |
| `settings` | object | none    | JSON configuration values that will be merged into Wallpaper Engine's `config.json`. |

### Merge Behavior

* Existing configuration values are preserved unless overwritten.
* Nested objects are merged recursively.
* New keys are added automatically.
* Supports arbitrary JSON structures.

## Usage Examples

### Configure graphics settings

```yaml
extensions:
  wallpaper-engine:
    settings:
      graphics:
        fpsLimit: 60
        quality: high
```

### Configure general settings

```yaml
extensions:
  wallpaper-engine:
    settings:
      general:
        muteWhenMaximized: true
        pauseWhenFullscreen: true
```

## Verification Steps

1. Apply your WDEM configuration.
2. Open the Wallpaper Engine configuration file.
3. Verify the expected JSON values were added or updated.
4. Launch Wallpaper Engine and confirm the settings are reflected in the application.

## Notes / Caveats

* Existing configuration entries are preserved.
* The plugin performs a recursive deep merge of JSON objects.
* Supports dry-run mode.
* If the configuration file does not exist, it is created automatically.
* Arbitrary JSON keys are supported.
