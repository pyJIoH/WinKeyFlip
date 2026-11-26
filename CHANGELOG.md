# Changelog

Notable user-visible changes to KeyFlip for Windows. Versions match `AssemblyInformationalVersion` in `src/KeyFlip/Properties/AssemblyInfo.cs`.

## [Unreleased]

### Added

- Caps Lock, pressed alone, switches between the English and Russian layouts and changes no text. A tap no longer turns capital letters on; for the regular Caps Lock, hold the key for half a second, as on macOS, or press Shift+Caps Lock. A checkbox in Settings turns this off.
- The layout gesture can be moved from Alt to the Windows (Start) key in Settings. That is where Option is on a keyboard shared with a Mac, so the gesture stays on the same physical key on both systems. A quick tap then converts text instead of opening the Start menu; holding the key for more than a second, Ctrl+Esc and the Start button still open it, and Win shortcuts keep working.

### Fixed

- A second tap that arrives while the first one is still being applied no longer activates the window's menu bar.

## [0.1.0] - 2026-11-25

Initial Windows release, a port of KeyFlip for macOS 0.4.0.

### Added

- Tap left or right Alt alone to convert text between the English and Russian layouts and switch the keyboard layout; tap left Ctrl alone to invert letter case.
- Selected text takes priority; otherwise KeyFlip changes the word before the cursor, keeping up to eight trailing spaces.
- A word typed just now is erased with Backspace and retyped, which also works in terminals and in editors that hide their text. Tap again to convert back.
- Replacement through UI Automation and a normal paste, with verification and clipboard preservation.
- Selections the editor does not expose are copied with Ctrl+Insert, converted, and pasted back with Shift+Insert. Terminals, read-only text and controls that are not editors are excluded. A single copied line ending in a line break is rejected.
- A lone Alt tap no longer activates the window's menu bar.
- Tray icon and settings window: layout choice, start at sign-in, pause, test controls, status.
- Password fields are excluded. Virtual machines and remote desktops only get the local layout switch.
- `KeyFlip.exe --diagnose` prints layout metadata; `KeyFlip.exe --quit` stops the running copy.

### Privacy

- To retype a word, KeyFlip keeps the current word (at most 256 characters) and the spaces after it in memory. It is cleared by clicks, navigation keys, shortcuts, Enter, Tab and window switches. Typed text is never written to disk or sent over the network.
- Temporary clipboard text is marked to stay out of clipboard history and cloud clipboard.
