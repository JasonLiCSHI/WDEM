> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Ditto Plugin

## Overview

The Ditto plugin manages configuration of Ditto via config.yaml.

## Prerequisites

- Ditto installed

## Configuration Schema

Ditto's settings aren't a fixed list — you can put any key you want under settings:, and it gets copied as-is into Ditto's settings file. To find real setting names, open Ditto's own Options window on your computer (General tab, Keyboard Shortcuts tab, etc.) and see what's there.



## Usage Examples

### ditto config

```yaml
extensions:
  ditto:
    settings:
      MaxItemsInList: 100
    dryRun: true
```

## Verification Steps

```bash
type "%APPDATA%\Ditto\Ditto.settings"
```

## Notes / Caveats
- Although the file is named `Ditto.settings`, its contents are JSON, not plain text.
- Merges are shallow and non-validating: only top-level keys you specify are added or overwritten, and the plugin does not check that a key is a real Ditto setting before writing it.
- Use `dryRun: true` to preview a change without writing to disk.
