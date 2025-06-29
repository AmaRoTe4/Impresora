
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
            if (!zpl.EndsWith("\r\n")) zpl += "\r\n";
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
}
