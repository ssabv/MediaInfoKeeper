# MediaInfoKeeper Agent Guide

## Goal
- Keep patches stable across Emby upgrades.
- Prefer exact signature matching over heuristic reflection.

## Source Of Truth
- `EMBY_DOCS_DIR` selects the active method-signature snapshot under `~/Documents/Emby/dlls/<emby_version>/`.
- Default: `emby_4.9.5.0`
- Default version suffix comes from `EMBY_DOCS_DIR`, for example `emby_4.9.5.0` -> `4.9.5.0`
- Signature source of truth: `~/Documents/Emby/dlls/<emby_version>/`
- Primary inputs: `*_methods.txt`

## Local Dependency Layout
- DLL root directory: `~/Documents/Emby/dlls`
- Versioned DLL directory: `~/Documents/Emby/dlls/<emby_version>`
- Method-signature files: `~/Documents/Emby/dlls/<emby_version>/<AssemblyName>_methods.txt`
- Decompiled source directory: `~/Documents/Emby/dlls/<emby_version>/source`
- Decompiled folder format: `<AssemblyName>_<version>`

## Required Research Order
1. Read `~/Documents/Emby/dlls/<emby_version>/<Assembly>_methods.txt` for exact method signatures.
2. Read decompiled source under `~/Documents/Emby/dlls/<emby_version>/source/<AssemblyName>_<version>/` for real behavior, access modifiers, inheritance, and usable entry points.
3. Use both before changing any patch that targets Emby internals.

## Missing Dependency Workflow
- Do not stop at "file missing" if the repo scripts can populate the dependency.
- If a DLL is missing, fetch it with `bash Scripts/pull-dll <DllName.dll>`.
- If method signatures are missing, fetch them with `bash Scripts/pull-methods --emby-docs-dir ${EMBY_DOCS_DIR}`.
- If decompiled source is missing, generate it with `bash Scripts/decompile <DllName.dll>`.
- Pass explicit DLL names by default. Do not use the full default set unless all default DLLs are actually needed.
- Invoke scripts with `bash Scripts/<name>` by default. Do not assume the executable bit is present.
- `bash Scripts/decompile` only writes source folders under `~/Documents/Emby/dlls/<emby_version>/source/`; it does not generate Markdown index files.

## macOS Decompile Notes
- `ilspycmd` may require .NET 8 even if newer runtimes are installed.
- If `ilspycmd --version` fails with missing `Microsoft.NETCore.App 8.0`, install `dotnet@8`.
- Use this environment when running decompile commands:

```bash
export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
export PATH="$DOTNET_ROOT:$PATH"
bash Scripts/decompile Emby.Providers.dll
```

## Patch Rules
- Always use `PatchMethodResolver.Resolve(...)` for patch targets.
- Set explicit `ParameterTypes` whenever possible.
- Set `ReturnType` when overload ambiguity is possible.
- Use exact `BindingFlags`.
- Avoid `Predicate` unless no stable type-based signature exists.
- Do not use fallback `FindMethod(...)` name-only matching in production paths.
- When multiple overloads are valid across versions, resolve each exact overload and patch all of them.
- Keep prefix/postfix signatures compatible with every patched overload.

## Patch Workflow
1. Locate the target in `~/Documents/Emby/dlls/<emby_version>/<Assembly>_methods.txt`.
2. Copy the exact parameter type order from docs.
3. Resolve dependent runtime types by full name, for example `Assembly.GetType("Namespace.Type")`.
4. Build `MethodSignatureProfile` with exact `BindingFlags`, `ParameterTypes`, and `ReturnType` when needed.
5. If a prerequisite type is missing, log `PatchLog.InitFailed(...)` with a clear reason and stop that patch.
6. Rely on existing `PatchLog.ResolveHit` and `PatchLog.ResolveFailed` for resolution outcomes.
7. Verify with `dotnet build MediaInfoKeeper.csproj -f net8.0 --no-restore`.
8. Restart Emby with `bash Scripts/restart --no-build`.

## High-Value Decompiled Entry Points
- External subtitle scanning usually starts from:
  - `~/Documents/Emby/dlls/4.9.5.0/source/Emby.Providers_4.9.5.0/Emby.Providers.MediaInfo/BaseTrackResolver.cs`
  - `~/Documents/Emby/dlls/4.9.5.0/source/Emby.Providers_4.9.5.0/Emby.Providers.MediaInfo/SubtitleResolver.cs`
  - `~/Documents/Emby/dlls/4.9.5.0/source/Emby.Providers_4.9.5.0/Emby.Providers.MediaInfo/FFProbeSubtitleInfo.cs`

## Project Map
- `Patch/`: Harmony patches and method-resolution logic
- `Patch/PatchManager.cs`: patch bootstrap and health tracking
- `ScheduledTask/`: operational tasks
- `Services/`: runtime business services
- `Configuration/` and `Options/`: plugin config models and UI wiring
- `~/Documents/Emby/dlls/<emby_version>/*_methods.txt`: signature docs
- `Scripts/`: dependency and helper scripts

## Don’ts
- Don’t use parameter count as the primary matching strategy.
- Don’t keep dead reflection helpers.
- Don’t silently swallow missing type or signature failures.
- Don’t mix unrelated refactors into patch-signature updates.
