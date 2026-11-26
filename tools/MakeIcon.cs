using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace KeyFlip.Tools
{
    /// Build step: writes the executable's icon file from the drawing in AppIcon.cs.
    /// Usage: MakeIcon.exe <output.ico>
    internal static class MakeIcon
    {
        private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Usage: MakeIcon <output.ico>");
                return 2;
            }
            byte[][] images = new byte[Sizes.Length][];
            for (int i = 0; i < Sizes.Length; i++)
            {
                using (Bitmap bitmap = AppIcon.Draw(Sizes[i], true))
                {
                    // Large images are stored as PNG, small ones as classic bitmaps.
                    images[i] = Sizes[i] >= 256 ? Png(bitmap) : Dib(bitmap);
                }
            }
            using (FileStream stream = File.Create(args[0]))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1); // Icon.
                writer.Write((ushort)Sizes.Length);
                int offset = 6 + 16 * Sizes.Length;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    writer.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                    writer.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);  // Planes.
                    writer.Write((ushort)32); // Bits per pixel.
                    writer.Write(images[i].Length);
                    writer.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) writer.Write(image);
            }
            return 0;
        }

        private static byte[] Png(Bitmap bitmap)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        /// A 32-bit bitmap with an (empty) transparency mask, as icon files store it.
        private static byte[] Dib(Bitmap bitmap)
        {
            int size = bitmap.Width;
            int maskStride = ((size + 31) / 32) * 4;
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(40);          // Header size.
                writer.Write(size);
                writer.Write(size * 2);    // Image and mask.
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(0);           // Uncompressed.
                writer.Write(size * size * 4 + maskStride * size);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row = new byte[size * 4];
                    for (int y = size - 1; y >= 0; y--) // Bottom-up.
                    {
                        Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, row.Length);
                        writer.Write(row);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                writer.Write(new byte[maskStride * size]);
                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
