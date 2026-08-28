> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Starship Plugin

## Overview

The Starship plugin configures the Starship shell prompt by updating the `starship.toml`
configuration file.

It helps customize terminal prompts with themes, symbols, and performance indicators.

## Prerequisites

- Starship installed (`starship.exe` or `starship`)
- Windows environment variable support

## Configuration Schema

| Key          | Type          | Description                   |
| ------------ | ------------- | ----------------------------- |
| Any TOML key | object/string | Starship configuration values |

## Usage Examples

### Basic setup

```yaml
plugins:
  - name: starship
    prompt: true
```

### Enable custom sections

```yaml
plugins:
  - name: starship
    prompt:
      add_newline: true
```

## Verification Steps

```bash
starship --version
```

Check config:

```bash
cat ~/.config/starship.toml
```

## Notes / Caveats

- Requires `starship` binary installed
- Config is automatically merged
- Runs on Windows via USERPROFILE

```

```
