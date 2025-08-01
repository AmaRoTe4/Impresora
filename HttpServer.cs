using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
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
            string printer = new PrintDocument().PrinterSettings.PrinterName;
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
                bool isQR = req.Url.AbsolutePath == "/print_qr";

                if (req.HttpMethod == "POST" && (isText || isZpl || isQR))
                {
                    using var sr = new StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await sr.ReadToEndAsync();

                    if (isQR)
                    {
                        await HandlePrintWithQR(body, res);
                        return;
                    }

                    var json = JsonDocument.Parse(body);

                    if (isZpl)
                    {
                        if (!json.RootElement.TryGetProperty("valores", out var arr) ||
                            arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                        {
                            res.StatusCode = 400;
                            await Write(res, new { error = "Falta arreglo 'valores' con nombre y codigo_barra" });
                            return;
                        }

                        var sb = new StringBuilder();
                        //38X20
                        foreach (var item in arr.EnumerateArray())
                        {
                            // --- datos base ---------------------------------------------------------
                            var nombre      = item.GetProperty("nombre").GetString()        ?? "";
                            var codigo      = item.GetProperty("codigo_barra").GetString()  ?? "";
                            var precio      = item.GetProperty("precio").GetString()        ?? "";
                            var usePrecioEl = item.GetProperty("use_precio").GetString()    ?? "false";

                            // --- valida si debe mostrar precio -------------------------------------
                            var mostrarPrecio = bool.TryParse(usePrecioEl, out var flag) && flag;

                            // —–– limita largo del precio para que nunca desborde (≈16 carac. máx.) –
                            if (precio.Length > 16) precio = precio[..16];

                            // --- plantilla ZPL ------------------------------------------------------
                            sb.Append("^XA")                        // inicio etiqueta
                            .Append("^PW300^LH0,0")               // 300 dots (≈37,5 mm de ancho útil)
                            .Append("^BY1,2,30")                  // ancho barras, ratio, alto 30
                            .Append("^FO20,10^BCN,30,N,N,N")      // barcode sin texto auto
                            .Append("^FD").Append(codigo).Append("^FS")
                            .Append("^FO20,45")                   // nombre bajo el código de barras
                            .Append("^FB260,3,0,L,0")             // bloque 260 px, máx. 3 líneas, alineado izq.
                            .Append("^A0N,16,16^FD").Append(nombre).Append("^FS");

                            // --- precio (opcional, centrado) ---------------------------------------
                            if (mostrarPrecio)
                            {
                                sb.Append("^FO20,70")               // posición vertical
                                .Append("^FB260,1,0,C,0")         // bloque 260 px, 1 línea, alineado CENTRO
                                .Append("^A0N,16,16^FD").Append(precio).Append("^FS");
                            }

                            sb.Append("^XZ");                       // fin etiqueta
                        }


                        _queue.Add(new PrintJob(JobKind.Zpl, sb.ToString()));
                        await Write(res, new { status = "queued" });
                        return;

                    //50X25
                    //foreach (var item in arr.EnumerateArray())
                    //{
                    //    var nombre = item.GetProperty("nombre").GetString() ?? "";
                    //    var codigo = item.GetProperty("codigo_barra").GetString() ?? "";

                    //    sb.Append("^XA")
                    //    .Append("^PW300^LH0,0") // Ancho máximo ≈ 38mm
                    //    .Append("^BY2,2,30")     // Código de barras más bajo
                    //    .Append("^FO20,10^BCN,30,Y,N,N^FD").Append(codigo).Append("^FS") // Código de barras
                    //    .Append("^FO20,50^A0N,16,16^FD").Append(nombre).Append("^FS")    // Nombre del producto
                    //    .Append("^XZ");
                    //}
                    //_queue.Add(new PrintJob(JobKind.Zpl, sb.ToString()));
                    //await Write(res, new { status = "queued" });
                    //return;
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
