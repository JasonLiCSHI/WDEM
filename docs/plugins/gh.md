> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# GitHub CLI Plugin

## Overview

The GitHub CLI plugin manages configuration for `gh` using YAML config file.

## Prerequisites

- GitHub CLI installed (`gh`)
- PyYAML installed

## Configuration Schema

| Key          | Type   | Description     |
| ------------ | ------ | --------------- |
| git_protocol | string | SSH or HTTPS    |
| editor       | string | Default editor  |
| aliases      | object | Command aliases |

## Usage Examples

### Git protocol

```yaml
plugins:
  - name: gh
    git_protocol: ssh
```

### Aliases

```yaml
plugins:
  - name: gh
    aliases:
      co: pr checkout
```

## Verification Steps

```bash
gh --version
```

```bash
gh config list
```

## Notes / Caveats

- YAML is auto-merged
- Empty values ignored

```

```
