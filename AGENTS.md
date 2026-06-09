# Repository Guidelines

## Project Structure & Module Organization

This repository contains a C# Unity Debug Adapter for Mono-based Unity debugging. The main executable lives in `unity-debug-adapter/`, with protocol handling, session logic, logging, and adapter entry point files such as `Program.cs`, `DebugSession.cs`, and `UnityDebugSession.cs`. End-to-end tests live in `unity-debug-adapter.E2ETests/`; its `unity_test_project_2022_3/` directory is a Unity 2022.3 fixture and is excluded from normal test project compilation. `debugger-libs/` is the Mono debugger dependency tree, normally cloned as a submodule. Build outputs are written under `bin/<Configuration>/`.

## Build, Test, and Development Commands

- `git clone --recurse-submodules <repo>`: clone the adapter and required `debugger-libs` sources.
- `dotnet build --configuration=Release unity-debug-adapter/unity-debug-adapter.csproj`: build the release adapter into `bin/Release/`.
- `dotnet publish --configuration=Release --runtime=linux-x64 unity-debug-adapter/unity-debug-adapter.csproj`: produce a runtime-specific publish bundle. CI also publishes `win-x64` and `osx-x64`.
- `dotnet test unity-debug-adapter.E2ETests/unity-debug-adapter.E2ETests.csproj`: run NUnit E2E tests. Requires Unity 2022.3.x installed through Unity Hub.
- `bin/Release/unity-debug-adapter.exe --log-level=info`: run the adapter over stdin/stdout; use `--log-file=<path>` when you need persisted logs.

If restore fails for `Microsoft.SymbolStore` or `Microsoft.FileFormats`, add the documented legacy NuGet source:
`dotnet nuget add source 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json' -n OutdatedPackages`.

## Coding Style & Naming Conventions

The adapter targets `net472` with C# `LangVersion` 8.0. Keep source formatting consistent with existing files: two-space indentation, braces on their own lines for types and methods, PascalCase for types and public members, camelCase for locals, and `m_` prefixes for private fields. Prefer explicit, small protocol helpers over dynamic dispatch; recent history intentionally removed `dynamic` usage.

## Code Exploration

When CSharpMCP code exploration tools are available, prefer them for navigating C# symbols, definitions, references, and call graphs instead of broad text search. Use text search only as a fallback when CSharpMCP is unavailable or when looking for non-C# assets, logs, generated files, or exact string occurrences.

## Testing Guidelines

Tests use NUnit with `Microsoft.NET.Test.Sdk`, `NUnit3TestAdapter`, and `coverlet.collector`. Name test classes after the subject plus scope, for example `UnityDebugSession_E2ETests`, and use clear `Test_...` method names. E2E tests start a real Unity Editor from Unity Hub and replay requests from `unity-debug-adapter.E2ETests/log.txt`, so keep that log and expected response assertions synchronized when protocol behavior changes.

## Commit & Pull Request Guidelines

Recent commits use concise Conventional Commit-style prefixes such as `fix:`, `fix!:`, `chore:`, `ci:`, and `docs:`; keep subjects short and imperative. For pull requests, include the behavior changed, commands run, Unity/.NET versions used for testing, and any screenshots or logs when debugging behavior or E2E output changes.

## Security & Configuration Tips

Do not add Unity `Library/`, local logs, credentials, or machine-specific Unity paths. Keep generated binaries under `bin/` out of source changes unless a release process explicitly requires them.
