
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace PrintAgent
{
    internal static class Program
    {
        private static readonly BlockingCollection<string> _queue = new BlockingCollection<string>();

        [STAThread]
        static async Task Main(string[] args)
        {
            var logger  = new Logger();
            var manager = new PrinterManager(logger);

            // Worker que serializa impresiones
            Task.Run(() => PrintWorker(manager, logger));

            var server = new HttpServer(manager, _queue, logger);
            logger.Info("PrintAgent iniciado en http://localhost:5000");

            await server.StartAsync();
        }

        private static void PrintWorker(PrinterManager mgr, Logger logger)
        {
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                try
                {
                    mgr.PrintText(job);
                    logger.Info($"Impresión exitosa ({job.Length} chars) en '{mgr.GetPreferredPrinter()}'.");
                }
                catch (Exception ex)
                {
                    logger.Error("Error imprimiendo: " + ex.Message);
                }
            }
        }
    }
}
