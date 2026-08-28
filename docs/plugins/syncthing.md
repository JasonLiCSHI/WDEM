> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# 🔄 Syncthing Plugin

## 📋 Overview

The Syncthing plugin deploys a decentralized, peer-to-peer decentralized file synchronization engine across node networks.

## 🛠️ Prerequisites

- Authorized localized local storage write permissions active

## 🗄️ Configuration Schema

| Key | Type | Description | Required |
| :--- | :--- | :--- | :--- |
| `gui_port` | `Integer` | Administrative interface dashboard web entry port | Yes |
| `folders` | `List` | Local path strings to monitor and broadcast | No |

## 💻 Usage Examples

```yaml
plugins:
  syncthing:
    gui_port: 8384
    folders:
      - path: ~/Development
```

## 🔍 Verification Steps

```bash
syncthing --version
```

## ⚠️ Notes & Caveats

- Requires network port exposure mapping clearance definitions.
