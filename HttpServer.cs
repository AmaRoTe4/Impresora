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

        private async Task HandlePrintTicket(string body, HttpListenerResponse res)
        {
            JsonDocument json;
            try { json = JsonDocument.Parse(body); }
            catch
            {
                res.StatusCode = 400;
                await Write(res, new { error = "JSON inválido" });
                return;
            }

            const int COLS = 48;
            string Line(char c = '-') => new string(c, COLS);

            string Center(string text)
            {
                if (text.Length >= COLS) return text[..COLS];
                int pad = (COLS - text.Length) / 2;
                return new string(' ', pad) + text;
            }

            string Right(string label, string value)
            {
                var txt = $"{label} {value}";
                return txt.Length >= COLS
                    ? txt[..COLS]
                    : new string(' ', COLS - txt.Length) + txt;
            }

            var bytes = new List<byte>();

            // ===== LOGO (opcional) =====
            if (json.RootElement.TryGetProperty("logo_base64", out var logoEl))
            {
                try
                {
                    var logoBytes = Convert.FromBase64String(logoEl.GetString() ?? "");
                    using var ms = new MemoryStream(logoBytes);
                    using var img = Image.FromStream(ms);
                    using var bmp = new MemoryStream();
                    img.Save(bmp, System.Drawing.Imaging.ImageFormat.Bmp);
                    bytes.AddRange(RawPrinterHelper.GetImageCommandFromBitmap(bmp.ToArray()));
                    bytes.AddRange(Encoding.UTF8.GetBytes("\n"));
                }
                catch { /* logo opcional, si falla se ignora */ }
            }

            // ===== HEADER =====
            if (json.RootElement.TryGetProperty("header_lines", out var headers) &&
                headers.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in headers.EnumerateArray())
                    bytes.AddRange(Encoding.UTF8.GetBytes(Center(h.GetString() ?? "") + "\n"));

                bytes.AddRange(Encoding.UTF8.GetBytes(Line('=') + "\n"));
            }

            // ===== DATOS GENERALES =====
            string fecha = json.RootElement.GetProperty("date").GetString() ?? "";
            string numero = json.RootElement.GetProperty("ticket_number").GetString() ?? "";
            string cliente = json.RootElement.GetProperty("client").GetString() ?? "";

            bytes.AddRange(Encoding.UTF8.GetBytes($"FECHA: {fecha}\n"));
            bytes.AddRange(Encoding.UTF8.GetBytes($"TICKET NUM: {numero}\n"));
            bytes.AddRange(Encoding.UTF8.GetBytes($"CLIENTE: {cliente}\n"));
            bytes.AddRange(Encoding.UTF8.GetBytes(Line() + "\n"));

            // ===== ITEMS =====
            foreach (var it in json.RootElement.GetProperty("items").EnumerateArray())
            {
                string desc = it.GetProperty("description").GetString() ?? "";
                int qty = it.GetProperty("quantity").GetInt32();
                decimal unit = it.GetProperty("unit_price").GetDecimal();
                decimal total = it.GetProperty("total").GetDecimal();
                decimal disc = it.TryGetProperty("discount_percent", out var d) ? d.GetDecimal() : 0;

                if (desc.Length > COLS)
                    desc = desc[..(COLS - 3)] + "...";

                bytes.AddRange(Encoding.UTF8.GetBytes(desc + "\n"));

                string left = $"{qty}x ${unit:F2}";
                if (disc > 0) left += $" -{disc}%";
                string right = $"${total:F2}";
                bytes.AddRange(Encoding.UTF8.GetBytes(left.PadRight(COLS - right.Length) + right + "\n"));
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(Line() + "\n"));

            // ===== TOTALES =====
            if (json.RootElement.TryGetProperty("subtotal", out var sub))
                bytes.AddRange(Encoding.UTF8.GetBytes(Right("SUBTOTAL:", $"${sub.GetDecimal():F2}") + "\n"));

            if (json.RootElement.TryGetProperty("discount_rate", out var dr) &&
                json.RootElement.TryGetProperty("discount_amount", out var da))
                bytes.AddRange(Encoding.UTF8.GetBytes(
                    Right($"DESCUENTO {dr.GetDecimal()}%:", $"${da.GetDecimal():F2}") + "\n"));

            if (json.RootElement.TryGetProperty("total_with_discount", out var td))
                bytes.AddRange(Encoding.UTF8.GetBytes(
                    Right("TOTAL C/DESCUENTO:", $"${td.GetDecimal():F2}") + "\n"));

            bytes.AddRange(Encoding.UTF8.GetBytes(
                Right("TOTAL FINAL:", $"${json.RootElement.GetProperty("total_final").GetDecimal():F2}") + "\n"));

            bytes.AddRange(Encoding.UTF8.GetBytes(Line() + "\n\n"));

            // ===== QR =====
            if (json.RootElement.TryGetProperty("qr_base64", out var qrEl))
            {
                try
                {
                    var qrBytes = Convert.FromBase64String(qrEl.GetString() ?? "");
                    using var ms = new MemoryStream(qrBytes);
                    using var img = Image.FromStream(ms);
                    using var bmp = new MemoryStream();
                    img.Save(bmp, System.Drawing.Imaging.ImageFormat.Bmp);
                    bytes.AddRange(RawPrinterHelper.GetImageCommandFromBitmap(bmp.ToArray()));
                    bytes.AddRange(Encoding.UTF8.GetBytes("\n\n"));
                }
                catch { }
            }

            // ===== FOOTER =====
            if (json.RootElement.TryGetProperty("footer_lines", out var foot) &&
                foot.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in foot.EnumerateArray())
                    bytes.AddRange(Encoding.UTF8.GetBytes(Center(f.GetString() ?? "") + "\n"));
            }

            // ===== CORTE =====
            bytes.AddRange(new byte[] { 0x1D, 0x56, 0x41, 0x00 });

            try
            {
                var printer = _printerManager.GetPreferredPrinter();
                RawPrinterHelper.SendBytesToPrinter(printer, bytes.ToArray());
                await Write(res, new { status = "printed" });
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                await Write(res, new { error = ex.Message });
            }
        }

        private class TicketItem
        {
            public string description { get; set; } = "";
            public int quantity { get; set; }
            public decimal unit_price { get; set; }
            public decimal discount_percent { get; set; } = 0;
        }

        private class TicketPayload
        {
            public string? logo_base64 { get; set; }          // opcional (PNG/JPG base64)
            public string? qr_base64 { get; set; }            // opcional (PNG/JPG base64)
            public List<string>? header_lines { get; set; }
            public string date { get; set; } = "";
            public string ticket_number { get; set; } = "";
            public string client { get; set; } = "";
            public List<TicketItem> items { get; set; } = new();
            public decimal? discount_rate { get; set; }       // opcional
            public List<string>? footer_lines { get; set; }
        }

        private async Task HandlePrintTicketGdi(string body, HttpListenerResponse res)
        {
            TicketPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<TicketPayload>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                res.StatusCode = 400;
                await Write(res, new { error = "JSON inválido" });
                return;
            }

            if (payload == null || payload.items == null)
            {
                res.StatusCode = 400;
                await Write(res, new { error = "Payload vacío" });
                return;
            }

            // === decode images (opcionales) ===
            Image? logoImg = null;
            Image? qrImg = null;

            try
            {
                if (!string.IsNullOrWhiteSpace(payload.logo_base64))
                {
                    var b = Convert.FromBase64String(payload.logo_base64);
                    logoImg = Image.FromStream(new MemoryStream(b));
                }
            }
            catch { logoImg = null; }

            try
            {
                if (!string.IsNullOrWhiteSpace(payload.qr_base64))
                {
                    var b = Convert.FromBase64String(payload.qr_base64);
                    qrImg = Image.FromStream(new MemoryStream(b));
                }
            }
            catch { qrImg = null; }

            // === calcular totales en server (para que sea general) ===
            decimal subtotal = 0m;
            var computed = new List<(string desc, int qty, decimal unit, decimal disc, decimal total)>();

            foreach (var it in payload.items)
            {
                var lineTotal = it.quantity * it.unit_price;
                if (it.discount_percent > 0)
                    lineTotal -= lineTotal * (it.discount_percent / 100m);

                computed.Add((it.description ?? "", it.quantity, it.unit_price, it.discount_percent, lineTotal));
                subtotal += lineTotal;
            }

            decimal discountRate = payload.discount_rate ?? 0m;
            decimal discountAmount = discountRate > 0 ? subtotal * (discountRate / 100m) : 0m;
            decimal totalFinal = subtotal - discountAmount;

            string printer = _printerManager.GetPreferredPrinter();
            if (string.IsNullOrWhiteSpace(printer))
            {
                res.StatusCode = 500;
                await Write(res, new { error = "No hay impresora preferida configurada" });
                return;
            }

            try
            {
                var doc = new PrintDocument();
                doc.PrinterSettings.PrinterName = printer;

                // Papel 80mm (aprox). Muchos drivers ignoran esto, pero ayuda.
                // 80mm = 3.15in. Width en hundredths of an inch: 315
                // Height dinámica (larga): 1200 = 12in aprox, el driver extiende si hace falta.
                doc.DefaultPageSettings.PaperSize = new PaperSize("Ticket80", 315, 1200);
                doc.DefaultPageSettings.Margins = new Margins(5, 5, 5, 5);

                // Fuente mono estilo POS (si no existe, cae a la default)
                var font = new Font("Consolas", 9f, FontStyle.Regular);
                var fontBold = new Font("Consolas", 10f, FontStyle.Bold);
                var fontBig = new Font("Consolas", 12f, FontStyle.Bold);

                doc.PrintPage += (s, e) =>
                {
                    float x = e.MarginBounds.Left;
                    float y = e.MarginBounds.Top;
                    float w = e.MarginBounds.Width;

                    // helpers
                    float LineH(Font f) => f.GetHeight(e.Graphics) + 2;
                    void DrawLine(string text, Font f, StringAlignment align = StringAlignment.Near)
                    {
                        using var sf = new StringFormat { Alignment = align };
                        e.Graphics.DrawString(text, f, Brushes.Black, new RectangleF(x, y, w, 1000), sf);
                        y += LineH(f);
                    }
                    void DrawHRule(char c)
                    {
                        DrawLine(new string(c, 48), font, StringAlignment.Near);
                    }

                    // LOGO (opcional)
                    if (logoImg != null)
                    {
                        // Escala a ancho máximo
                        float maxW = w;
                        float ratio = (float)logoImg.Width / (float)logoImg.Height;
                        float drawW = Math.Min(maxW, logoImg.Width);
                        float drawH = drawW / ratio;

                        // Centrar
                        float lx = x + (w - drawW) / 2f;
                        e.Graphics.DrawImage(logoImg, lx, y, drawW, drawH);
                        y += drawH + 6;
                    }

                    // HEADER
                    if (payload.header_lines != null)
                    {
                        foreach (var hl in payload.header_lines)
                            DrawLine(hl ?? "", fontBold, StringAlignment.Center);

                        DrawHRule('=');
                    }

                    // Datos
                    DrawLine($"FECHA: {payload.date}", font);
                    DrawLine($"TICKET NUM: {payload.ticket_number}", font);
                    DrawLine($"CLIENTE: {payload.client}", font);
                    DrawHRule('-');

                    // Items (dos líneas por item)
                    foreach (var it in computed)
                    {
                        // descripción (truncada visualmente, GDI no necesita 48 cols exactos)
                        DrawLine(it.desc, font);

                        string left = $"{it.qty}x ${it.unit:F2}";
                        if (it.disc > 0) left += $" -{it.disc}%";
                        string right = $"${it.total:F2}";

                        // Columna derecha alineada
                        // Dibujamos left a la izquierda y right a la derecha en la misma línea
                        e.Graphics.DrawString(left, font, Brushes.Black, new RectangleF(x, y, w, 1000), new StringFormat { Alignment = StringAlignment.Near });
                        e.Graphics.DrawString(right, font, Brushes.Black, new RectangleF(x, y, w, 1000), new StringFormat { Alignment = StringAlignment.Far });
                        y += LineH(font);
                    }

                    DrawHRule('-');

                    // Totales (alineados a la derecha)
                    void DrawTotal(string label, decimal val)
                    {
                        e.Graphics.DrawString($"{label} ${val:F2}", font, Brushes.Black, new RectangleF(x, y, w, 1000), new StringFormat { Alignment = StringAlignment.Far });
                        y += LineH(font);
                    }

                    DrawTotal("SUBTOTAL:", subtotal);

                    if (discountRate > 0)
                    {
                        DrawTotal($"DESCUENTO {discountRate:F0}%:", discountAmount);
                        DrawTotal("TOTAL C/DESCUENTO:", totalFinal);
                    }

                    // Total final grande
                    e.Graphics.DrawString($"TOTAL FINAL: ${totalFinal:F2}", fontBig, Brushes.Black, new RectangleF(x, y, w, 1000), new StringFormat { Alignment = StringAlignment.Far });
                    y += LineH(fontBig);

                    DrawHRule('-');
                    y += 6;

                    // QR (opcional)
                    if (qrImg != null)
                    {
                        float qrSize = Math.Min(w * 0.6f, 220); // tamaño razonable
                        float qx = x + (w - qrSize) / 2f;
                        e.Graphics.DrawImage(qrImg, qx, y, qrSize, qrSize);
                        y += qrSize + 6;
                    }

                    // Footer (opcional)
                    if (payload.footer_lines != null)
                    {
                        foreach (var fl in payload.footer_lines)
                            DrawLine(fl ?? "", fontBold, StringAlignment.Center);
                    }

                    // Avance extra
                    y += 20;

                    // no paginación por ahora
                    e.HasMorePages = false;
                };

                doc.EndPrint += (s, e) =>
                {
                    // Corte por ESC/POS al final
                    try { SendCutCommand(); } catch { /* ignorar */ }
                };

                doc.Print();

                await Write(res, new { status = "printed_gdi" });
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                await Write(res, new { error = ex.Message });
            }
            finally
            {
                logoImg?.Dispose();
                qrImg?.Dispose();
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
                bool isTicket = req.Url.AbsolutePath == "/print/ticket";
                bool isTicketGdi = req.Url.AbsolutePath == "/print_ticket_gdi";

                if (req.HttpMethod == "POST" && (isText || isZpl || isZplRaw || isQR || isTicket || isTicketGdi))
                {
                    using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await sr.ReadToEndAsync();

                    if (isTicketGdi)
                    {
                        await HandlePrintTicketGdi(body, res);
                        return;
                    }

                    if (isQR)
                    {
                        await HandlePrintWithQR(body, res);
                        return;
                    }

                    if (isTicket)
                    {
                        await HandlePrintTicket(body, res);
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
