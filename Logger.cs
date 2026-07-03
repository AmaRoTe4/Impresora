
using System;
using System.IO;

namespace PrintAgent
{
    public class Logger
    {
        private const long MaxBytes = 5 * 1024 * 1024; // 5MB

        private readonly string _logPath;
        private readonly string _backupPath;
        private readonly object _writeLock = new();

        public Logger()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrintAgent");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "agent.log");
            _backupPath = Path.Combine(dir, "agent.log.bak");
        }

        public void Info(string msg) => Write("INFO", msg);
        public void Error(string msg) => Write("ERROR", msg);

        private void Write(string level, string msg)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
            lock (_writeLock)
            {
                RotateIfNeeded();
                File.AppendAllLines(_logPath, new[] { line });
            }
        }

        private void RotateIfNeeded()
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists || info.Length < MaxBytes) return;

            try
            {
                File.Copy(_logPath, _backupPath, overwrite: true);
                File.Delete(_logPath);
            }
            catch
            {
                // si la rotación falla, seguimos escribiendo al archivo actual
                // en vez de perder el log o tirar una excepción desde el logger
            }
        }
    }
}
