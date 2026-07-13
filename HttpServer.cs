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
using System.Threading;

namespace PrintAgent
{
    public class HttpServer
    {
        private HttpListener _listener;
        private readonly PrinterManager _printerManager;
        private readonly BlockingCollection<PrintJob> _queue;
        private readonly Logger _logger;
        private string _prefix;
        private int _activeRequests;

        public string Prefix => _prefix;
        public bool IsRunning => _listener?.IsListening ?? false;

        public HttpServer(PrinterManager manager, BlockingCollection<PrintJob> queue, Logger logger, string prefix = "http://localhost:5000/")
        {
            _printerManager = manager;
            _queue = queue;
            _logger = logger;
            _prefix = prefix;
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
        }

        public async Task StartAsync()
        {
            _listener.Start();
            _logger.Info($"HTTP server iniciado en {_prefix}");
            await ListenLoop();
        }

        private async Task ListenLoop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    break; // listener fue detenido (Stop/RestartAsync)
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        public void Stop()
        {
            try
            {
                _listener.Stop(); // deja de aceptar conexiones nuevas
                WaitForDrain();
                _listener.Close();
                _logger.Info("HTTP server detenido");
            }
            catch (Exception ex)
            {
                _logger.Error("Error deteniendo HTTP server: " + ex.Message);
            }
        }

        private void WaitForDrain()
        {
            const int maxWaitMs = 2000;
            const int pollMs = 50;
            int waited = 0;
            while (Volatile.Read(ref _activeRequests) > 0 && waited < maxWaitMs)
            {
                Thread.Sleep(pollMs);
                waited += pollMs;
            }
        }

        /// <summary>
        /// Detiene el listener actual y arranca uno nuevo con otro prefix (host/puerto),
        /// sin reiniciar el proceso. Bindea de forma sincrónica para poder propagar
        /// al llamador un error real (puerto ocupado, IP inválida, falta urlacl, etc.)
        /// en vez de tragarlo en background. Si falla, intenta volver al prefix anterior.
        /// </summary>
        public Task RestartAsync(string newPrefix)
        {
            var previousPrefix = _prefix;
            Stop();

            var newListener = new HttpListener();
            newListener.Prefixes.Add(newPrefix);
            try
            {
                newListener.Start();
            }
            catch (Exception ex)
            {
                _logger.Error($"No se pudo escuchar en {newPrefix}: {ex.Message}");
                RollbackTo(previousPrefix);
                throw new InvalidOperationException($"No se pudo escuchar en {newPrefix}: {ex.Message}", ex);
            }

            _listener = newListener;
            _prefix = newPrefix;
            _logger.Info($"HTTP server reiniciado en {_prefix}");
            _ = Task.Run(ListenLoop);
            return Task.CompletedTask;
        }

