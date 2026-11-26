using System;
using System.Threading;
using KeyFlip.Core;

namespace KeyFlip
{
    /// The editor refused the UI Automation selection request; nothing was changed.
    internal sealed class SelectionRejected : AppFailure
    {
        public SelectionRejected()
            : base("This editor does not allow selecting the recent word. Select it manually first.")
        {
        }
    }

    internal enum ClipboardOutcome { NothingCopied, Unchanged, Replaced }

    /// Performs replacements. Every method blocks and must run on the action thread.
    internal sealed class TextReplacement
    {
        public const string ModifiersHeldMessage = "Release the modifier keys and try again.";

        private readonly TextAccess access;
        private readonly IClipboard clipboard;
        private readonly Func<long> activityVersion;
        private volatile bool busy;
        private volatile ClipboardLease activeLease;
        private int leaseGeneration;

        public TextReplacement(TextAccess access, IClipboard clipboard, Func<long> activityVersion)
        {
            this.access = access;
            this.clipboard = clipboard;
            this.activityVersion = activityVersion;
        }

        /// True while synthetic input or a paste is in flight. Real input arriving
        /// meanwhile makes the editor's contents unpredictable.
        public bool Busy { get { return busy; } }

        public void Shutdown()
        {
            ClipboardLease lease = activeLease;
            if (lease != null) Release(lease);
            activeLease = null;
        }

        private static AppFailure InProgress()
        {
            return new AppFailure("A replacement is already in progress.");
        }

        // ---- Strategy 1: retype ----

        /// Removes `count` characters before the caret with Backspace and types `text`.
        /// This needs no UI Automation, so it works in any editor that accepts typing.
        public void Retype(int count, string text)
        {
            if (busy) throw InProgress();
            Native.INPUT[] deletions = KeySender.Backspaces(count);
            Native.INPUT[] typing = KeySender.Typing(text);
            Native.INPUT[] events = new Native.INPUT[deletions.Length + typing.Length];
            deletions.CopyTo(events, 0);
            typing.CopyTo(events, deletions.Length);
            busy = true;
            try
            {
                KeySender.SendPaced(events);
            }
            finally
            {
                busy = false;
            }
        }

        // ---- Strategy 2: UI Automation selection and a verified paste ----

        private bool Unchanged(TextTarget target, System.Windows.Automation.Text.TextPatternRange selection)
        {
            return access.StillFocused(target) && access.SelectionIs(target, selection)
                && access.TextIs(target.Range, target.Text);
        }

        /// Returns false when the text was replaced but the caret could not be put
        /// back behind the spaces that follow the word.
        public bool Replace(TextTarget target, string text)
        {
            if (busy) throw InProgress();
            long version = activityVersion();
            if (!Unchanged(target, target.OriginalSelection))
            {
                throw new AppFailure("The target changed. No text was replaced.");
            }
            busy = true;
            try
            {
                if (!target.WasSelected)
                {
                    if (!access.Select(target.Range)) throw new SelectionRejected();
                    if (!WaitForSelection(target, version))
                    {
                        RestoreSelectionIfUnchanged(target, version);
                        throw new AppFailure("The editor did not confirm the word selection. No replacement was sent.");
                    }
                }
                // Web editors may publish a selection before their renderer has finished
                // handling it. Allow that request to settle, then revalidate.
                Thread.Sleep(80);
                if (activityVersion() != version || !Unchanged(target, target.Range))
                {
                    throw new AppFailure("The target changed before replacement. Try again.");
                }
                // Use the editor's normal paste action, including its input events and Undo
                // handling, rather than writing through UI Automation.
                ClipboardLease lease = new ClipboardLease(clipboard, text);
                Interlocked.Increment(ref leaseGeneration);
                activeLease = lease;
                try
                {
                    if (activityVersion() != version || !Unchanged(target, target.Range))
                    {
                        throw new AppFailure("The target changed before pasting. Try again.");
                    }
                    if (!KeySender.WaitForModifiersReleased(250)) throw new AppFailure(ModifiersHeldMessage);
                    // A modifier that had to be waited for is input, too.
                    if (activityVersion() != version) throw new AppFailure("The target changed before pasting. Try again.");
                    KeySender.Send(KeySender.Paste());
                }
                catch (AppFailure)
                {
                    Release(lease);
                    activeLease = null;
                    RestoreSelectionIfUnchanged(target, version);
                    throw;
                }
                return Verify(target, text, version, lease);
            }
            finally
            {
                busy = false;
            }
        }

