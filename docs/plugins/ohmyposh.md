> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Oh My Posh Plugin

## Overview

The Oh My Posh plugin configures terminal themes using Oh My Posh prompt engine.

## Prerequisites

- Oh My Posh installed
- PowerShell or supported shell

## Configuration Schema

| Key     | Type   | Description           |
| ------- | ------ | --------------------- |
| theme   | string | Path to theme file    |
| profile | string | Optional profile path |

## Usage Examples

### Basic theme

```yaml
plugins:
  - name: ohmyposh
    theme: 'atomic.omp.json'
```

### Custom profile

```yaml
plugins:
  - name: ohmyposh
    profile: 'custom.ps1'
```

## Verification Steps

```bash
oh-my-posh --version
```

## Notes / Caveats

- Only modifies prompt initialization block
- Safe overwrite using markers

```

```
