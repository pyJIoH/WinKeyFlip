using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using KeyFlip.Core;

namespace KeyFlip
{
    /// The Win32 clipboard. Sequence numbers are always read after the clipboard is
    /// closed: Windows adds synthesized formats on close, which advances the number.
    internal sealed class WinClipboard : IClipboard
    {
        private sealed class Snapshot : ClipboardSnapshot
        {
            public readonly List<KeyValuePair<uint, byte[]>> Formats = new List<KeyValuePair<uint, byte[]>>();
        }

        public const string UnpreservableMessage =
            "The clipboard cannot be preserved right now. Try copying ordinary text first.";

        private const long MaximumSnapshotBytes = 256L * 1024 * 1024;

        /// Clipboard history, cloud sync and well-behaved clipboard managers skip
        /// contents that carry these formats.
        private static readonly uint ExcludeFromMonitors = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        private static readonly uint IncludeInHistory = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");
        private static readonly uint UploadToCloud = Native.RegisterClipboardFormat("CanUploadToCloudClipboard");

        public uint SequenceNumber { get { return Native.GetClipboardSequenceNumber(); } }

        private static void Open()
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                if (Native.OpenClipboard(IntPtr.Zero)) return;
                Thread.Sleep(10);
            }
            throw new AppFailure("The clipboard is busy. Try again.");
        }

        /// Formats whose handle is a block of global memory. GDI handles are skipped:
        /// Windows rebuilds a bitmap from the saved device-independent bitmap, and a
        /// classic metafile from the saved enhanced metafile.
        private static bool IsMemoryFormat(uint format)
        {
            if (format == Native.CF_BITMAP || format == Native.CF_METAFILEPICT || format == Native.CF_PALETTE
                || format == Native.CF_ENHMETAFILE)
            {
                return false;
            }
            if (format == 0x0080 || format == 0x0082 || format == 0x0083 || format == 0x008E) return false; // Owner display, DSP handles.
            if (format >= 0x0200 && format <= 0x03FF) return false; // Private and GDI object ranges.
            return true;
        }

        public ClipboardSnapshot Capture()
        {
            Snapshot snapshot = new Snapshot();
            Open();
            try
            {
                List<uint> formats = new List<uint>();
                uint format = 0;
                while ((format = Native.EnumClipboardFormats(format)) != 0) formats.Add(format);
                long total = 0;
                foreach (uint id in formats)
                {
                    byte[] data = id == Native.CF_ENHMETAFILE ? ReadMetafile() : IsMemoryFormat(id) ? ReadMemory(id) : null;
                    if (data == null) continue;
                    total += data.Length;
                    if (total > MaximumSnapshotBytes)
                    {
                        throw new AppFailure("The clipboard is too large to preserve. Copy something smaller first.");
                    }
                    snapshot.Formats.Add(new KeyValuePair<uint, byte[]>(id, data));
                }
                // Refuse to continue when the clipboard holds something and none of it could be saved.
                if (formats.Count > 0 && snapshot.Formats.Count == 0) throw new AppFailure(UnpreservableMessage);
            }
            finally
            {
                Native.CloseClipboard();
            }
            snapshot.SequenceNumber = Native.GetClipboardSequenceNumber();
            return snapshot;
        }

        private static byte[] ReadMemory(uint format)
        {
            IntPtr handle = Native.GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;
            ulong size = Native.GlobalSize(handle).ToUInt64();
            if (size == 0 || size > int.MaxValue) return null;
            IntPtr pointer = Native.GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;
            try
            {
                byte[] data = new byte[(int)size];
                Marshal.Copy(pointer, data, 0, data.Length);
                return data;
            }
            finally
            {
                Native.GlobalUnlock(handle);
            }
        }

        private static byte[] ReadMetafile()
        {
            IntPtr handle = Native.GetClipboardData(Native.CF_ENHMETAFILE);
            if (handle == IntPtr.Zero) return null;
            uint size = Native.GetEnhMetaFileBits(handle, 0, null);
            if (size == 0) return null;
            byte[] data = new byte[size];
            return Native.GetEnhMetaFileBits(handle, size, data) == size ? data : null;
        }

        /// Hands a copy of `data` to the clipboard, which then owns the memory.
        private static void Write(uint format, byte[] data)
        {
            IntPtr handle;
            if (format == Native.CF_ENHMETAFILE)
            {
                handle = Native.SetEnhMetaFileBits((uint)data.Length, data);
                if (handle == IntPtr.Zero) throw new InvalidOperationException("Metafile could not be rebuilt.");
                if (Native.SetClipboardData(format, handle) == IntPtr.Zero)
                {
                    Native.DeleteEnhMetaFile(handle);
                    throw new InvalidOperationException("SetClipboardData failed.");
                }
                return;
            }
            handle = Native.GlobalAlloc(Native.GMEM_MOVEABLE, new UIntPtr((uint)Math.Max(data.Length, 1)));
            if (handle == IntPtr.Zero) throw new OutOfMemoryException();
            IntPtr pointer = Native.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                Native.GlobalFree(handle);
                throw new InvalidOperationException("GlobalLock failed.");
            }
            Marshal.Copy(data, 0, pointer, data.Length);
            Native.GlobalUnlock(handle);
            if (Native.SetClipboardData(format, handle) == IntPtr.Zero)
            {
                Native.GlobalFree(handle);
                throw new InvalidOperationException("SetClipboardData failed.");
            }
        }

        private static void WriteMonitorExclusions()
        {
            Write(ExcludeFromMonitors, new byte[4]);
            Write(IncludeInHistory, new byte[4]);   // A DWORD of zero: do not include.
            Write(UploadToCloud, new byte[4]);
        }

        public uint SetTemporaryText(string text)
        {
            Open();
            try
            {
                if (!Native.EmptyClipboard()) throw new InvalidOperationException("EmptyClipboard failed.");
                Write(Native.CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
                WriteMonitorExclusions();
            }
            finally
            {
                Native.CloseClipboard();
            }
            return Native.GetClipboardSequenceNumber();
        }

        public uint Restore(ClipboardSnapshot saved)
        {
            Snapshot snapshot = (Snapshot)saved;
            Open();
            bool emptied = false;
            try
            {
                if (!Native.EmptyClipboard()) throw new InvalidOperationException("EmptyClipboard failed.");
                emptied = true;
                bool tagged = false;
                foreach (KeyValuePair<uint, byte[]> entry in snapshot.Formats)
                {
                    Write(entry.Key, entry.Value);
                    if (entry.Key == ExcludeFromMonitors || entry.Key == IncludeInHistory || entry.Key == UploadToCloud) tagged = true;
                }
                // The original copy is already in clipboard history; do not add it twice.
                if (snapshot.Formats.Count > 0 && !tagged) WriteMonitorExclusions();
            }
            catch (Exception)
            {
                Native.CloseClipboard();
                // What the clipboard holds now is this unfinished write.
                if (emptied) throw new PartialClipboardWrite(Native.GetClipboardSequenceNumber());
                throw;
            }
            Native.CloseClipboard();
            return Native.GetClipboardSequenceNumber();
        }

        public string GetText()
        {
            Open();
            try
            {
                IntPtr handle = Native.GetClipboardData(Native.CF_UNICODETEXT);
                if (handle == IntPtr.Zero) return null;
                ulong size = Native.GlobalSize(handle).ToUInt64();
                IntPtr pointer = Native.GlobalLock(handle);
                if (pointer == IntPtr.Zero) return null;
                try
                {
                    int maximum = (int)Math.Min(size / 2, int.MaxValue / 2);
                    string text = Marshal.PtrToStringUni(pointer, maximum);
                    int terminator = text.IndexOf('\0');
                    return terminator >= 0 ? text.Substring(0, terminator) : text;
                }
                finally
                {
                    Native.GlobalUnlock(handle);
                }
            }
            finally
            {
                Native.CloseClipboard();
            }
        }

        /// The process that last wrote to the clipboard, or 0 when it did not say.
        public static uint OwnerProcess()
        {
            IntPtr owner = Native.GetClipboardOwner();
            if (owner == IntPtr.Zero) return 0;
            uint processId;
            Native.GetWindowThreadProcessId(owner, out processId);
            return processId;
        }

        /// Format ids on the clipboard right now, for the platform checks.
        public static List<uint> CurrentFormats()
        {
            List<uint> formats = new List<uint>();
            Open();
            try
            {
                uint format = 0;
                while ((format = Native.EnumClipboardFormats(format)) != 0) formats.Add(format);
            }
            finally
            {
                Native.CloseClipboard();
            }
            return formats;
        }

        /// Replaces the clipboard with arbitrary formats, for the platform checks.
        public static void SetRaw(IEnumerable<KeyValuePair<uint, byte[]>> formats)
        {
            Open();
            try
            {
                Native.EmptyClipboard();
                if (formats != null)
                {
                    foreach (KeyValuePair<uint, byte[]> entry in formats) Write(entry.Key, entry.Value);
                }
            }
            finally
            {
                Native.CloseClipboard();
            }
        }

        public static byte[] GetRaw(uint format)
        {
            Open();
            try
            {
                return ReadMemory(format);
            }
            finally
            {
                Native.CloseClipboard();
            }
        }
    }
}
