> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Zoxide Plugin

## Overview

The Zoxide plugin configures smart directory navigation using `zoxide`.

## Prerequisites

- zoxide installed
- Shell support (PowerShell / Bash)

## Configuration Schema

| Key       | Type   | Description           |
| --------- | ------ | --------------------- |
| init.cmd  | string | Alias command         |
| init.hook | string | Hook type             |
| env_vars  | object | Environment variables |

## Usage Examples

### Basic setup

```yaml
plugins:
  - name: zoxide
    init: {}
```

### Custom alias

```yaml
plugins:
  - name: zoxide
    init:
      cmd: 'z'
```

## Verification Steps

```bash
zoxide --version
```

## Notes / Caveats

- Updates shell profiles automatically
- Works across PowerShell and Bash

```

```
