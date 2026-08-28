> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Yazi Plugin

## Overview

The Yazi plugin manages configuration for the Yazi terminal file manager using TOML files.

## Prerequisites

- Yazi installed
- Windows AppData access

## Configuration Schema

| File        | Purpose        |
| ----------- | -------------- |
| yazi.toml   | Main config    |
| keymap.toml | Key bindings   |
| theme.toml  | Theme settings |

## Usage Examples

### Manager config

```yaml
plugins:
  - name: yazi
    manager:
      show_hidden: true
```

### Keymap config

```yaml
plugins:
  - name: yazi
    keymap:
      manager: []
```

## Verification Steps

```bash
yazi --version
```

## Notes / Caveats

- TOML merging supported
- Unknown keys ignored safely

```

```
