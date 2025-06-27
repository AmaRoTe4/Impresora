
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Drawing.Printing;

namespace PrintAgent
{
    public class HttpServer
    {
        private readonly HttpListener _listener;
        private readonly PrinterManager _printerManager;
        private readonly BlockingCollection<PrintJob> _queue;
        private readonly Logger _logger;

        public HttpServer(PrinterManager manager, BlockingCollection<PrintJob> queue, Logger logger, string prefix="http://localhost:5000/")
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
            // Comando de corte: GS V A 0
            byte[] cut = new byte[] { 0x1D, 0x56, 0x41, 0x00 };
            string printer = new PrintDocument().PrinterSettings.PrinterName;

            RawPrinterHelper.SendBytesToPrinter(printer, cut);
        }


        private async Task HandlePrintWithQR(string body, HttpListenerResponse res)
        {
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(body);
            }
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

            // Decodificar QR base64
            byte[] qrBytes;
            try
            {
                qrBytes = Convert.FromBase64String(qrBase64);
            }
            catch
            {
                res.StatusCode = 400;
                await Write(res, new { error = "QR inválido: base64 no válido" });
                return;
            }

            using var qrStream = new MemoryStream(qrBytes);
            using var qrImage = Image.FromStream(qrStream);

            // Imprimir
            var pd = new PrintDocument();
            pd.PrintPage += (sender, e) =>
            {
                var g = e.Graphics;
                var font = new Font("Arial", 10);
                float marginLeft = 0;
                float y = 0;

                // Medir y dibujar texto1
                var text1Size = g.MeasureString(text1, font);
                g.DrawString(text1, font, Brushes.Black, marginLeft, y);
                y += text1Size.Height + 10;

                // Dibujar imagen QR
                int qrWidth = 150;
                int qrX = (int)((e.PageBounds.Width - qrWidth) / 2);
                g.DrawImage(qrImage, new Rectangle(qrX, (int)y, qrWidth, qrWidth));
                y += qrWidth + 10;

                // Medir y dibujar texto2
                g.DrawString(text2, font, Brushes.Black, marginLeft, y);
            };


            try
            {
                pd.Print();
                SendCutCommand();
                await Write(res, new { status = "printed" });
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                await Write(res, new { error = "Error al imprimir: " + ex.Message });
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            // CORS
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
                    using var sr = new System.IO.StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await sr.ReadToEndAsync();
                    var json = JsonDocument.Parse(body);
                    if (json.RootElement.TryGetProperty("printer", out var pr))
                        _printerManager.SetPreferredPrinter(pr.GetString() ?? "");
                    await Write(res, new { status = "ok" });
                    return;
                }

                bool isText = req.Url.AbsolutePath is "/print" or "/print_text";
                bool isZpl  = req.Url.AbsolutePath == "/print_zpl";
                bool isQR  = req.Url.AbsolutePath == "/print_qr";


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
                        foreach (var item in arr.EnumerateArray())
                        {
                            var nombre = item.GetProperty("nombre").GetString() ?? "";
                            var codigo = item.GetProperty("codigo_barra").GetString() ?? "";

                            sb.Append("^XA")
                            .Append("^PW400^LH0,0")
                            .Append("^BY2,2,50^FO30,20^BCN,50,Y,N,N^FD").Append(codigo).Append("^FS")
                            .Append("^FO30,100^A0,20,20^FD").Append(nombre).Append("^FS")
                            .Append("^XZ");
                        }

                        _queue.Add(new PrintJob(JobKind.Zpl, sb.ToString()));
                    }

                    if (isText)
                    {
                        if (!json.RootElement.TryGetProperty("text", out var dataEl))
                        {
                            res.StatusCode = 400;
                            await Write(res, new { error = "Falta campo 'text'" });
                            return;
                        }

                        var payload = dataEl.GetString() ?? "";
                        _queue.Add(new PrintJob(JobKind.Text, payload));
                        SendCutCommand();
                    }

                    await Write(res, new { status = "queued" });
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
