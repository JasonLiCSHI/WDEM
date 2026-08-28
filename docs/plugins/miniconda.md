> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# 🐍 Miniconda Plugin

## 📋 Overview

The Miniconda plugin automates the provisioning of light-weight conda package, dependency, and environment management systems across active terminal workflows.

## 🛠️ Prerequisites

- Python baseline environment dependencies configured

## 🗄️ Configuration Schema

| Key | Type | Description | Required |
| :--- | :--- | :--- | :--- |
| `packages` | `List` | Core python or science package modules to provision | Yes |
| `channels` | `List` | External environment search channels | No |

## 💻 Usage Examples

```yaml
plugins:
  miniconda:
    channels:
      - conda-forge
    packages:
      - python=3.10
      - numpy
```

## 🔍 Verification Steps

```bash
conda env list
```

## ⚠️ Notes & Caveats

- Relies on standard network connections to download target wheel distributions.
