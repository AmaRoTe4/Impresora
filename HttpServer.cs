
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

                if (req.HttpMethod == "POST" && (isText || isZpl))
                {
                    using var sr = new System.IO.StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await sr.ReadToEndAsync();
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
                    else
                    {
                        if (!json.RootElement.TryGetProperty("text", out var dataEl))
                        {
                            res.StatusCode = 400;
                            await Write(res, new { error = "Falta campo 'text'" });
                            return;
                        }

                        var payload = dataEl.GetString() ?? "";
                        _queue.Add(new PrintJob(JobKind.Text, payload));
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
