using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Drawing.Printing;
using System.Drawing;
using System.IO;

namespace PrintAgent
{
    public class HttpServer
    {
        private readonly HttpListener _listener;
        private readonly PrinterManager _printerManager;
        private readonly BlockingCollection<PrintJob> _queue;
        private readonly Logger _logger;

        public HttpServer(PrinterManager manager, BlockingCollection<PrintJob> queue, Logger logger, string prefix = "http://localhost:5000/")
        {
            _printerManager = manager;
            _queue = queue;
            _logger = logger;
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
        }

        public async Task StartAsync()
        {
            _listener.Start();
            _logger.Info("HTTP server iniciado");
            while (true)
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private void SendCutCommand()
        {
            byte[] cut = new byte[] { 0x1D, 0x56, 0x41, 0x00 };
            string printer = _printerManager.GetPreferredPrinter();
            RawPrinterHelper.SendBytesToPrinter(printer, cut);
        }

        private async Task HandlePrintWithQR(string body, HttpListenerResponse res)
        {
            JsonDocument json;
            try { json = JsonDocument.Parse(body); }
            catch
            {
                res.StatusCode = 400;
                await Write(res, new { error = "El cuerpo no es JSON válido." });
                return;
            }

            if (!json.RootElement.TryGetProperty("text_1", out var text1El) ||
                !json.RootElement.TryGetProperty("text_2", out var text2El) ||
                !json.RootElement.TryGetProperty("qr_base64", out var qrEl))
            {
                res.StatusCode = 400;
                await Write(res, new { error = "Faltan campos: text_1, text_2 o qr_base64" });
                return;
            }

            string text1 = text1El.GetString() ?? "";
            string text2 = text2El.GetString() ?? "";
            string qrBase64 = qrEl.GetString() ?? "";

            byte[] qrBytes;
            try { qrBytes = Convert.FromBase64String(qrBase64); }
            catch
            {
                res.StatusCode = 400;
                await Write(res, new { error = "QR inválido: base64 no válido" });
                return;
            }

            try
            {
                using var qrStream = new MemoryStream(qrBytes);
                using var qrImage = Image.FromStream(qrStream);

                // Convertimos la imagen QR a formato ESC/POS compatible (monocromo BMP)
                using var ms = new MemoryStream();
                qrImage.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                var bmpBytes = ms.ToArray();

                // Armamos todo el bloque a enviar al printer
                var finalBuilder = new List<byte>();
                finalBuilder.AddRange(Encoding.UTF8.GetBytes(text1 + "\n\n"));

                finalBuilder.AddRange(RawPrinterHelper.GetImageCommandFromBitmap(bmpBytes));

                finalBuilder.AddRange(Encoding.UTF8.GetBytes("\n\n" + text2 + "\n\n"));
                finalBuilder.AddRange(new byte[] { 0x1D, 0x56, 0x41, 0x00 }); // corte

                var printer = new PrintDocument().PrinterSettings.PrinterName;
                RawPrinterHelper.SendBytesToPrinter(printer, finalBuilder.ToArray());

                await Write(res, new { status = "printed" });
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                await Write(res, new { error = "Error al imprimir: " + ex.Message });
            }
        }


        private void SendRawText(string text)
        {
            var printer = new PrintDocument().PrinterSettings.PrinterName;
            var bytes = Encoding.GetEncoding("UTF-8").GetBytes(text);
            RawPrinterHelper.SendBytesToPrinter(printer, bytes);
        }


        private async Task Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.AddHeader("Access-Control-Allow-Headers", "Content-Type");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            try
            {
                if (req.Url.AbsolutePath == "/printers" && req.HttpMethod == "GET")
                {
                    await Write(res, new
                    {
                        defaultPrinter = _printerManager.GetDefaultPrinter(),
                        preferredPrinter = _printerManager.GetPreferredPrinter(),
                        printers = _printerManager.GetInstalledPrinters()
                    });
                    return;
                }

                if (req.Url.AbsolutePath == "/config" && req.HttpMethod == "POST")
                {
                    using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await sr.ReadToEndAsync();
                    var json = JsonDocument.Parse(body);
                    if (json.RootElement.TryGetProperty("printer", out var pr))
                        _printerManager.SetPreferredPrinter(pr.GetString() ?? "");
                    await Write(res, new { status = "ok" });
                    return;
                }

                bool isText = req.Url.AbsolutePath is "/print" or "/print_text";
                bool isZpl = req.Url.AbsolutePath == "/print_zpl";
                bool isZplRaw = req.Url.AbsolutePath == "/print_zpl_raw";
                bool isQR = req.Url.AbsolutePath == "/print_qr";

                if (req.HttpMethod == "POST" && (isText || isZpl || isZplRaw || isQR))
                {
                    using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await sr.ReadToEndAsync();

                    if (isQR)
                    {
                        await HandlePrintWithQR(body, res);
                        return;
                    }

                                        if (isZplRaw)
                    {
                        // Accepts either:
                        // - Content-Type: application/json  { "zpl": "^XA...^XZ" }
                        // - Content-Type: text/plain       ^XA...^XZ
                        string zplPayload = body;

                        var ct = (req.ContentType ?? "").ToLowerInvariant();
                        if (ct.Contains("application/json"))
                        {
                            try
                            {
                                var j = JsonDocument.Parse(body);
                                if (!j.RootElement.TryGetProperty("zpl", out var zplEl))
                                {
                                    res.StatusCode = 400;
                                    await Write(res, new { error = "Falta campo 'zpl'" });
                                    return;
                                }
                                zplPayload = zplEl.GetString() ?? "";
                            }
                            catch
                            {
                                res.StatusCode = 400;
                                await Write(res, new { error = "El cuerpo no es JSON válido." });
                                return;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(zplPayload))
                        {
                            res.StatusCode = 400;
                            await Write(res, new { error = "ZPL vacío" });
                            return;
                        }

                        _queue.Add(new PrintJob(JobKind.Zpl, zplPayload));
                        await Write(res, new { status = "queued", bytes = zplPayload.Length });
                        return;
                    }

                    var json = JsonDocument.Parse(body);

                    if (isZpl)
                    {
                        if (!json.RootElement.TryGetProperty("valores", out var arr) ||
                            arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                        {
                            res.StatusCode = 400;
                            await Write(res, new { error = "Falta arreglo 'valores' con codigo_barra" });
                            return;
                        }

                        // ===== Layout fijo 203 dpi =====
                        const int PW = 240;  // 30 mm
                        const int LL = 560;  // 70 mm

                        // Bandera IZQUIERDA inferior (15x25 mm) → ROI (x=8,y=352) tamaño 120x200
                        const int ROI_X = 8, ROI_Y = 352, ROI_W = 120, ROI_H = 200;

                        // EAN-8 “grande y centrado” (acordado)
                        // módulo=2 (legible), altura=34; centrado visual: X=66, Y=376
                        const int MOD = 2, RATIO = 2, H = 34;
                        const int X_BAR = 66, Y_BAR = 376;         // centro dentro del ROI
                        const int X_TXT = 46, Y_TXT = 376;         // texto a la izquierda del código

                        bool drawRoi = false; // poner true para ver el rectángulo de la bandera

                        string ToEan8(string raw)
                        {
                            // 1) dejar sólo dígitos
                            var digitsOnly = new string((raw ?? "").Where(char.IsDigit).ToArray());

                            // 2) tomamos los últimos 6; si hay menos, completamos a la izquierda
                            string last6 = digitsOnly.Length >= 6
                                ? digitsOnly.Substring(digitsOnly.Length - 6, 6)
                                : digitsOnly.PadLeft(6, '0');

                            // 3) EAN-8 requiere 7 datos + 1 check → anteponemos '0' para formar 7
                            string data7 = "0" + last6;

                            // 4) dígito verificador EAN-8
                            int sum = 0;
                            for (int i = 0; i < 7; i++)
                            {
                                int d = data7[i] - '0';
                                // posiciones 1,3,5,7 (i=0,2,4,6) pesan x3
                                sum += ((i % 2) == 0) ? d * 3 : d;
                            }
                            int check = (10 - (sum % 10)) % 10;

                            return data7 + check.ToString();
                        }

                        var sb = new StringBuilder();
                        int agregadas = 0;

                        foreach (var item in arr.EnumerateArray())
                        {
                            string input = item.TryGetProperty("codigo_barra", out var el) ? (el.GetString() ?? "") : "";
                            string ean8 = ToEan8(input);

                            sb.Append("^XA")
                            .Append("^CI28")
                            .Append("^PW").Append(PW)
                            .Append("^LL").Append(LL)
                            .Append("^LH0,0");

                            if (drawRoi)
                            {
                                sb.Append("^FO").Append(ROI_X).Append(",").Append(ROI_Y)
                                .Append("^GB").Append(ROI_W).Append(",").Append(ROI_H).Append(",2^FS");
                            }

                            // EAN-8 vertical, centrado y grande
                            sb.Append("^BY").Append(MOD).Append(",").Append(RATIO).Append(",").Append(H)
                            .Append("^FO").Append(X_BAR).Append(",").Append(Y_BAR)
                            .Append("^B8B,").Append(H).Append(",N")
                            .Append("^FD").Append(ean8).Append("^FS")

                            // Texto rotado (un poco más grande)
                            .Append("^FO").Append(X_TXT).Append(",").Append(Y_TXT)
                            .Append("^A0B,22,18")
                            .Append("^FD").Append(ean8).Append("^FS")

                            .Append("^XZ");

                            agregadas++;
                        }

                        if (agregadas == 0)
                        {
                            res.StatusCode = 400;
                            await Write(res, new { error = "Ningún item con 'codigo_barra' válido" });
                            return;
                        }

                        _queue.Add(new PrintJob(JobKind.Zpl, sb.ToString()));
                        await Write(res, new { status = "queued", count = agregadas });
                        return;
                    }



                    if (isText)
                    {
                        if (!json.RootElement.TryGetProperty("text", out var dataEl))
                        {
                            res.StatusCode = 400;
                            await Write(res, new { error = "Falta campo 'text'" });
                            return;
                        }

                        string text = dataEl.GetString() ?? "";

                        try
                        {
                            SendRawText(text + "\n\n" + "\x1D\x56\x41\x00"); // texto crudo + corte
                            await Write(res, new { status = "printed_with_cut" });
                        }
                        catch (Exception ex)
                        {
                            res.StatusCode = 500;
                            await Write(res, new { error = "Error al imprimir: " + ex.Message });
                        }
                        return;
                    }

                }

                res.StatusCode = 404;
                await Write(res, new { error = "not-found" });
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                await Write(res, new { error = ex.Message });
                _logger.Error("Excepción: " + ex);
            }
            finally { res.Close(); }
        }

        private static async Task Write(HttpListenerResponse res, object obj)
        {
            var json = JsonSerializer.Serialize(obj);
            var buf = Encoding.UTF8.GetBytes(json);
            res.ContentType = "application/json";
            res.ContentEncoding = Encoding.UTF8;
            res.ContentLength64 = buf.Length;
            await res.OutputStream.WriteAsync(buf);
        }
    }
}
