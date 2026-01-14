
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace PrintAgent
{
    internal static class Program
    {
        private static readonly BlockingCollection<PrintJob> _queue = new BlockingCollection<PrintJob>();

        [STAThread]
        static async Task Main(string[] args)
        {
            var logger  = new Logger();
            var manager = new PrinterManager(logger);
            Task.Run(() => PrintWorker(manager, logger));

            var config = ServerConfig.Load(logger);
            var server = new HttpServer(manager, _queue, logger, prefix: config.Prefix ?? "http://localhost:5000/");
            logger.Info($"PrintAgent iniciado en {config.Prefix}");
            await server.StartAsync();
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
