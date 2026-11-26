# KeyFlip for Windows — specification

Version 0.1.0. A Windows port of [KeyFlip for macOS](https://github.com/pyJIoH/KeyFlip) (0.4.0):
the same two gestures, the same rules for what text is changed, and the same
replacement strategies, implemented with Windows facilities.

## 1. Purpose

KeyFlip is a small offline tray app that fixes text typed with the wrong
English/Russian keyboard layout and inverts letter case.

| Tap a key by itself | macOS key | Action | Example |
| --- | --- | --- | --- |
| Left or right **Alt**, or left or right **Win** (a setting; Alt by default) | Option | Convert English ↔ Russian and switch the keyboard layout | `ghbdtn` ↔ `привет` |
| **Left Ctrl** | Left Control | Invert each letter's case | `Hello ПРИВЕТ` ↔ `hELLO привет` |
| **Caps Lock** (optional, on by default) | none | Switch the keyboard layout only | English ↔ Russian |

Right Ctrl (the counterpart of right Control) never triggers an action. Of Alt
and the Windows key, only the one chosen as the conversion key does. On a
keyboard that is shared with a Mac, the Windows key is the key that is Option
on the Mac, so choosing Win keeps the gesture on the same physical key on both
systems.

### Goals

- Behave like the macOS app wherever Windows allows it.
- No third-party dependencies, no network access, no installer. One `.exe`
  that runs on a stock Windows 10/11 machine.
- Build and test with what Windows ships: the .NET Framework 4.8 C# compiler.
- Never make text worse: every replacement is checked before and, where the
  editor allows it, after; nothing is retried automatically.

### Non-goals

- Languages other than English and Russian; automatic (as-you-type) correction.
- Configurable hotkeys, beyond the choice between Alt and Win for the
  conversion gesture.
- Working inside elevated (administrator) windows while KeyFlip itself is not
  elevated, or on the secure desktop (UAC prompts, lock screen).

## 2. Gestures

A gesture is a complete press and release of one trigger key with nothing in
between.

- The key must be pressed while no other modifier (Shift, Ctrl, Alt, Win) is held.
- It must be released within **1.0 s**. Auto-repeat while the key is held is ignored.
- Any other key press, mouse button press, wheel movement, modifier change or
  lock-key press between the press and the release cancels the gesture. So do
  focus changes and a replacement that is still in progress: a tap that
  overlaps a replacement, at its press or at its release, starts no action.
- A gesture is ignored while a mouse button is held (for example Ctrl during a drag).
- After a cancelled gesture, nothing is detected until all modifiers are released.
- Ordinary shortcuts (Alt+Tab, Ctrl+C, Alt+Shift, AltGr combinations) keep working
  and never trigger an action. On layouts with AltGr, right Alt is AltGr and
  does not trigger.

Modifier state is taken from the system at every event instead of being
accumulated, so a key-up that Windows never delivered (UAC prompt, lock screen)
cannot leave the detector stuck.

Only real transitions count: a press of a modifier that is already down
(auto-repeat) and a release of one that is not down are ignored. The latter
matters because Windows sends a release of left Ctrl (scan code `0x21D`)
whenever the keyboard layout changes, which would otherwise look like input in
the middle of KeyFlip's own replacement. The same synthetic left Ctrl is what
Windows adds to AltGr; it changes the modifier state but can never start or
finish a gesture.

**The conversion key: Alt or Win.** Settings offers the choice; Alt is the
default. With Win chosen, a lone tap of either Windows key is the gesture and
Alt is an ordinary key again; with Alt chosen it is the other way round.

**Menus.** Windows activates a window's menu bar when Alt is pressed and
released alone, and opens the Start menu when a Windows key is. When a tap of
the conversion key is accepted, KeyFlip injects an unassigned key (`0xE8`)
from inside the keyboard hook, so that it is handled before the release is.
The release then no longer ends a lone press, and neither menu opens. A tap
that started no action because it overlapped a replacement is masked in the
same way. Holding the key longer than one second still opens the menu, as do
F10 for the menu bar and Ctrl+Esc for the Start menu. While KeyFlip is paused,
and in elevated windows, nothing is masked.

The action starts 40 ms after the release, so the application has processed it.

**Caps Lock as the layout switch.** While this is turned on (the default), a press
of Caps Lock with no modifier held toggles between the two chosen layouts and
does nothing else:

- The press, its auto-repeat and its release are not passed on, so the Caps
  Lock state does not change and no application sees the key. With Shift, Ctrl,
  Alt or Win held, the key is the regular Caps Lock.
- The layout is switched on the press, without the 40 ms delay. No text is
  read or changed, in any kind of window, including password fields and
  terminals.
- A press that lasts **0.5 s** is the regular Caps Lock instead, as on macOS:
  KeyFlip toggles the Caps Lock state with a synthetic key press and selects
  the layout that was active before the press. This happens once per press:
  while the key is still down, at the first auto-repeat after 0.5 s, or at the
  release if no auto-repeat came in time.
- The layout is switched at the press and not at the release, so that a key
  typed right after Caps Lock, even before Caps Lock is released, is already in
  the new layout. The price is that a long press switches the layout and
  switches it back.
- A key-down that follows more than 1.5 s without any Caps Lock event is a new
  press: auto-repeat never pauses that long, so the earlier release was lost
  (lock screen, elevated window).
- Because no application sees the key, it is not treated as input: the tracked
  word is kept (an Alt tap afterwards still converts it), and an action that is
  under way is not cancelled; the switch happens after it.
- It has no effect where KeyFlip receives no input: while paused, and in
  elevated windows. There the key is the regular Caps Lock.

## 3. What text is changed

- Selected text has priority. Otherwise the target is the **last word**: the
  whitespace-delimited text before the caret, including attached punctuation.
- The caret may be separated from the word by up to **8** spaces, but never by a
  tab or a line break. Trailing spaces and the caret position after them are kept.
- A word is at most **256** UTF-16 units. A selection is at most **100,000**.
- After replacing a selection the caret is placed after it; it is not reselected.

Layout conversion:

- It maps physical key positions between the two chosen layouts. It is not
  transliteration. The table is built from the installed layouts over the
  printable main block (scan codes `02–0D`, `10–1B`, `1E–29`, `2B–35`, `56`, `39`),
  unshifted and shifted; the first key that produces a character wins.
- Direction comes from the text: Latin letters → Russian, Cyrillic → English.
  Text with both alphabets is rejected. Text with neither (digits, punctuation)
  follows the active layout.
- Characters outside the table, including emoji, are preserved.
- The keyboard layout is switched to the target layout. In an empty field, or
  when no text can be found, only the layout is toggled and the status says so.

Case inversion uses Unicode simple case mapping per code point, plus the common
expansions (`ß` → `SS`, Latin ligatures). It works on mixed-language text and
is not reversible for characters that expand.

## 4. Replacement strategies

Tried in this order.

1. **A word typed just now.** KeyFlip tracks the characters typed since the last
   caret-moving input. It sends one Backspace per character (word and trailing
   spaces) and types the converted word as Unicode characters, so the result
   does not depend on the layout the application is using. No clipboard, no UI
   Automation: this works in terminals, IDEs and Electron editors.
   If the editor exposes its text, KeyFlip first checks that the text before
   the caret is exactly the tracked word at a word boundary; if not, strategy 2
   is used. An application-made selection after the caret (inline completion in
   an address bar) costs one extra Backspace. After a successful retype the
   buffer holds the new word, so a second tap converts it back.
2. **UI Automation.** For a selection, or a word that was not typed just now.
   KeyFlip reads the focused editor through the UI Automation Text pattern,
   selects the word (a selection is used as is), waits for the editor to confirm
   the selection, pastes the replacement with **Shift+Insert**, and verifies that
   the text before the caret ends with it. For a word, the caret is then moved
   back behind its trailing spaces; if that cannot be confirmed, the conversion
   still counts and the status line says so. An unconfirmed paste is reported
   and never repeated.
3. **Copy and paste.** When the editor does not expose its selection, KeyFlip
   copies with **Ctrl+Insert**, converts, and pastes with **Shift+Insert**. If
   nothing is copied within 400 ms, no text is changed. A single copied line
   ending in a line break is rejected: code editors copy the current line when
   nothing is selected. It is not tried where it cannot help: in terminals,
   when the focused control reports read-only text or is not an editor at all
   (a list, a button, a menu), or when the editor reports a caret with nothing
   selected. A selection gesture made just before the tap (drag, double-click,
   Shift+click, Shift+navigation, Ctrl+A) overrides the last two, because some
   editors expose an empty stand-in for their real text.
4. **Otherwise** only the layout is toggled (Alt), or a status message explains
   that there is nothing to change (left Ctrl).

Ctrl+Insert and Shift+Insert are used instead of Ctrl+C and Ctrl+V because they
have the same meaning in practically every Windows editor and can never send an
interrupt to a console program.

Before injecting anything KeyFlip waits (up to 250 ms) for all physical
modifiers to be released, and it verifies that the foreground window and the
input-activity counter are unchanged. Any real input during a pending action
cancels it before text is touched.

### The typed-word buffer

- Holds the current word (≤ 256 characters) and the spaces typed after it
  (≤ 8), in memory only. It is replaced when the next word starts.
- A key press is recorded only when Ctrl, Alt and Win are up, the active layout
  is one of the two chosen layouts, and the key produces exactly one printable
  character. Backspace removes the last character. Unicode input injected by
  other tools is recorded as typed.
- Anything else clears it: mouse button presses, navigation and editing keys,
  Enter, Tab, Escape, shortcuts, dead keys, input methods, a change of the
  focused window or control, pausing, and any input during a replacement.
  Volume, media and Print Screen keys are ignored.
- It is never filled in classic password controls, and it is cleared when a
  password field is detected. While a browser or Electron application is still
  starting its accessibility support (up to 1.5 s after KeyFlip's first
  contact with it), a tracked word is not retyped until the application can say
  whether the field is a password field.

### Clipboard handling (strategies 2 and 3)

- Before use, every representation that can be read as global memory or as an
  enhanced metafile is saved; afterwards it is restored in the original order.
  If the clipboard holds data but none of it can be saved, or it exceeds
  256 MB, KeyFlip stops before inserting anything.
- After the synthetic copy of strategy 3, the copied text is read first and the
  copy is taken as KeyFlip's own only once the sequence number has stopped
  moving: applications that render their text on request, or that are still
  writing, advance it after the first change.
- A restore that fails because the clipboard is busy is tried again for about
  two seconds.
- A clipboard that changed for any reason other than KeyFlip's own writes is
  never overwritten. Ownership is tracked with the clipboard sequence number,
  read after the clipboard is closed (Windows adds synthesized formats on close).
- Temporary text is tagged `ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory=0` and `CanUploadToCloudClipboard=0`, so
  clipboard history and cloud sync skip it. Restored content carries the same
  tags to avoid a duplicate history entry.
- After an unverifiable paste (strategy 3) the text stays available for 0.8 s
  before the clipboard is restored. A copy that arrives after KeyFlip stopped
  waiting is undone if it arrives within 0.6 s and no user input happened.
- Not restored: delay-rendered OLE data that cannot be read as memory (embedded
  objects, virtual files) and GDI bitmap handles (the device-independent bitmap
  is restored instead).

## 5. Keyboard layouts

- Candidates are the loaded layouts whose input language is English or Russian.
  The user picks one of each; the defaults are the first of each language. The
  choice is stored by layout handle (`HKL`, 8 hex digits).
- The active layout is that of the thread owning the focused control. A classic
  console window reports the thread of the program running in it, which has no
  layout; there the thread that owns the window's input method window is used.
- Switching posts `WM_INPUTLANGCHANGEREQUEST` to the focused control (then to
  the foreground window) and waits up to 0.5 s for the thread's layout to
  change. A failed switch is reported; converted text is left in place.
- Key translation uses `ToUnicodeEx` with an explicit key state and the
  "do not change keyboard state" flag, so it never disturbs dead-key state.

## 6. Excluded contexts

| Context | Behavior |
| --- | --- |
| Password fields (UI Automation `IsPassword`, or an `Edit` with `ES_PASSWORD`) | No text is read, converted or retyped. An Alt tap only toggles the layout. |
| Terminals (console and terminal window classes and processes, terminal panes of IDEs) | Strategy 1 only. Selected terminal text is usually output. |
| Virtual machines and remote desktops | Only the local layout is toggled; keys would act inside another system. |
| Elevated windows, while KeyFlip is not elevated | Windows does not deliver the input. Nothing happens. |
| Secure desktop | Not reachable. |

## 7. Privacy

- No network access. Nothing typed is written to disk or kept as history.
- Windows delivers every key press to a low-level keyboard hook, including in
  password fields that KeyFlip cannot recognize (for example a console password
  prompt). Such characters can sit in the in-memory word buffer until the next
  clearing event; they are never stored or sent anywhere.
- Editor text is read only when an action is requested.
- UI Automation queries make Chromium-based applications enable their
  accessibility support.
- The clipboard is used only by strategies 2 and 3, as described above. A copy
  made by strategy 3 is written by the application itself and can appear in
  clipboard history.

## 8. User interface

- **Tray icon** (grey while paused). Tooltip and menu show the status line.
  Menu: title, gesture hint, status, Pause/Resume, Settings…, Quit. A left
  click opens Settings.
- **Settings window**: active/paused badge, the two gestures with examples,
  English and Russian layout pickers with Refresh and a link to Windows language
  settings, the conversion key (Alt or Win), "Caps Lock only switches the
  layout", "Start when I sign in", a
  "Test conversion" group (pick a window,
  then run either action in it through the normal pipeline), the last detected
  gesture, the active layout, the status line, Pause/Resume and Quit.
- Settings opens on first launch. Launching a second copy opens the Settings
  window of the running one and exits.
- Failures play the system default sound and set the status line.
- Stored state (`HKCU\Software\KeyFlip`): the two layout ids, the conversion
  key, the Caps Lock choice and a first-run flag. "Start when I sign in" is a value under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Pause is not persisted.
- `KeyFlip.exe --diagnose` prints the version, elevation, the candidate
  layouts and the active layout as JSON. It prints no editor text.
- `KeyFlip.exe --quit` asks the running copy to exit.

## 9. Architecture

Pure logic lives in `KeyFlip.Core` and has no Windows dependencies. The
executable owns platform integration and the UI.

| Component | Responsibility |
| --- | --- |
| `Core/TapDetector.cs` | Modifier press/release state machine; cancellation and timing |
| `Core/CapsLockGesture.cs` | Caps Lock as the layout switch: press, auto-repeat, long press, lost release |
| `Core/WordAtCaret.cs` | Bounded word lookup with UTF-16 ranges |
| `Core/TypedWordBuffer.cs` | In-memory current word; matching against editor text; copied-line detection |
| `Core/Transforms.cs` | Case inversion, alphabet detection, character mapping |
| `Core/KeyMap.cs` | Main-block scan codes; conversion table from two layouts |
| `Core/ClipboardLease.cs` | Save, stage, claim and restore over an abstract clipboard |
| `InputMonitor.cs` | Low-level keyboard and mouse hooks, foreground-change hook, on their own thread |
| `KeySender.cs` | Tagged synthetic input: Backspace, Unicode typing, Ctrl+Insert, Shift+Insert, menu mask |
| `Layouts.cs` | Layout enumeration and names, key translation, switching |
| `TextAccess.cs` | UI Automation: focused editor, selection, word before the caret, verification |
| `WinClipboard.cs` | Win32 clipboard behind the `IClipboard` interface |
| `TextReplacement.cs` | Retype, verified paste, copy-and-paste |
| `Engine.cs` | Coordinates input, strategies, status; the counterpart of `AppModel` |
| `AppCategories.cs` | Terminals and key-forwarding applications |
| `Settings.cs` | Registry-backed settings and start at sign-in |
| `Native.cs` | Win32 declarations |
| `AppIcon.cs` | The keycap icon, drawn at any size; also used by the build to write the `.ico` |
| `TrayApp.cs`, `SettingsForm.cs`, `Program.cs` | Tray icon, settings window, lifecycle, `--diagnose`, `--quit` |

Threads:

- **Input thread.** Owns the hooks and a message loop. A callback only updates
  the detector and the word buffer under a lock and queues an action; the menu
  mask is the only thing sent from here. It never blocks: Windows silently
  removes a low-level hook whose callback is slow.
- **Action thread.** Runs one action at a time: UI Automation, clipboard,
  synthetic input, waiting. Blocking here cannot delay input.
- **UI thread.** Tray icon and settings. A one-second timer refreshes the
  active layout name and reinstalls the hooks if the system reports input that
  the hooks did not see for more than two seconds.

Synthetic events carry a tag in `dwExtraInfo`; the hooks ignore tagged events.
An activity counter is incremented for every real input event; pending work
compares it with the value it started with.

## 10. Differences from the macOS app

| Topic | macOS | Windows |
| --- | --- | --- |
| Trigger keys | Option, left Control | Alt or Win (a setting; Win is where Option is on a keyboard shared with a Mac), left Ctrl |
| Permissions | Accessibility, Input Monitoring | None. Elevated windows need an elevated KeyFlip. |
| Lone trigger key side effect | None | Alt opens the menu bar and Win the Start menu; masked with key `0xE8` |
| Text access | Accessibility (AX) with UTF-16 offsets | UI Automation text ranges; offsets are not available, so ranges are verified by their text |
| Copy / paste keys | Command-C / Command-V | Ctrl+Insert / Shift+Insert |
| Retyped characters | Unicode string on a key event, with the key code | `KEYEVENTF_UNICODE` input; no key code can be attached |
| Clipboard ownership | Change count, "exactly one change" | Sequence number plus "no user input since" (one copy changes the number several times) |
| Clipboard history | Third-party only | Built in; temporary text is tagged to stay out of it |
| Password fields | Secure Input stops key delivery | Detected through UI Automation and `ES_PASSWORD`; see Privacy |
| Electron editors | `AXManualAccessibility` is set on activation | Accessibility starts on the first query; KeyFlip retries for up to 1.5 s after the first contact with such a process |
| Visual Studio Code's editor | Text is exposed once accessibility is on | Text is exposed only in screen reader mode; otherwise typed words (strategy 1) and selections (strategy 3) work, a word typed earlier does not |
| Copy fallback | Tried whenever the selection is not exposed | Also skipped on read-only text and non-editor controls |
| Empty stand-in editors | Not handled | Selection gesture enables the copy strategy |
| Case expansion | Full Unicode | Simple mapping plus a short expansion table |
| Settings store | UserDefaults | Registry |
| Launch at login | SMAppService | `Run` registry value |
| Caps Lock | A macOS setting: a tap switches the input source, a long press is Caps Lock | The same, done by KeyFlip (optional). The layout switches at the press, not at the release, and is put back after a long press |

## 11. Build, tests, tooling

- Language level C# 5, target .NET Framework 4.8, compiled with the in-box
  `csc.exe`. No project files, packages or SDK.
- `scripts\build.cmd` → `dist\KeyFlip.exe` (icon generated at build time by
  `tools\MakeIcon.cs`, manifest with system DPI awareness and `asInvoker`).
  The settings window scales its own layout from the display DPI.
- `scripts\test.cmd` → builds and runs `dist\KeyFlip.Tests.exe`: a
  dependency-free runner for the core scenarios; non-zero exit on failure.
- `scripts\check-platform.cmd [--with-layouts]` → desktop checks that post no
  input and do not touch the user's clipboard: synthetic event construction,
  the real clipboard implementation inside a private window station, UI
  Automation range logic against its own unfocused edit controls, and (with the
  flag) conversion with the installed layouts.
- `scripts\test.cmd --e2e` → optional end-to-end check in its own window:
  classic edit controls, a rich edit control, a password field, and a control
  that exposes no text. It takes the keyboard focus for about twenty seconds,
  injects key presses only while that window is in the foreground, uses the
  real clipboard (and checks that it is restored), and toggles the layout an
  even number of times.
- `dist\KeyFlip.Tests.exe --external <window title> <script file>` → the
  same pipeline in another application's window, driven by a script of key
  presses and expectations (`tests\external`). Keys are pressed only while
  that window is in the foreground.
- `dist\KeyFlip.Tests.exe --render-settings <file.png>` draws the settings
  window without showing it.
- CI (GitHub Actions, `windows-latest`): build and core tests.

## 12. Acceptance

The automated suites pass, and the manual checklist in `docs/TESTING.md`
passes in a native editor (Notepad), a browser field, an Electron editor, and
a terminal. In particular:

1. `ghbdtn` + Alt → `привет`, Russian layout active; Alt again → `ghbdtn`, English.
2. Left Ctrl twice → `GHBDTN`, then `ghbdtn`.
3. A selection inside a sentence is converted alone; the caret ends after it.
4. `first ghbdtn ` + Alt → `first привет `, caret after the space.
5. Alt+Tab, Ctrl+C, Alt+Shift, right Ctrl, Win, and Alt held over one second
   do nothing on release. A lone Alt tap does not open the menu bar.
6. The clipboard holds what it held before, and clipboard history has no
   temporary entry from strategy 2.
7. Typing, clicking or switching windows during a pending action stops it.
8. Password fields are left alone.
9. A tap of Caps Lock switches the layout, changes no text and does not turn
   capital letters on; holding it for half a second, or Shift+Caps Lock, is
   the regular Caps Lock and leaves the layout as it was.
10. With Win as the conversion key: `ghbdtn` + Win → `привет` and the Start
    menu stays closed; Win+E and Win held over one second work as in Windows;
    a lone Alt tap converts nothing.

## 13. Known limitations and open risks

- A quick Alt tap no longer opens the menu bar or Office key tips.
- With Win as the conversion key, a quick tap of a Windows key no longer opens
  the Start menu; a longer press, Ctrl+Esc or the mouse does. Other programs
  that react to a lone Windows key see an extra, unassigned key during the tap.
- While Caps Lock switches the layout, other programs that use Caps Lock alone as
  a hotkey no longer receive it. A press of half a second or more is
  taken as the regular Caps Lock, however it was meant.
- UI Automation fidelity varies. Editors that expose text that differs from
  what is on screen can defeat strategy 2; verification limits the damage to an
  error message. JetBrains IDEs, Office and Google Docs must be checked by
  hand (see `docs/TESTING.md`).
- Editors that expose no text (Visual Studio Code outside screen reader mode,
  terminals) only get a word that was typed just now, or a selection where
  selections can be copied.
- Chromium-based applications may react to accessibility queries (for example
  an editor offering a screen-reader mode).
- A tracked word is trusted when the editor shows no word before the caret,
  because editors that expose a stand-in instead of their text look the same
  (the macOS app does the same). A page that moves the focus to another field
  of the same window while a word is being typed can therefore have that word
  retyped into the new field.
- Applications that ignore `VK_PACKET` Unicode input cannot be retyped into.
- Strategy 3 can leave an entry in clipboard history and cannot confirm its paste.
- Where UI Automation gives no answer and nothing was typed, an Alt tap waits
  0.4 s for a copy before it switches the layout.
- Ctrl is used far more on Windows than Control is on macOS. Pressing left
  Ctrl and releasing it without using it inverts the case of the last word.
