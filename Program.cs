
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PrintAgent
{
    internal static class Program
    {
        private static readonly BlockingCollection<PrintJob> _queue = new BlockingCollection<PrintJob>();

        [STAThread]
        static void Main(string[] args)
        {
            using var singleInstanceMutex = new Mutex(initiallyOwned: true, "Global\\PrintAgent_SingleInstance", out bool isNew);
            if (!isNew)
            {
                MessageBox.Show("PrintAgent ya está en ejecución (ver bandeja del sistema).", "PrintAgent",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ApplicationConfiguration.Initialize();

            var logger = new Logger();

            Application.ThreadException += (s, e) => logger.Error("Excepción de UI no manejada: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => logger.Error("Excepción fatal no manejada: " + e.ExceptionObject);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            var manager = new PrinterManager(logger);
            Task.Run(() => PrintWorker(manager, logger));

            var config = ServerConfig.Load(logger);
            var server = new HttpServer(manager, _queue, logger, prefix: config.Prefix ?? "http://localhost:5000/");
            logger.Info($"PrintAgent iniciado en {config.Prefix}");
            _ = Task.Run(server.StartAsync);

            Application.Run(new TrayApplicationContext(server, manager, logger));
        }

        private static void PrintWorker(PrinterManager mgr, Logger logger)
        {
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                try
                {
                    switch (job.Kind)
                    {
                        case JobKind.Text:
                            mgr.PrintText(job.Payload);
                            break;
                        case JobKind.Zpl:
                            mgr.PrintZpl(job.Payload);
                            break;
                    }
                    logger.Info($"Job {job.Kind} impreso.");
                }
                catch (Exception ex)
                {
                    logger.Error($"Error imprimiendo {job.Kind}: {ex.Message}");
                }
            }
        }
    }

    public record PrintJob(JobKind Kind, string Payload);
    public enum JobKind { Text, Zpl }
}
