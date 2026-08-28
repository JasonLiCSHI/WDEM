# Cross-platform development

WDEM currently validates its libraries and tests; it has no runnable product
host. Developers on Windows, Linux, and macOS can restore, build, and test the
solution with the .NET 10 SDK:

```bash
dotnet restore Wdem.sln -p:EnableWindowsTargeting=true
dotnet build Wdem.sln -p:EnableWindowsTargeting=true --no-restore
dotnet test Wdem.sln -p:EnableWindowsTargeting=true --no-build
```

Do not use `dotnet run` or expect a release executable until `Wdem.Cli` and
`Wdem.Desktop` are introduced. Windows-specific behavior remains covered by
the transition-library tests.
