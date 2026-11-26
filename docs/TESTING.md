# Testing KeyFlip for Windows

## Automated coverage

### Core scenarios

Run `scripts\test.cmd` from the repository root. The standalone runner contains **69 scenarios** and exits with status 1 if any assertion fails. It needs no desktop session.

| Area | Covered cases |
| --- | --- |
| Caps Lock as the layout switch | A press switches; auto-repeat and the release are swallowed; a long press is reported once, by auto-repeat or by the release; modifiers and the setting; a lost release; clock wrap-around |
| Modifier taps | Both Alt keys; left Ctrl; ignored right Ctrl and Windows keys; the Windows keys as the conversion key, with their shortcuts; chords; both sides held; AltGr (left Ctrl + right Alt); typing/mouse cancellation; long holds; exact timeout boundary; backwards timestamps; unmatched/wrong release; duplicate press; reset and recovery; repeated gestures |
| Word lookup | English/Russian; punctuation; trailing spaces and their limit; tabs/newlines/CRLF/Unicode whitespace; empty input; UTF-16 emoji/combining sequences; length boundary; clipped context; document offsets and overflow |
| Transformations | Complete English/Russian alphabets and ё; mixed case; Unicode expansions; symbols/emoji/whitespace; multiline and empty input; alphabet detection; unmapped characters |
| Typed word | Trailing spaces and their limit; Backspace; leading/other whitespace; multi-character input; length overflow until the next word; retyped word; matching editor text at a word boundary, no-break spaces, surrogates; independent snapshots; copied-line detection |
| Layout table | Both directions; punctuation by key position; dead and missing keys; every main-block key exactly once |
| Clipboard lease | Staging and restoring every format; idempotent restore; newer-copy protection; empty clipboard; saving before a copy and staging after it; a copy raced by a newer copy; delay-rendered data; failed staging, a failed restore and one that failed halfway; unpreservable contents; disposal |

### Platform checks

Run `scripts\check-platform.cmd` in a logged-in desktop session. These checks **post no input, take no focus, switch no layout and do not touch your clipboard or settings**:

- **Synthetic input.** Structure sizes and offsets used with `SendInput` and the keyboard hook; Backspace and Unicode typing events (one pair per UTF-16 unit, including surrogate halves); Ctrl+Insert and Shift+Insert with the extended-key flag; the menu mask key; the loop-prevention tag on every event. Events are constructed and inspected, never sent.
- **Clipboard.** The real Win32 clipboard code runs in a child process that first moves itself to a new, invisible window station, which has its own clipboard: several formats including a bitmap and a custom binary format, history-exclusion markers, restore and repeated restore, newer-copy protection, empty clipboard, copy-then-stage, a raced copy, disposal, and a 100,000-character round trip. The parent verifies that the desktop clipboard's sequence number did not change.
- **UI Automation.** Against the check's own off-screen, never-focused edit, multi-line edit and rich edit controls: the word before the caret, trailing spaces, selections, word boundaries (tab, line break, nine spaces, length limit), emoji, selection requests and their confirmation, replacement verification, caret restoration, typed-word probes (match, contradiction, document start, inline completion), and classification of password, read-only and non-text controls.

Add `--with-layouts` to check typed characters with Shift and Caps Lock, conversion in both directions with the installed English and Russian layouts, mixed case, punctuation, multiline text, emoji preservation, and rejection of mixed Latin/Cyrillic input. The layouts are read, not switched.

### End-to-end check

`scripts\test.cmd --e2e` runs the whole pipeline (real hooks, real key presses, real replacements) in a window of its own: **19 scenarios**.

It is not silent: it opens a small topmost window, takes the keyboard focus for about twenty seconds, presses keys only while that window is in the foreground, uses the real clipboard and checks that its text is restored, and switches the layout an even number of times. Do not type while it runs. If Windows does not let the window take the focus, nothing is typed and the check exits with status 3.

