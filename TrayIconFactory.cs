using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PrintAgent
{
    /// <summary>
    /// Genera íconos de bandeja con un indicador de estado (círculo de color)
    /// superpuesto al ícono base, sin depender de archivos .ico externos.
    /// </summary>
    internal static class TrayIconFactory
    {
        public static Icon Build(Color statusColor)
        {
            using var baseIcon = (Icon)SystemIcons.Application.Clone();
            using var bmp = baseIcon.ToBitmap();
            using var canvas = new Bitmap(bmp.Width, bmp.Height);

            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);

                int dotSize = (int)(bmp.Width * 0.42);
                var rect = new Rectangle(bmp.Width - dotSize, bmp.Height - dotSize, dotSize, dotSize);

                using var fill = new SolidBrush(statusColor);
                using var outline = new Pen(Color.Black, 1f);
                g.FillEllipse(fill, rect);
                g.DrawEllipse(outline, rect);
            }

            IntPtr hIcon = canvas.GetHicon();
            return Icon.FromHandle(hIcon);
        }
    }
}
