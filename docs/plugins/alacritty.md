> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Alacritty Plugin

## Overview

This plugin manages the Alacritty terminal emulator configuration file. It preserves existing TOML
formatting as much as possible while merging settings.

## Prerequisites

- Windows only (if managing Windows paths), but cross-platform logic applies.
- `APPDATA` must be set.
- The user must have permission to write to `%APPDATA%\alacritty\alacritty.toml`.

## Configuration Schema

The plugin accepts a top-level YAML object with a single supported field:

| Field      | Type   | Default | Description                                                                    |
| ---------- | ------ | ------- | ------------------------------------------------------------------------------ |
| `settings` | object | none    | Recursively merged into `alacritty.toml`. Any nested object shape is accepted. |

The plugin does not enforce an Alacritty-specific schema. It simply deep-merges the TOML object tree
you provide.

### Merge behavior

- Objects are merged recursively.
- Non-object values replace the existing value.
- New keys are added.
- If the current file is missing, the plugin starts from an empty object.

## Usage Examples

### Minimal update

```yaml
settings:
  window:
    padding:
      x: 10
      y: 10
```

### Nested settings update

```yaml
settings:
  font:
    normal:
      family: 'Cascadia Code'
    size: 11.0
  colors:
    primary:
      background: '#282c34'
      foreground: '#abb2bf'
```

## Verification Steps

1. Confirm Alacritty is installed.
2. Apply your configuration.
3. Open `%APPDATA%\alacritty\alacritty.toml`.
4. Verify the expected nested keys were added or updated.
5. Launch Alacritty and confirm the setting appears or takes effect.

## Notes / Caveats

- The plugin never validates whether a key is a real Alacritty option.
- Because merging is recursive, providing a partial object only changes the matching subtree.
- Dry runs are controlled by the execution context, not by a YAML field.