| Scenario | What it establishes |
| --- | --- |
| Typed word: Alt, Alt | Retyping, layout switch, tap again to undo |
| Typed word: left Ctrl twice | Case inversion without a layout switch |
| Typed word with a trailing space | Space kept; typing continues in the new layout; the new word converts back |
| Typed word with Caps Lock on | Letters are tracked in the case they were typed in; Caps Lock is put back afterwards |
| Caps Lock alone only switches the layout | No text changes and capitals stay off; the typed word is still converted by Alt afterwards; Shift+Caps Lock is the regular Caps Lock, and so is a long press, shown by auto-repeat or by the release, which leaves the layout as it was |
| A lone Alt tap does not open the menu bar | The window has a classic menu bar; typing after the tap still reaches the editor |
| Win as the conversion key | A lone tap of the Windows key converts the typed word and back; the Start menu stays closed |
| A tap during a replacement opens no menu | A second tap, of Alt and then of Win, in the middle of the first action: no menu bar, no Start menu, and typing still reaches the editor |
| Existing word; selection in a sentence; trailing spaces | UI Automation selection, verified paste, caret position, clipboard restored |
| Rich edit control | The same in a rich edit control, including a second line |
| Shortcuts and other modifiers | Ctrl+C, right Ctrl, Shift and a 1.15 s hold do nothing |
| Password field | Text untouched, nothing tracked; Alt only switches the layout |
| Editor without accessibility (4 scenarios) | A control that exposes no text: retyping; copy, convert and paste of a selection; nothing selected; a whole-line copy that is not pasted back |
| Empty field | Alt only switches the layout |

Add a name fragment to run matching scenarios only, and `--trace` to print what the engine saw (key codes, never characters): `scripts\test.cmd --e2e "typed word" --trace`.

### Scripted checks in real applications

`dist\KeyFlip.Tests.exe --external "<window title>" <script file> [--trace]` runs the same pipeline in another application's window. The script is a list of steps (key presses, taps, and expectations read back through UI Automation or from a file); the steps are described at the top of `tests\KeyFlip.Tests\ExternalCheck.cs`. Keys are pressed only while the named window is in the foreground, and the check stops as soon as it is not. Open a disposable window first:

| Script in `tests\external` | Open first | Window title to pass |
| --- | --- | --- |
| `browser-password.txt`, then `browser.txt` | `tests\fixtures\editor.html` in a browser, freshly loaded before each script | `KeyFlip editor checks` |
| `code.txt` | An empty `%TEMP%\keyflip-check.txt` in Visual Studio Code with `"files.autoSave": "afterDelay"` | `keyflip-check.txt` |
| `console.txt`, `capslock.txt`, `winkey.txt` | `conhost.exe cmd.exe /d /k "title KeyFlipConsoleCheck & prompt $G"`, or the same prompt in a terminal tab with a fixed title | `KeyFlipConsoleCheck` |

Results on Windows 11 (build 26300), 25 and 26 November 2026, KeyFlip 0.1.0 and the unreleased changes after it (Caps Lock, Win as the conversion key):

| Application | Result |
| --- | --- |
| Microsoft Edge 158 (input, textarea, contenteditable, password field) | Typed word converted and converted back; left Ctrl; a word not typed just now and a selection replaced through UI Automation, caret behind the trailing space; password field untouched, also as the first thing after the browser starts. Caps Lock switches the layout and changes no text. With Win as the conversion key: a typed word converted and converted back, the Start menu stays closed, an Alt tap converts nothing |
| Visual Studio Code 1.140 | Typed word converted and converted back; a selection converted through the clipboard; a lone Alt tap does not move the focus to the menu bar. The editor exposes no text, so a word typed earlier only gets the layout switch, and with nothing selected the line is not duplicated |
| Visual Studio Code 1.140, Win as the conversion key | Not checked |
| Classic console (`conhost`) and Windows Terminal 1.25, command prompt | Typed word converted and converted back; the layout follows; left Ctrl; Caps Lock switches the layout and changes no text; with Win as the conversion key, a typed word converted and converted back and the Start menu stays closed |

`dist\KeyFlip.Tests.exe --render-settings out.png` draws the settings window into a picture without showing it, at the display's scale; add `--unscaled` for 100%.

GitHub Actions builds the app and runs the core scenarios. Desktop-dependent checks are explicit local commands, because hosted runners do not represent a user's layouts or editors.

## What automated tests do not establish

There is no claim of complete line coverage or universal application compatibility. The end-to-end check uses classic Windows edit controls and a stand-in for canvas editors; the scripted checks cover only the applications and versions listed above. Not checked at all: Notepad, Chrome and Firefox, Office, JetBrains IDEs, Google Docs, chat applications, Undo behavior, clipboard history contents, remote desktops and virtual machines, elevated windows, AltGr layouts, real keyboards with auto-repeat, start at sign-in, and the hook watchdog. These require the manual checks below.

## Manual regression checklist

