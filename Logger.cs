
using System;
using System.IO;

namespace PrintAgent
{
    public class Logger
    {
        private readonly string _logPath;

        public Logger()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrintAgent");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "agent.log");
        }

        public void Info(string msg) => Write("INFO", msg);
        public void Error(string msg) => Write("ERROR", msg);

        private void Write(string level, string msg)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
            File.AppendAllLines(_logPath, new[] { line });
        }
    }
}
