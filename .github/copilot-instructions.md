# Copilot Instructions — WindowThumbWall

## Project boundaries

- This is a Windows desktop app built with WPF on .NET 10. It displays live DWM thumbnails of external windows; it is not a screen-capture or streaming app.
- Official distribution is ZIP, MSI, and MSIX. Tagged GitHub Actions builds produce release artifacts; local builds are for development and verification.
- Repository work does not authorize SSH access to, inspection of, or changes on a remote or production host. Such work requires a user instruction naming the host and intended operation.

## Scope and product invariants

- Complete the current user-authorized objective and its necessary follow-ups. Keep unrelated work separate. Reuse authorization already present in the task, and request a decision only when a necessary action exceeds it.
- Keep changes limited to the objective. Do not add unrelated cleanup, packaging changes, release preparation, or generated files.
- Follow [the project invariants](../docs/invariants.md), [the developer guide](../docs/developer-guide.md), and [the release process](../docs/releasing.md) for maintained behavior and release requirements.
- User-facing explanations are in Japanese; internal reasoning and tool decisions are in English.

## Verification

Use these Release build and test commands for checks that do not need a window:

~~~powershell
dotnet build .\WindowThumbWall.csproj -c Release
dotnet build .\WindowThumbWall.Tests\WindowThumbWall.Tests.csproj -c Release
dotnet test .\WindowThumbWall.Tests\WindowThumbWall.Tests.csproj -c Release --no-build
~~~

The no-build test command assumes the test project has already been built. For interactive checks, use PowerShell 7 and .\packaging\start-development.ps1, which targets the native Display 3 device and returns an isolated state path. If Display 3 is unavailable, continue checks that do not need a visible window. Do not force focus or synthesize user input; use a deliberate valid user action for checks that require source-window activation, and report only the result actually observed.

For MSIX packaging, use .\packaging\build-msix.ps1 -Configuration Release; it requires Visual Studio MSBuild. To build all official formats, use .\packaging\build-all.ps1.