        private void RollbackTo(string previousPrefix)
        {
            try
            {
                var rollbackListener = new HttpListener();
                rollbackListener.Prefixes.Add(previousPrefix);
                rollbackListener.Start();
                _listener = rollbackListener;
                _prefix = previousPrefix;
                _ = Task.Run(ListenLoop);
                _logger.Info($"Rollback: volvió a escuchar en {previousPrefix}");
            }
            catch (Exception rollbackEx)
            {
                _logger.Error("Rollback también falló, server queda detenido: " + rollbackEx.Message);
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
            var printer = _printerManager.GetPreferredPrinter();
            var bytes = Encoding.GetEncoding("UTF-8").GetBytes(text);
            RawPrinterHelper.SendBytesToPrinter(printer, bytes);
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            Interlocked.Increment(ref _activeRequests);
            try
            {
                await HandleCore(ctx);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        private async Task HandleCore(HttpListenerContext ctx)
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
                        printers = _printerManager.GetInstalledPrinters(),
                        installed = _printerManager.GetInstalledPrinters()
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

                if (req.HttpMethod == "POST" && (isText || isZpl))
                {
                    using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await sr.ReadToEndAsync();

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

                if (req.Url.AbsolutePath == "/admin")
                {
                    await HandleAdmin(req, res);
                    return;
                }

                if (req.Url.AbsolutePath == "/")
                {
                    await Write(res, new { status = "ok" });
                    return;
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

        private async Task HandleAdmin(HttpListenerRequest req, HttpListenerResponse res)
        {
            if (req.HttpMethod == "GET")
            {
                bool wantsJson = req.AcceptTypes?.Contains("application/json") == true
                    || (req.QueryString["format"]?.ToLowerInvariant() == "json");

                string rawHost = _prefix.Replace("http://", "").Replace("https://", "").Split(':')[0];
                string portStr = _prefix.TrimEnd('/').Split(':').LastOrDefault() ?? "5000";
                string displayHost = rawHost == "+" ? "0.0.0.0" : rawHost;

                if (wantsJson)
                {
                    await Write(res, new
                    {
                        host = displayHost,
                        port = int.TryParse(portStr, out var p) ? p : 5000,
                        prefix = _prefix
                    });
                    return;
                }

                var msg = req.QueryString["saved"] == "1" ? "<div class=\"msg ok\">✔ Config saved. Server restarted.</div>" : "";

                var html = $@"<!DOCTYPE html>
<html lang=""es"">
<head>
  <meta charset=""UTF-8"">
  <title>PrintAgent Admin</title>
  <style>
    *{{box-sizing:border-box}}
    body{{font-family:-apple-system,sans-serif;margin:2em;background:#f5f5f5}}
    .card{{background:#fff;max-width:420px;margin:0 auto;padding:24px;border-radius:8px;box-shadow:0 1px 4px rgba(0,0,0,.1)}}
    h2{{margin:0 0 8px}}
    .current{{font-size:.85em;color:#555;margin-bottom:20px}}
    label{{display:block;margin:12px 0 4px;font-weight:600;font-size:.9em}}
    input{{width:100%;padding:8px;border:1px solid #ccc;border-radius:4px;font-size:1em}}
    input:focus{{border-color:#06c;outline:none}}
    button{{width:100%;padding:10px;background:#06c;color:#fff;border:none;border-radius:4px;font-size:1em;cursor:pointer;margin-top:16px}}
    button:hover{{background:#0052a3}}
    .msg{{padding:10px;border-radius:4px;margin-bottom:16px}}
    .msg.ok{{background:#d4edda;color:#155724}}
    .msg.err{{background:#f8d7da;color:#721c24}}
  </style>
</head>
<body>
<div class=""card"">
  <h2>PrintAgent</h2>
  <div class=""current""><strong>{_prefix}</strong></div>
  {msg}
  <form method=""POST"">
    <label>Host</label>
    <input name=""host"" value=""{displayHost}"" placeholder=""0.0.0.0"">
    <label>Port</label>
    <input type=""number"" name=""port"" value=""{portStr}"" min=""1"" max=""65535"">
    <button>Save &amp; Restart</button>
  </form>
</div>
</body>
</html>";
                await WriteHtml(res, html);
                return;
            }

            if (req.HttpMethod == "POST")
            {
                using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await sr.ReadToEndAsync();

                string host;
                int port;

                if (req.ContentType?.StartsWith("application/json") == true)
                {
                    var json = JsonDocument.Parse(body);
                    host = json.RootElement.GetProperty("host").GetString() ?? "localhost";
                    port = json.RootElement.GetProperty("port").GetInt32();
                }
                else
                {
                    var form = ParseFormData(body);
                    host = form.GetValueOrDefault("host", "localhost");
                    int.TryParse(form.GetValueOrDefault("port"), out port);
                    if (port <= 0 || port > 65535) port = 5000;
                }

                bool wantsJson = req.AcceptTypes?.Contains("application/json") == true
                    || (req.QueryString["format"]?.ToLowerInvariant() == "json");

                try
                {
                    var newConfig = ServerConfig.Save(host, port, false, _logger);

                    if (wantsJson)
                    {
                        await Write(res, new { status = "ok", message = "Config saved. Restarting...", prefix = newConfig.Prefix });
                    }
                    else
                    {
                        res.StatusCode = 302;
                        res.Headers.Add("Location", "/admin?saved=1");
                    }

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(200);
                        try { await RestartAsync(newConfig.Prefix!); }
                        catch (Exception ex) { _logger.Error("Admin restart failed: " + ex.Message); }
                    });
                }
                catch (Exception ex)
                {
                    if (wantsJson)
                    {
                        res.StatusCode = 500;
                        await Write(res, new { error = ex.Message });
                    }
                    else
                    {
                        var errHtml = $"<!DOCTYPE html><html lang=\"es\"><head><meta charset=\"UTF-8\"><title>PrintAgent</title></head><body style=\"font-family:sans-serif;margin:2em\"><div class=\"msg err\" style=\"background:#f8d7da;color:#721c24;padding:12px;border-radius:4px\"><strong>Error:</strong> {ex.Message}</div><a href=\"/admin\">← Back</a></body></html>";
                        await WriteHtml(res, errHtml);
                    }
                }
                return;
            }

            res.StatusCode = 405;
            await Write(res, new { error = "method-not-allowed" });
        }

        private static Dictionary<string, string> ParseFormData(string body)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(body)) return result;
            foreach (var part in body.Split('&'))
            {
                var idx = part.IndexOf('=');
                if (idx <= 0) continue;
                var k = Uri.UnescapeDataString(part[..idx]);
                var v = idx < part.Length - 1 ? Uri.UnescapeDataString(part[(idx + 1)..]) : "";
                result[k] = v;
            }
            return result;
        }

        private static async Task WriteHtml(HttpListenerResponse res, string html)
        {
            var buf = Encoding.UTF8.GetBytes(html);
            res.ContentType = "text/html; charset=utf-8";
            res.ContentEncoding = Encoding.UTF8;
            res.ContentLength64 = buf.Length;
            await res.OutputStream.WriteAsync(buf);
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
