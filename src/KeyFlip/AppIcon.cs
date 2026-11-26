using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace KeyFlip
{
    /// The application icon: a keycap with the letter «Я», drawn at any size.
    /// This file has no other dependencies: the build also compiles it into the
    /// tool that writes the executable's icon file.
    internal static class AppIcon
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        public static Bitmap Draw(int size, bool active)
        {
            Bitmap bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.TextRenderingHint = size < 32 ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.AntiAlias;
                graphics.Clear(Color.Transparent);

                Color top = active ? Color.FromArgb(74, 144, 255) : Color.FromArgb(150, 155, 164);
                Color bottom = active ? Color.FromArgb(31, 95, 224) : Color.FromArgb(104, 109, 118);
                float inset = size >= 32 ? size / 16f : 0.5f;
                RectangleF bounds = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
                using (GraphicsPath path = RoundedRectangle(bounds, size * 0.22f))
                using (LinearGradientBrush fill = new LinearGradientBrush(
                    new PointF(0, bounds.Top - 1), new PointF(0, bounds.Bottom + 1), top, bottom))
                {
                    graphics.FillPath(fill, path);
                }

                using (FontFamily family = PickFamily())
                using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                using (GraphicsPath letter = new GraphicsPath())
                {
                    // Outline the glyph and centre its real bounds; font metrics would
                    // leave it visibly off-centre at small sizes.
                    letter.AddString("Я", family, (int)FontStyle.Bold, size * 0.62f, new PointF(0, 0), format);
                    RectangleF glyph = letter.GetBounds();
                    using (Matrix move = new Matrix())
                    {
                        move.Translate((size - glyph.Width) / 2f - glyph.Left, (size - glyph.Height) / 2f - glyph.Top);
                        letter.Transform(move);
                    }
                    graphics.FillPath(Brushes.White, letter);
                }
            }
            return bitmap;
        }

        private static FontFamily PickFamily()
        {
            try
            {
                return new FontFamily("Segoe UI");
            }
            catch (ArgumentException)
            {
                return new FontFamily(GenericFontFamilies.SansSerif);
            }
        }

        private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// An icon that owns its handle.
        public static Icon Create(int size, bool active)
        {
            using (Bitmap bitmap = Draw(size, active))
            {
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon borrowed = Icon.FromHandle(handle))
                    {
                        return (Icon)borrowed.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }
    }
}
