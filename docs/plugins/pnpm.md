> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# PNPM Plugin

## Overview

The pnpm plugin manages configuration for the pnpm package manager using the user's `.npmrc` file.

## Prerequisites

- pnpm installed
- Available in PATH

## Configuration Schema

| File | Purpose |
| ---- | ------- |
| .npmrc | pnpm and npm settings |

The plugin automatically translates camelCase settings to dash-case (e.g., `storeDir` -> `store-dir`).

## Usage Examples

### pnpm config

```yaml
extensions:
  pnpm:
    settings:
      storeDir: "D:\\.pnpm-store"
      shamefullyHoist: true
      strictPeerDependencies: false
```

## Verification Steps

```bash
pnpm --version
```

## Notes / Caveats

- Modifies the global `.npmrc` file in the user's home directory.
- Backs up the existing configuration file before making changes.
