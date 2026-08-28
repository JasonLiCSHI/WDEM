> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# ☕ Sdkman Plugin

## 📋 Overview

The Sdkman plugin manages parallel versions of multiple Software Development Kits for the Java ecosystem seamlessly.

## 🛠️ Prerequisites

- Zip and Curl utilities active inside terminal shell

## 🗄️ Configuration Schema

| Key | Type | Description | Required |
| :--- | :--- | :--- | :--- |
| `candidates` | `Map` | SDK runtime environments and specific versions to pin | Yes |

## 💻 Usage Examples

```yaml
plugins:
  sdkman:
    candidates:
      java: 17.0.7-tem
      gradle: 8.1.1
```

## 🔍 Verification Steps

```bash
sdk list java
```

## ⚠️ Notes & Caveats

- Modifies baseline shell environmental profile variables (`.bashrc` / `.zshrc`).