        private bool WaitForSelection(TextTarget target, long version)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (activityVersion() != version || !access.StillFocused(target)) return false;
                if (access.SelectionIs(target, target.Range)) return true;
                Thread.Sleep(25);
            }
            return false;
        }

        private void RestoreSelectionIfUnchanged(TextTarget target, long version)
        {
            if (!target.WasSelected && activityVersion() == version && Unchanged(target, target.Range))
            {
                access.Select(target.OriginalSelection);
            }
        }

        private bool Verify(TextTarget target, string text, long version, ClipboardLease lease)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Thread.Sleep(50);
                if (activityVersion() != version || !access.StillFocused(target))
                {
                    // Keep the temporary clipboard briefly available to an in-flight paste.
                    Thread.Sleep(500);
                    Release(lease);
                    activeLease = null;
                    throw new AppFailure("Input changed during replacement. Check the result before continuing.");
                }
                if (!access.Replaced(target, text)) continue;
                // For the word before the caret, put the caret back after its trailing spaces.
                bool positioned = target.WasSelected || target.Trailing.Length == 0
                    || access.MoveCaretForward(target, target.Trailing.Length, text + target.Trailing);
                Release(lease);
                activeLease = null;
                return positioned;
            }
            Release(lease);
            activeLease = null;
            RestoreSelectionIfUnchanged(target, version);
            throw new AppFailure("The editor did not confirm the pasted text. Check the result; it will not be pasted again automatically.");
        }

        // ---- Strategy 3: copy and paste ----

        /// For editors that do not expose their selection: copies it with Ctrl+Insert,
        /// pastes `transform(selection)` with Shift+Insert, then restores the clipboard.
        /// `completed` runs as soon as the outcome is known, before the clipboard is
        /// restored. It is not called when the method throws.
        public void ReplaceSelectionUsingClipboard(Func<string, string> transform, Action<ClipboardOutcome> completed)
        {
            if (busy) throw InProgress();
            long version = activityVersion();
            FocusInfo focus = Native.GetFocusInfo();
            if (!KeySender.WaitForModifiersReleased(250)) throw new AppFailure(ModifiersHeldMessage);
            ClipboardLease lease = new ClipboardLease(clipboard);
            // Waiting for a modifier or for the clipboard's owner leaves time for other input.
            if (activityVersion() != version || Native.GetForegroundWindow() != focus.Foreground)
            {
                throw new AppFailure("Input changed. No text was replaced.");
            }
            int generation = Interlocked.Increment(ref leaseGeneration);
            KeySender.Send(KeySender.Copy());
            busy = true;
            activeLease = lease;
            try
            {
                bool copied = false;
                for (int attempt = 0; attempt < 20 && !copied; attempt++)
                {
                    Thread.Sleep(20);
                    copied = lease.ChangedSinceSaved;
                    if (activityVersion() != version || Native.GetForegroundWindow() != focus.Foreground)
                    {
                        // The user did something meanwhile. Take back only a change that
                        // the target application made.
                        if (copied && OwnedBy(focus))
                        {
                            lease.Claim();
                            Release(lease);
                        }
                        else if (!copied)
                        {
                            WatchForLateCopy(lease, generation, focus);
                        }
                        throw new AppFailure("Input changed during replacement. No text was replaced.");
                    }
                }
                if (!copied)
                {
                    WatchForLateCopy(lease, generation, focus);
                    completed(ClipboardOutcome.NothingCopied);
                    return;
                }
                string text;
                try
                {
                    text = ReadCopy(lease);
                }
                catch (Exception)
                {
                    // Unreadable, but still the copy this method asked for.
                    lease.Claim();
                    Release(lease);
                    throw;
                }
                if (string.IsNullOrEmpty(text) || CopiedSelection.IsLikelyWholeLineCopy(text))
                {
                    Release(lease);
                    completed(ClipboardOutcome.NothingCopied);
                    return;
                }
                string output;
                try
                {
                    if (text.Length > TextRules.MaximumSelectionLength) throw new AppFailure("The selection is too long to convert.");
                    output = transform(text);
                    if (output != text)
                    {
                        if (activityVersion() != version || Native.GetForegroundWindow() != focus.Foreground)
                        {
                            throw new AppFailure("Input changed during replacement. No text was replaced.");
                        }
                        lease.Stage(output);
                        KeySender.Send(KeySender.Paste());
                    }
                }
                catch (Exception)
                {
                    Release(lease);
                    throw;
                }
                if (output == text)
                {
                    Release(lease);
                    completed(ClipboardOutcome.Unchanged);
                    return;
                }
                // Typing may be tracked again; the next action still waits for the clipboard.
                busy = false;
                try
                {
                    completed(ClipboardOutcome.Replaced);
                }
                finally
                {
                    // The editor cannot confirm the paste; keep the text available while it is handled.
                    Thread.Sleep(800);
                    Release(lease);
                }
            }
            finally
            {
                activeLease = null;
                busy = false;
            }
        }

        /// Reads the copied text and marks the copy as temporary. The first change of
        /// the sequence number is not the end of a copy: the owner may still be writing,
        /// or may render its text only when it is asked for, and both advance the number.
        private string ReadCopy(ClipboardLease lease)
        {
            string text = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                uint before = clipboard.SequenceNumber;
                text = clipboard.GetText();
                bool settled = lease.Claim() == before;
                if (settled && !string.IsNullOrEmpty(text)) break;
                Thread.Sleep(20);
            }
            return text;
        }

        /// Puts the saved clipboard back. When the clipboard is busy just now, tries
        /// again a few times over the next two seconds.
        private static void Release(ClipboardLease lease)
        {
            if (lease.Restore()) return;
            int attempts = 0;
            Timer timer = null;
            timer = new Timer(delegate
            {
                bool done = true;
                try
                {
                    done = lease.Restore() || ++attempts >= 5;
                }
                catch (Exception)
                {
                }
                if (done) timer.Dispose();
                else timer.Change(400, Timeout.Infinite);
            }, null, 400, Timeout.Infinite);
        }

        private static bool OwnedBy(FocusInfo focus)
        {
            uint owner = WinClipboard.OwnerProcess();
            if (owner == 0) return false;
            uint foregroundProcess;
            Native.GetWindowThreadProcessId(focus.Foreground, out foregroundProcess);
            return owner == focus.ProcessId || owner == foregroundProcess;
        }

        /// A copy may land after KeyFlip stopped waiting for it. If the clipboard changes
        /// shortly afterwards and either no user input happened or the target application
        /// wrote it, it is that copy: restore the original.
        private void WatchForLateCopy(ClipboardLease lease, int generation, FocusInfo focus)
        {
            long version = activityVersion();
            Timer timer = null;
            timer = new Timer(delegate
            {
                try
                {
                    // A newer lease owns the clipboard now.
                    if (Volatile.Read(ref leaseGeneration) != generation) return;
                    if (lease.ChangedSinceSaved && (activityVersion() == version || OwnedBy(focus)))
                    {
                        lease.Claim();
                        Release(lease);
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    if (timer != null) timer.Dispose();
                }
            }, null, 600, Timeout.Infinite);
        }
    }
}
