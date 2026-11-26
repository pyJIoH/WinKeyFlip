# KeyFlip for Windows

A small, offline Windows tray app for correcting text typed with the wrong English/Russian keyboard layout and inverting letter case. It is the Windows port of [KeyFlip for macOS](https://github.com/pyJIoH/KeyFlip): the same two gestures and the same rules, built on Windows facilities. Written in C#, with no third-party dependencies.

[MIT license](LICENSE) · [Specification](SPEC.md) · [Testing guide](docs/TESTING.md) · [Changelog](CHANGELOG.md)

| Tap a key by itself | Action | Example |
| --- | --- | --- |
| Left or right **Alt** (or the **Win** key, if you choose it in Settings) | Convert English ↔ Russian and switch the keyboard layout | `ghbdtn` ↔ `привет` |
| **Left Ctrl** | Invert each letter's case | `Hello ПРИВЕТ` ↔ `hELLO привет` |
| **Caps Lock** | Switch the keyboard layout only; no text is changed | English ↔ Russian |

Selected text takes priority. With no selection, KeyFlip changes the word immediately before the cursor. For a word you have just typed, this also works in terminals and other editors that hide their text from UI Automation (see [How text is replaced](#how-text-is-replaced)). Release the key within one second; typing, clicking, scrolling, or pressing another modifier cancels the gesture. Right Ctrl never triggers anything, and of Alt and the Windows key only the one chosen in Settings does. Normal keyboard shortcuts keep working.

## Requirements

- Windows 10 or 11. The .NET Framework 4.8 they ship with is all KeyFlip needs, to run and to build.
- An English and a Russian keyboard layout added in Windows language settings.

No administrator rights, permissions, packages, accounts or network services.

## Build and run

From the repository root, in Command Prompt or PowerShell:

```bat
scripts\test.cmd
scripts\build.cmd
```

The app is written to `dist\KeyFlip.exe`: one file, with its icon and manifest inside. The scripts use the C# compiler that is part of Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`); no Visual Studio or .NET SDK is involved. Build output is ignored by Git.

1. Copy `dist\KeyFlip.exe` to a stable place, for example `%LOCALAPPDATA%\Programs\KeyFlip\`.
2. Run it. A keycap icon with «Я» appears in the notification area, and Settings opens on the first launch. Later, click the icon or run `KeyFlip.exe` again to open Settings.
3. Choose the English and Russian layouts if you have more than one of either. Tick **Start when I sign in** if desired, and untick **Caps Lock only switches the layout** if you want Caps Lock to stay as it is. On a keyboard that is shared with a Mac, choose **Win** under **Convert layout with a tap of** (see [Win instead of Alt](#usage-and-behavior)).

To update, quit KeyFlip (tray menu, or `KeyFlip.exe --quit`) and replace the file. If you move the file, tick **Start when I sign in** again from the new location.

The executable is not signed. Windows SmartScreen or an antivirus product may ask for confirmation the first time, as for any unsigned program that listens to the keyboard.

## Usage and behavior

Type `ghbdtn`, release the keys, and tap Alt: the word becomes `привет` and the Russian layout becomes active. Tap left Ctrl to get `ПРИВЕТ`. You can also select an existing word, sentence, or several lines first. The converted text replaces the selection and the cursor is placed after it; the text is not left selected.

- “Last word” means the whitespace-delimited text before the caret, including attached punctuation. KeyFlip can reach back across up to eight trailing spaces, but never across a tab or a line break.
- Automatic word conversion is limited to 256 UTF-16 units. Explicit selections can contain up to 100,000.
- Layout conversion follows physical key positions in the two chosen layouts; it is not transliteration or translation.
- Selections containing both Latin and Cyrillic letters are rejected for layout conversion. Case inversion still works on mixed-language text.
- Digits and punctuation follow the active layout when no letters indicate a direction. Unmapped characters, including emoji, are preserved.
- In an empty field, Alt just switches the layout. If KeyFlip cannot find text to change, it switches the layout and says that no text was changed.
- Case inversion uses Unicode case mapping. A few characters expand, such as `ß` → `SS`, so inversion is not reversible for every language.

**Alt and menus.** Windows normally activates a window's menu bar when Alt is pressed and released alone. KeyFlip suppresses that for a quick tap, so a lone Alt tap no longer opens the menu bar or Office key tips. Holding Alt for more than a second, F10, and Alt+letter work as before.

**AltGr.** On layouts that have AltGr, right Alt is AltGr and does not trigger; use left Alt.

**Win instead of Alt.** On a keyboard that is shared with a Mac, the key that is Option on the Mac is the Windows (Start) key on Windows. Choose **Win** under **Convert layout with a tap of** in Settings to keep the gesture on that key: a quick tap of either Windows key then converts the text and does not open the Start menu, and Alt goes back to what it does in Windows. Shortcuts such as Win+E, Win+V and Win+Shift+S keep working. To open the Start menu, hold the key for more than a second before releasing it, press Ctrl+Esc, or click the Start button. While KeyFlip is paused, and in windows that run as administrator, a tap opens the Start menu as usual.

**Caps Lock.** A tap of Caps Lock switches between the two chosen layouts and nothing else: it does not turn capital letters on, and applications never see the key. For the regular Caps Lock, hold the key for half a second, as on macOS, or press it with Shift (or any other modifier): capitals turn on or off, and the layout stays as it was. The layout switches the moment the key goes down, so that whatever you type next is already in the new layout; when the press turns out to be a long one, the layout is put back. A word you were typing stays tracked, so you can still tap Alt afterwards to convert it. Untick **Caps Lock only switches the layout** in Settings to give the key back to Windows; while KeyFlip is paused, and in windows that run as administrator, it is the regular Caps Lock too.

## How text is replaced

KeyFlip tries these strategies in order:

1. **A word you just typed.** KeyFlip remembers the characters typed since the last click, arrow key, Enter, Tab, shortcut, or window switch. It erases the word with Backspace and types the converted word as Unicode characters. This needs no UI Automation and no clipboard, so it also works in terminals and in IDE, Electron and web editors that hide their text. When the editor does expose its text, KeyFlip first checks that the text before the cursor is exactly that word; otherwise it uses the next strategy. Tap again to convert back.
2. **UI Automation.** For a selection, or a word you did not just type (for example, after clicking at the end of it), KeyFlip reads the text through UI Automation, selects it, and pastes the replacement with Shift+Insert. It verifies the result, puts the cursor back behind any trailing spaces, and never pastes a second time on its own.
3. **Copy and paste.** If the editor does not expose its selection, KeyFlip copies it with Ctrl+Insert, converts it, and pastes it with Shift+Insert. If nothing is copied, no text is changed. A single copied line ending in a line break is rejected, because code editors copy the current line when nothing is selected. This is skipped where it cannot help: in terminals (selected terminal text is usually output), on read-only text, and on controls that are not editors.

Ctrl+Insert and Shift+Insert are used instead of Ctrl+C and Ctrl+V: they mean the same in practically every Windows editor, and they can never interrupt a console program.

Some editors expose no text at all. In Visual Studio Code (unless its screen reader mode is on) and in terminals, KeyFlip can convert a word you have just typed and, in Visual Studio Code, a selection; for a word typed earlier, select it first.

Virtual machines and remote desktops send keys to another system with its own layout, so KeyFlip only switches the local layout there. Windows running as administrator cannot be reached unless KeyFlip itself runs as administrator. Compatibility with every application cannot be guaranteed.

## Privacy

KeyFlip works offline. It does not write typed text to disk, keep a history, or send anything over the network. It reads editor text only when an action is requested.

To retype a word, KeyFlip keeps only the current word (at most 256 characters) and the spaces after it in memory. It is cleared by clicks, navigation keys, shortcuts, Enter, Tab and window switches, and replaced when the next word starts.

Password fields are excluded: nothing is read, converted or retyped there, and an Alt tap only switches the layout. They are recognized through UI Automation and the classic password style. Windows has no system-wide equivalent of macOS Secure Input, so in a password prompt that cannot be recognized (for example one drawn by a terminal program) the current word can still sit in memory until the next clearing event. It is never stored or sent.

Strategies 2 and 3 temporarily use the clipboard, then restore everything that can be read back (text, rich text, HTML, images, file lists and custom formats; not embedded OLE objects). If another copy happens during a replacement, that newer content is kept. If the existing clipboard cannot be preserved, KeyFlip stops before inserting anything. The temporary text is marked so that clipboard history and cloud clipboard skip it; in strategy 3 the copy is made by the application itself and can appear in clipboard history.

UI Automation queries make Chromium-based applications turn on their accessibility support.

## Tests

```bat
rem Deterministic core tests; no desktop access needed.
scripts\test.cmd

rem Desktop checks: synthetic key events, the real clipboard code in a private
rem window station, UI Automation against the checks' own edit controls.
scripts\check-platform.cmd

rem Also check conversion with the installed English and Russian layouts.
scripts\check-platform.cmd --with-layouts

rem Optional: the whole pipeline in the test's own window.
scripts\test.cmd --e2e

rem Optional: the whole pipeline in a real application's window.
dist\KeyFlip.Tests.exe --external "KeyFlip editor checks" tests\external\browser.txt
```

The test runner is a standalone executable with no test framework. The platform checks need a logged-in desktop session; they post no input, take no focus, switch no layout and do not touch your clipboard.

The end-to-end check is different: it opens a small window, takes the keyboard focus for about twenty seconds, presses keys while that window is in the foreground, uses the real clipboard (and checks that it is restored), and switches the layout an even number of times. Do not type while it runs.

The scripted check does the same in a window you name, for example a browser showing `tests\fixtures\editor.html`; it presses keys only while that window is in the foreground.

See [the testing guide](docs/TESTING.md) for coverage, the applications checked so far, and the manual checklist.

## Troubleshooting

- **PAUSED, or taps do nothing:** check the Pause/Resume setting. If the focused window runs as administrator, Windows does not let KeyFlip see or send keys there.
- **The word was selected but not changed:** read the status line in Settings. The application may not have accepted the paste, or the target changed. Check the text before trying again.
- **A word typed earlier is not converted in a terminal or IDE:** KeyFlip can only retype words it saw you type. Select the word and tap again where selections can be copied, or retype it.
- **A browser or Electron editor does nothing on the first try:** its accessibility support starts on the first query; KeyFlip retries for a moment. Try again, or select the text.
- **Wrong or missing layout:** add English and Russian in Windows language settings, press **Refresh**, and choose the intended variant.
- **Alt no longer opens the menu:** that is the price of the Alt gesture; hold Alt for more than a second or press F10.
- **The Windows key no longer opens the Start menu:** that is the price of choosing Win for the gesture; hold the key for more than a second, press Ctrl+Esc, or switch back to Alt in Settings.

The **Test conversion** controls in Settings run either action in a chosen window through the normal pipeline. They help separate a gesture-detection problem from a text-access problem.

For layout metadata only:

```bat
dist\KeyFlip.exe --diagnose
```

This prints the version, whether KeyFlip is elevated, the candidate layouts and the active layout as JSON. It prints no editor text.

## Architecture

Pure logic lives in `src\KeyFlip.Core` and has no Windows dependencies; `src\KeyFlip` owns platform integration and the UI. The component table, the threading model and the differences from the macOS app are in [SPEC.md](SPEC.md).

## License

KeyFlip's source code and documentation are available under the [MIT license](LICENSE).

The project has no third-party package dependencies and bundles no third-party fonts, images or libraries. It uses the .NET Framework and system components supplied by Windows; those are not redistributed in this repository.
