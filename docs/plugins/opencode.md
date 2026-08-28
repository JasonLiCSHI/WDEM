> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# OpenCode Plugin

## Overview

The `opencode` plugin manages the configuration and settings for OpenCode. It automatically merges
the user's defined settings into the appropriate configuration files, ensuring that the user's
environment is set up correctly.

## Prerequisites

- **OpenCode**: Must be installed on user's system.
- The OpenCode configuration file (typically `settings.json`) should exist and be accessible by the
  user running WDEM.

## Configuration Schema

The plugin accepts a top-level YAML object with the following supported fields:

| Field      | Type   | Default | Description                                                                 |
| :--------- | :----- | :------ | :-------------------------------------------------------------------------- |
| `settings` | object | none    | A dictionary of OpenCode settings that will be applied to your environment. |

## Usage Examples

### Example 1 - Basic configuration

```yaml
extensions:
  opencode:
    settings:
      enableFeatureX: true
      maxLimit: 10
```

### Example 2 - Advanced configuration

```yaml
extensions:
  opencode:
    settings:
      theme: 'dark'
      autoSave: true
      workspacePath: "C:\\Projects"
```

## Notes / Caveats

- The plugin deep-merges settings – existing config entries and features not mentioned in user's
  config are preserved.
- It supports dryRun mode – logs what would change in the config path without actually writing to
  disk.

## Verification Steps

To verify that the settings managed by this plugin have been applied correctly:

- The user have to restart the OpenCode application.
- Then they have to open the OpenCode UI and navigate to the Settings/Preferences menu to their
  configurations.
