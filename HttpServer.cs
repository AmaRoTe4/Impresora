
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
        private readonly BlockingCollection<string> _queue;
        private readonly Logger _logger;

        public HttpServer(PrinterManager manager, BlockingCollection<string> queue, Logger logger, string prefix = "http://localhost:5000/")
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
            try
            {
                if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/printers")
                {
                    var list = new
                    {
                        defaultPrinter = _printerManager.GetDefaultPrinter(),
                        preferredPrinter = _printerManager.GetPreferredPrinter(),
                        printers = _printerManager.GetInstalledPrinters()
                    };
                    await WriteJson(res, list);
                }
                else if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/config")
                {
                    using var reader = new System.IO.StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await reader.ReadToEndAsync();
                    var json = JsonDocument.Parse(body);
                    if (json.RootElement.TryGetProperty("printer", out var printerEl))
                    {
                        var printer = printerEl.GetString();
                        if (!string.IsNullOrWhiteSpace(printer))
                            _printerManager.SetPreferredPrinter(printer!);
                    }
                    await WriteJson(res, new { status = "ok", preferredPrinter = _printerManager.GetPreferredPrinter() });
                }
                else if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/print")
                {
                    using var reader = new System.IO.StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await reader.ReadToEndAsync();
                    var json = JsonDocument.Parse(body);
                    if (json.RootElement.TryGetProperty("content", out var contentEl))
                    {
                        var content = contentEl.GetString() ?? "";
                        _queue.Add(content);
                        await WriteJson(res, new { status = "queued", length = content.Length });
                    }
                    else
                    {
                        res.StatusCode = 400;
                        await WriteJson(res, new { error = "Missing 'content' field" });
                    }
                }
                else
                {
                    res.StatusCode = 404;
                    await WriteJson(res, new { error = "Not found" });
                }
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                await WriteJson(res, new { error = ex.Message });
                _logger.Error("Excepción en HTTP: " + ex);
            }
            finally
            {
                res.Close();
            }
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
