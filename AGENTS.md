# Agent instructions

Read `README.md` for behavior and `SPEC.md` for the specification and architecture. Pure logic belongs in `src/KeyFlip.Core` with regression scenarios in `tests/KeyFlip.Tests`; Windows integration belongs in `src/KeyFlip`. `SPEC.md` describes what the app does: change it in the same commit as the behavior.

This is the Windows port of [KeyFlip for macOS](https://github.com/pyJIoH/KeyFlip). Keep the two in step where Windows allows it, and record deliberate differences in `SPEC.md`, section 10.

## Toolchain

- The only compiler is the one inside Windows: .NET Framework 4.8 `csc.exe`, which accepts **C# 5**. No string interpolation, `?.`, `nameof`, expression-bodied members, auto-property initializers, `out var`, tuples, pattern matching or local functions. No project files, packages or SDK.
- Sources are UTF-8 without a byte-order mark and are compiled with `/codepage:65001`. Cyrillic and common punctuation are written as they are; other non-ASCII characters are written as `\xNNNN` escapes with exactly four hexadecimal digits (a surrogate pair is two escapes). Do not use `\u` escapes.
- `.cmd` files need CRLF line endings.
- Code that runs inside the keyboard or mouse hook (`InputMonitor`, `Engine.Handle` and what they call) must never block or call into another process: Windows silently removes a slow low-level hook.

## Changelog and version: required for every change

1. **Every user-visible change** (behavior, UI text, privacy, compatibility, fixes) gets a line in `CHANGELOG.md` under `## [Unreleased]`, in the same commit as the change. Use the subsections `### Added`, `### Changed`, `### Fixed`, `### Removed` and `### Privacy`. Write for users, not about code. Internal refactors and test-only changes need no entry.
2. **Releasing a version**: set the three version attributes in `src/KeyFlip/Properties/AssemblyInfo.cs` and the version in `res/app.manifest` and at the top of `SPEC.md` (semantic versioning: patch for fixes, minor for new features or behavior changes); rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD` and add a new empty `## [Unreleased]` above it; commit, then tag `vX.Y.Z`.
3. If a change affects what KeyFlip reads, keeps in memory, or puts on the clipboard, update the Privacy sections of `README.md` and `SPEC.md` as well.

## Before committing

Run all of these and make sure they pass:

```bat
scripts\test.cmd
scripts\build.cmd
scripts\check-platform.cmd --with-layouts
```

`scripts\test.cmd --e2e` exercises the whole pipeline but takes the keyboard focus and types into its own window for about twenty seconds; warn the user before running it. `dist\KeyFlip.Tests.exe --external` does the same in a real application's window (scripts in `tests/external`). Update `docs/TESTING.md` when you add scenarios or manual checks. Report which real editors were or were not checked by hand.

## Git

- Commit messages are in English.
- `dist/` and `obj/` are build output and stay out of commits.

## Installing a build

Quit the running copy first (`KeyFlip.exe --quit`), then replace the file; a running executable cannot be overwritten. Settings live in `HKCU\Software\KeyFlip`; start at sign-in is a value named `KeyFlip` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` that holds the path of the executable.
