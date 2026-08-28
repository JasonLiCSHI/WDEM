> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Ripgrep Plugin

## Overview

The Ripgrep plugin manages configuration for `ripgrep` (rg) using the `.ripgreprc` config file.

## Prerequisites

- Ripgrep installed
- Available in PATH

## Configuration Schema

| File | Purpose |
| ---- | ------- |
| .ripgreprc | Ripgrep settings |

Any valid ripgrep command-line flag can be provided as a key (without the leading `--`).

## Usage Examples

### Ripgrep config

```yaml
extensions:
  ripgrep:
    settings:
      smart-case: true
      hidden: true
      max-columns: 150
```

## Verification Steps

```bash
rg --version
```

## Notes / Caveats

- Settings are written as `--key=value` or `--key` if boolean `true`.
- Safely handles corrupt configuration files by backing them up and starting fresh.
