using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace PromptVault
{
    /// <summary>Dibuja el logo (cuadro ámbar con "›_") en cualquier tamaño. Lo usan el .ico del .exe y la bandeja.</summary>
    public static class IconArt
    {
        public static Bitmap Draw(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size;
                float pad = Math.Max(0.5f, s * 0.04f);
                var rect = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
                using (var path = RoundRect(rect, s * 0.26f))
                using (var fill = new SolidBrush(Color.FromArgb(255, 0xF0, 0xB4, 0x4C)))
                {
                    g.FillPath(fill, path);
                }
                float w = Math.Max(1.5f, s * 0.105f);
                using (var pen = new Pen(Color.FromArgb(255, 0x1A, 0x14, 0x05), w))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[] { new PointF(s * 0.27f, s * 0.31f), new PointF(s * 0.46f, s * 0.50f), new PointF(s * 0.27f, s * 0.69f) });
                    g.DrawLine(pen, s * 0.54f, s * 0.69f, s * 0.74f, s * 0.69f);
                }
            }
            return bmp;
        }

        static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static byte[] Png(int size)
        {
            using (var b = Draw(size))
            using (var ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>.ico con entradas PNG (soportado desde Windows Vista).</summary>
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var images = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++) images[i] = Png(sizes[i]);
            using (var fs = new FileStream(path, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim);
                    w.Write(dim);
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
            }
        }
    }
}