Use disposable text in an empty draft or document. Repeat the main conversion cases in a native editor (Notepad), a browser input/textarea/contenteditable, an Electron editor (Visual Studio Code, a chat application), a JetBrains IDE, and a terminal (Windows Terminal and the classic console).

The local [editor fixture](../tests/fixtures/editor.html) provides three disposable browser fields and an input-event counter. Open it in a browser from Explorer. It has no network calls or submit action.

| Check | Expected result |
| --- | --- |
| Type `ghbdtn`, tap either Alt alone | `привет`; Russian layout active; the menu bar does not open |
| Tap Alt again | `ghbdtn`; English layout active |
| Tap left Ctrl twice | `GHBDTN`, then `ghbdtn` |
| Select `Ghbdtn` inside a longer sentence, then Alt | Only the selection becomes `Привет`; caret after it, nothing selected |
| Select `Hello МИР` and tap left Ctrl | `hELLO мир`; surrounding text unchanged |
| Put the caret after `first ghbdtn `, then Alt | `first привет `; trailing space and caret position kept |
| In a terminal or a JetBrains editor, type `ghbdtn`, tap Alt, tap again | `привет`, then `ghbdtn`; the layout switches each time |
| Type `ghbdtn`, Backspace twice, type `tn`, tap Alt | `привет` |
| Type `ghbdtn`, click elsewhere and back, tap Alt | In a UI Automation editor, `привет`; otherwise only the layout switches |
| In a browser address bar, type a word with inline completion, tap Alt | Typed word converted; the completion is removed |
| Select text in Visual Studio Code or a JetBrains editor, tap Alt; tap Alt with no selection | Selection converted; with no selection, the line is not duplicated |
| Convert a word after an emoji; invert selected `straße` | Correct text; caret directly after the expanded `STRASSE` |
| Select several lines and convert/invert | Line breaks and surrounding content kept |
| Press Alt+Tab, Ctrl+C/V, Ctrl+A, Alt+Shift, right Ctrl, Win, or hold Alt for more than a second | Nothing extra happens on release |
| Tap Alt in Explorer, on the desktop, in a browser page with nothing editable | The layout switches; no text is changed, no menu opens |
| Undo after a conversion | The editor restores its previous text |
| Copy text, an image, or files before converting; check Win+V afterwards | Clipboard restored; no temporary entry from a UI Automation replacement |
| Make a new copy during a pending conversion | The newer clipboard content is not overwritten |
| Type/click/switch windows during pending work | The pending replacement stops; nothing is inserted into a different window |
| Try a password field in a dialog and in a browser | No text conversion; Alt only switches the layout |
| Focus a window running as administrator | Nothing happens there; KeyFlip works again in other windows |
| Use a remote desktop or virtual machine window | Only the local layout switches |
| On a layout with AltGr, type AltGr characters and tap AltGr | No conversion |
| Lock and unlock the session, answer a UAC prompt, then tap | Gestures still work |
| Press Caps Lock alone in an editor, a browser field and a terminal; then Shift+Caps Lock twice | The layout switches each time, no text changes, capitals stay off; with Shift, capitals turn on and off again |
| Hold Caps Lock for a second; hold it again | Capitals turn on while the key is still down, and off the second time; the layout ends as it was |
| Untick "Caps Lock only switches the layout" | Caps Lock is the regular Caps Lock again, at once |
| Choose Win under "Convert layout with a tap of"; type `ghbdtn`, tap the Windows key alone; tap it again | `привет`, then `ghbdtn`; the layout follows; the Start menu does not open; an Alt tap converts nothing |
| With Win chosen: Win+E, Win+V, Win+Shift+S, then hold the Windows key for more than a second | The shortcuts work as in Windows; the held key opens the Start menu on release |
| Pause and resume | The tray icon turns grey and back; gestures stop and resume; Caps Lock is the regular Caps Lock while paused |
| Tick Start when I sign in, then sign out and in | One copy starts from the intended path |
| Run `KeyFlip.exe` twice; use the Settings window that opens; run `KeyFlip.exe --quit` | The second launch opens Settings of the first, and the window works; `--quit` stops the app at once |
| Open Settings on displays at 100% and 150% scale | Nothing is clipped or misaligned |

If a paste cannot be confirmed, inspect the text before retrying. KeyFlip deliberately avoids a second automatic paste.

## Recording a compatibility issue

Include the KeyFlip version, Windows version, application/version, field type, selected-text versus last-word mode, and the status message from KeyFlip Settings. Use a small invented input and its expected/actual output; do not include private draft text or clipboard contents.
