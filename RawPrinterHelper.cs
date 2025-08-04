using System;
using System.Runtime.InteropServices;
using System.Drawing.Printing;
using System.IO;

namespace PrintAgent
{

public class RawPrinterHelper
{
    [DllImport("winspool.Drv", EntryPoint="OpenPrinterA", SetLastError=true)]
    static extern bool OpenPrinter(string szPrinter, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.Drv", EntryPoint="ClosePrinter")]
    static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint="StartDocPrinterA")]
    static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] ref DOCINFOA di);

    [DllImport("winspool.Drv", EntryPoint="EndDocPrinter")]
    static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint="StartPagePrinter")]
    static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint="EndPagePrinter")]
    static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint="WritePrinter")]
    static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    [StructLayout(LayoutKind.Sequential)]
    public struct DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
    }

    public static byte[] GetImageCommandFromBitmap(byte[] bmpBytes)
    {
        using var ms = new MemoryStream(bmpBytes);
        using var bmp = new Bitmap(ms);

        int width = bmp.Width;
        int height = bmp.Height;
        int widthBytes = (width + 7) / 8;

        var command = new List<byte>();

        // ESC * m nL nH bitmap
        for (int y = 0; y < height; y++)
        {
            command.Add(0x1B); // ESC
            command.Add(0x2A); // *
            command.Add(0x21); // modo 32-dot
            command.Add((byte)(widthBytes % 256)); // nL
            command.Add((byte)(widthBytes / 256)); // nH

            for (int x = 0; x < widthBytes * 8; x += 8)
            {
                byte b = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int pixelX = x + bit;
                    if (pixelX >= width) continue;

                    var pixel = bmp.GetPixel(pixelX, y);
                    int luminance = (pixel.R + pixel.G + pixel.B) / 3;
                    if (luminance < 128) b |= (byte)(1 << (7 - bit));
                }
                command.Add(b);
            }

            command.Add(0x0A); // salto de línea
        }

        return command.ToArray();
    }

    public static bool SendBytesToPrinter(string printerName, byte[] bytes)
    {
        IntPtr pPrinter = IntPtr.Zero;
        DOCINFOA di = new DOCINFOA()
        {
            pDocName = "PrintAgent Job",
            pDataType = "RAW"
        };

        try
        {
            // Normalizar nombre por compatibilidad
            printerName = printerName?.Trim().Normalize();

            if (!OpenPrinter(printerName, out pPrinter, IntPtr.Zero))
            {
                Console.WriteLine($"❌ No se pudo abrir la impresora: '{printerName}'");
                return false;
            }

            if (!StartDocPrinter(pPrinter, 1, ref di))
            {
                Console.WriteLine("❌ Falló StartDocPrinter");
                return false;
            }

            if (!StartPagePrinter(pPrinter))
            {
                Console.WriteLine("❌ Falló StartPagePrinter");
                return false;
            }

            IntPtr pUnmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, pUnmanagedBytes, bytes.Length);

            bool success = WritePrinter(pPrinter, pUnmanagedBytes, bytes.Length, out _);

            Marshal.FreeCoTaskMem(pUnmanagedBytes);

            EndPagePrinter(pPrinter);
            EndDocPrinter(pPrinter);
            ClosePrinter(pPrinter);

            if (!success)
                Console.WriteLine("❌ Falló WritePrinter");

            return success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Excepción al imprimir: {ex.Message}");
            return false;
        }
    }

}

}