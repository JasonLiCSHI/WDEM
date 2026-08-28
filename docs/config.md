> **Development status:** WDEM currently provides transition libraries and automated tests only. No public CLI or desktop host exists yet, so command and distribution examples on this page are design references rather than supported product instructions. Binary releases will be enabled only after `Wdem.Cli` and `Wdem.Desktop` exist. See [THIRD-PARTY-NOTICES](https://github.com/JasonLiCSHI/WDEM/blob/main/THIRD-PARTY-NOTICES.md) and [source provenance](https://github.com/JasonLiCSHI/WDEM/blob/main/docs/wdem/source-provenance.md).
# WDEM Configuration (`config.yaml`)

This document outlines the various configuration modules available in WDEM. You can define your
desired state in a `config.yaml` file, and WDEM will apply it.

Select a module below to see its configuration options.

## Package Managers

- [Winget](./modules/winget.md)
- [Chocolatey](./modules/chocolatey.md)
- [Scoop](./modules/scoop.md)

## System Configuration

- [Plugins & Extensions](./modules/plugins.md)
- [Dotfiles](./modules/dotfiles.md)
- [Environment Variables](./modules/env.md)
- [Git](./modules/git.md)
- [Registry Tweaks](./modules/registry.md)
- [System Settings](./modules/system_settings.md)
- [Windows Services](./modules/win_services.md)
- [Scheduled Tasks](./modules/scheduled_tasks.md)
- [WSL (Windows Subsystem for Linux)](./modules/wsl.md)
