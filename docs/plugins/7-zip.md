> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# 📦 7-Zip Plugin

## 📋 Overview

The 7-Zip plugin provides high-ratio archive decompression and packing routines natively accessible across system automation hooks.

## 🛠️ Prerequisites

- Valid destination platform storage blocks active

## 🗄️ Configuration Schema

| Key | Type | Description | Required |
| :--- | :--- | :--- | :--- |
| `install_path` | `String` | Target platform deployment folder location | Yes |

## 💻 Usage Examples

```yaml
plugins:
  7-zip:
    install_path: C:\Program Files\7-Zip
```

## 🔍 Verification Steps

```powershell
7z --help
```

## ⚠️ Notes & Caveats

- Ensure execution path mappings are registered inside your user environments.
