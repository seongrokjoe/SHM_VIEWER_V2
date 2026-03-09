# Repository Guidelines

## Project Structure & Module Organization
`ShmViewer.sln` contains one .NET 8 WPF application in `ShmViewer/`. Keep window and dialog markup in `MainWindow.xaml` and `Views/Dialogs/`, view-models in `ViewModels/`, parser and shared-memory logic in `Core/`, and shared converters in `Converters.cs`. Release outputs belong in `dist/`. Treat `TestHeaders/` and the root `test_*.h` files as parser input samples, not automated tests.

## Build, Test, and Development Commands
Run commands from the repository root:

- `dotnet build ShmViewer\ShmViewer.csproj -c Debug` builds the app.
- `dotnet run --project ShmViewer\ShmViewer.csproj` starts the local viewer.
- `build_release.bat` publishes a `win-x64` Release build to `dist\ShmViewer` and creates `dist\ShmViewer_Release.zip`.

There is no test project yet, so `dotnet test` is not currently used here.

## Coding Style & Naming Conventions
Follow the existing C# style: 4-space indentation, file-scoped namespaces, nullable reference types enabled, and comments only where logic is not obvious. Use `PascalCase` for types, properties, commands, and XAML resources. Use `_camelCase` for private fields. Keep MVVM boundaries clear: code-behind should stay UI-focused, while parsing, tab state, and search behavior belong in `ViewModels/` or `Core/`.

## Testing Guidelines
Before opening a PR:

- Build in Debug configuration.
- Run the app and load headers from `TestHeaders/` or `test_sample.h`.
- Manually verify any affected flow, especially tree rendering, search, dialogs, and SHM refresh behavior.

If you change parsing logic, add or update a sample header that exercises the case.

## Commit & Pull Request Guidelines
Recent history uses Conventional Commit prefixes such as `feat:`, `fix:`, and `perf:` followed by a short summary, often written in Korean. Keep that pattern, for example `fix: search result null handling`. PRs should describe the user-visible change, note touched areas (`Core`, `ViewModels`, `Views`), and list manual verification steps. Include screenshots or short recordings for XAML changes.

## Security & Configuration Tips
This app targets `net8.0-windows` on `x64` and depends on ClangSharp plus native `libclang` binaries. Do not hardcode machine-specific header paths, local file locations, or SHM names in committed code.
