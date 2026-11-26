using System;

namespace KeyFlip.Core
{
    /// Saved clipboard contents. `SequenceNumber` is the clipboard's sequence number
    /// once saving has finished: reading delay-rendered data can itself advance it.
    public abstract class ClipboardSnapshot
    {
        public uint SequenceNumber;
    }

    /// A write that emptied the clipboard and then failed. `SequenceNumber` is the
    /// clipboard's number after it, so the writer still knows the contents as its own.
    public sealed class PartialClipboardWrite : AppFailure
    {
        public readonly uint SequenceNumber;

        public PartialClipboardWrite(uint sequenceNumber) : base("The clipboard could not be restored.")
        {
            SequenceNumber = sequenceNumber;
        }
    }

    /// The operations KeyFlip needs from the system clipboard. Every write returns the
    /// sequence number after it, because one logical change advances the number several times.
    public interface IClipboard
    {
        uint SequenceNumber { get; }

        /// Saves every readable representation without changing the contents.
        /// Throws `AppFailure` when the contents cannot be preserved.
        ClipboardSnapshot Capture();

        /// Replaces the contents with temporary text.
        uint SetTemporaryText(string text);

        /// Replaces the contents with a snapshot.
        uint Restore(ClipboardSnapshot snapshot);

        /// The plain text on the clipboard, or null.
        string GetText();
    }

    /// Borrows the clipboard: saves it, lets KeyFlip put temporary text on it, and
    /// restores it afterwards. It never overwrites a change it did not make.
    public sealed class ClipboardLease : IDisposable
    {
        public const string ChangedMessage = "The clipboard changed. Try again.";

        private readonly IClipboard clipboard;
        private readonly ClipboardSnapshot saved;
        private uint? owned;

        /// Saves the clipboard without changing it.
        public ClipboardLease(IClipboard clipboard)
        {
            this.clipboard = clipboard;
            saved = clipboard.Capture();
        }

        /// Saves the clipboard and stages `text`.
        public ClipboardLease(IClipboard clipboard, string text) : this(clipboard)
        {
            Stage(text);
        }

        /// The sequence number when the clipboard was saved.
        public uint SavedSequenceNumber { get { return saved.SequenceNumber; } }

        public bool ChangedSinceSaved { get { return clipboard.SequenceNumber != saved.SequenceNumber; } }

        /// Whether the clipboard currently holds contents this lease must put back.
        public bool HoldsTemporaryContents { get { return owned.HasValue; } }

        /// Marks the current contents as temporary, e.g. after a synthetic copy.
        /// Returns the sequence number that was claimed.
        public uint Claim()
        {
            uint number = clipboard.SequenceNumber;
            owned = number;
            return number;
        }

        /// Puts `text` on the clipboard, unless someone else changed it since it was saved or claimed.
        public void Stage(string text)
        {
            uint expected = owned ?? saved.SequenceNumber;
            if (clipboard.SequenceNumber != expected) throw new AppFailure(ChangedMessage);
            try
            {
                owned = clipboard.SetTemporaryText(text);
            }
            catch (Exception)
            {
                // A failed write may have emptied the clipboard already.
                if (clipboard.SequenceNumber != expected)
                {
                    owned = clipboard.SequenceNumber;
                    Restore();
                }
                throw new AppFailure("Could not prepare the text for insertion.");
            }
        }

        /// Puts the saved contents back if the clipboard still holds this lease's
        /// temporary contents. Returns false when a restore was needed and failed;
        /// it can then be called again.
        public bool Restore()
        {
            if (!owned.HasValue || clipboard.SequenceNumber != owned.Value)
            {
                owned = null;
                return true;
            }
            try
            {
                clipboard.Restore(saved);
            }
            catch (PartialClipboardWrite partial)
            {
                // The clipboard holds this lease's unfinished write; a later call can finish it.
                owned = partial.SequenceNumber;
                return false;
            }
            catch (Exception)
            {
                return false;
            }
            owned = null;
            return true;
        }

        public void Dispose()
        {
            Restore();
        }
    }
}
