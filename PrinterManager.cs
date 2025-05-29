
using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PrintAgent
{
    public class PrinterManager
    {
        private readonly Logger _logger;
        private readonly string _configPath;
        private string? _preferredPrinter;

        public PrinterManager(Logger logger)
        {
            _logger = logger;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrintAgent");
            Directory.CreateDirectory(dir);
            _configPath = Path.Combine(dir, "config.json");
            LoadConfig();
        }

        public IReadOnlyList<string> GetInstalledPrinters() =>
            PrinterSettings.InstalledPrinters.Cast<string>().ToList();

        public string GetDefaultPrinter() => new PrinterSettings().PrinterName;

        public void SetPreferredPrinter(string printerName)
        {
            if (!GetInstalledPrinters().Contains(printerName))
                throw new ArgumentException($"Printer '{printerName}' no encontrada.");
            _preferredPrinter = printerName;
            SaveConfig();
            _logger.Info($"Impresora preferida: {_preferredPrinter}");
        }

        public string GetPreferredPrinter() => _preferredPrinter ?? GetDefaultPrinter();

        public void PrintText(string text)
        {
            var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = GetPreferredPrinter();
            doc.PrintPage += (s, e) =>
            {
                e.Graphics.DrawString(text, new System.Drawing.Font("Consolas", 10),
                    System.Drawing.Brushes.Black, new System.Drawing.PointF(10, 10));
            };
            doc.Print();
        }

        public void PrintZpl(string zpl)
        {
            var bytes = Encoding.UTF8.GetBytes(zpl);
            RawPrinterHelper.SendBytesToPrinter(GetPreferredPrinter(), bytes);
        }

        private void LoadConfig()
        {
            if (!File.Exists(_configPath)) return;
            try
            {
                var json = JsonDocument.Parse(File.ReadAllText(_configPath));
                if (json.RootElement.TryGetProperty("preferredPrinter", out var el))
                    _preferredPrinter = el.GetString();
            }
            catch (Exception ex)
            {
                _logger.Error("No se pudo leer config: " + ex.Message);
            }
        }

        private void SaveConfig()
        {
            try
            {
                var json = JsonSerializer.Serialize(new { preferredPrinter = _preferredPrinter });
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                _logger.Error("No se pudo guardar config: " + ex.Message);
            }
        }
    }

    internal static class RawPrinterHelper
    {
        [DllImport("winspool.Drv", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);
        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);
        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, IntPtr pDocInfo);
        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);
        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);
        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);
        [DllImport("winspool.Drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        [StructLayout(LayoutKind.Sequential)]
        private struct DOCINFOA
        {
            [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
        }

        public static void SendBytesToPrinter(string printerName, byte[] bytes)
        {
            if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
                throw new InvalidOperationException("No se pudo abrir la impresora.");

            try
            {
                var docInfo = new DOCINFOA { pDocName = "RAW ZPL", pDataType = "RAW" };
                IntPtr pDocInfo = Marshal.AllocHGlobal(Marshal.SizeOf(docInfo));
                Marshal.StructureToPtr(docInfo, pDocInfo, false);

                if (!StartDocPrinter(hPrinter, 1, pDocInfo)) throw new InvalidOperationException("StartDocPrinter falló.");
                if (!StartPagePrinter(hPrinter)) throw new InvalidOperationException("StartPagePrinter falló.");

                IntPtr unmanagedBytes = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);

                if (!WritePrinter(hPrinter, unmanagedBytes, bytes.Length, out _))
                    throw new InvalidOperationException("WritePrinter falló.");

                EndPagePrinter(hPrinter);
                EndDocPrinter(hPrinter);

                Marshal.FreeHGlobal(unmanagedBytes);
                Marshal.FreeHGlobal(pDocInfo);
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }
    }
}
