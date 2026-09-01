# Scripts

Store executable scripts referenced by declarative Profile Task Activities in this directory.

Scripts should accept explicit arguments, return meaningful exit codes, write useful diagnostic output, and avoid embedding product-specific behavior in `Wdem.Core`.

Included scripts:

- `Invoke-VisualStudioProfessionalTask.ps1` detects Visual Studio Professional 2026, validates prerequisites, installs it from Microsoft's official stable channel with `Settings/.vsconfig`, and verifies the declared components.
- `Invoke-ReSharperTask.ps1` detects ReSharper for Visual Studio Professional 2026, validates prerequisites, and installs a SHA-256-pinned package from JetBrains.
- `Apply-ReSharperSettings.ps1` safely merges `Settings/CT.DotSettings` into the current user's ReSharper global settings layer and preserves a backup.
