
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

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
            _logger.Info("Servidor HTTP iniciado.");
            while (true)
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(ctx));
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
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
                    await WriteJson(res, new
                    {
                        defaultPrinter = _printerManager.GetDefaultPrinter(),
                        preferredPrinter = _printerManager.GetPreferredPrinter(),
                        printers = _printerManager.GetInstalledPrinters()
                    });
                    return;
                }

                bool isText = req.Url.AbsolutePath is "/print" or "/print_text";
                bool isZpl  = req.Url.AbsolutePath == "/print_zpl";

                if (req.HttpMethod == "POST" && (isText || isZpl))
                {
                    using var reader = new System.IO.StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await reader.ReadToEndAsync();
                    var json = JsonDocument.Parse(body);

                    string field = isZpl ? "zpl" : "content";
                    if (!json.RootElement.TryGetProperty(field, out var contentEl))
                    {
                        res.StatusCode = 400;
                        await WriteJson(res, new { error = $"Falta campo '{field}'" });
                        return;
                    }

                    string payload = contentEl.GetString() ?? "";
                    _queue.Add(new PrintJob(isZpl ? JobKind.Zpl : JobKind.Text, payload));
                    await WriteJson(res, new { status = "queued", kind = isZpl ? "zpl" : "text", length = payload.Length });
                    return;
                }

                if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/config")
                {
                    using var reader = new System.IO.StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await reader.ReadToEndAsync();
                    var json = JsonDocument.Parse(body);
                    if (json.RootElement.TryGetProperty("printer", out var printerEl))
                    {
                        _printerManager.SetPreferredPrinter(printerEl.GetString()!);
                    }
                    await WriteJson(res, new { status = "ok", preferredPrinter = _printerManager.GetPreferredPrinter() });
                    return;
                }

                res.StatusCode = 404;
                await WriteJson(res, new { error = "not-found" });
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                await WriteJson(res, new { error = ex.Message });
                _logger.Error("Excepción: " + ex);
            }
            finally { res.Close(); }
        }

        private static async Task WriteJson(HttpListenerResponse res, object obj)
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
