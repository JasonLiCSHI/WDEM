> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# Everything Plugin

## Overview

The Everything plugin manages configuration for the Everything file search application using its `Everything.ini` file.

## Prerequisites

- Everything installed
- Windows AppData access

## Configuration Schema

| Key | Purpose |
|------|--------|
| settings | INI sections and values to apply |

## Usage Examples

### Apply settings
```yaml
plugins:
  - name: everything
    settings:
      Everything:
        match_case: false
```

### Multiple settings
```yaml
plugins:
  - name: everything
    settings:
      Everything:
        match_case: false
        show_window: true
```

## Verification Steps

```bash
dir "%APPDATA%\Everything"
```

Verify that `Everything.ini` exists and contains the expected values.

## Notes / Caveats

- Existing configuration values are merged safely
- Missing sections are created automatically
- Boolean values are written as lowercase strings
- Dry-run previews are supported
