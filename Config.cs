using System;
using System.IO;

namespace PrintAgent
{
    public sealed class ServerConfig
    {
        public string Host { get; init; } = "localhost";
        public int Port { get; init; } = 5000;
        public bool ListenOnAllInterfaces { get; init; } = false;
        public string? Prefix { get; init; } = null;

        public static ServerConfig Load(Logger logger)
        {
            // Priority:
            // 1) PRINTAGENT_PREFIX (full prefix)
            // 2) PRINTAGENT_HOST / PRINTAGENT_PORT / PRINTAGENT_LISTEN_ALL
            // 3) .env-like file (printagent.env)
            // 4) defaults
            var envPrefix = Environment.GetEnvironmentVariable("PRINTAGENT_PREFIX");
            var envHost   = Environment.GetEnvironmentVariable("PRINTAGENT_HOST");
            var envPort   = Environment.GetEnvironmentVariable("PRINTAGENT_PORT");
            var envAll    = Environment.GetEnvironmentVariable("PRINTAGENT_LISTEN_ALL");

            // .env-like file next to the executable, or PRINTAGENT_ENV_PATH override
            var baseDir = AppContext.BaseDirectory;
            var envPath = Environment.GetEnvironmentVariable("PRINTAGENT_ENV_PATH");
            if (string.IsNullOrWhiteSpace(envPath))
                envPath = Path.Combine(baseDir, "printagent.env");

            var file = new DotEnv(envPath);
            if (!file.Exists)
            {
                // create a helpful template if missing
                try
                {
                    file.WriteTemplateIfMissing(baseDir);
                    logger.Info($"Config: creado template en {envPath}");
                }
                catch { /* ignore */ }
            }

            string? filePrefix = file.Get("PREFIX");
            string? fileHost   = file.Get("HOST");
            string? filePort   = file.Get("PORT");
            string? fileAll    = file.Get("LISTEN_ALL");

            string host = FirstNonEmpty(envHost, fileHost) ?? "localhost";
            int port = ParseInt(FirstNonEmpty(envPort, filePort), 5000);
            bool listenAll = ParseBool(FirstNonEmpty(envAll, fileAll), false);

            string? prefix = FirstNonEmpty(envPrefix, filePrefix);
            if (string.IsNullOrWhiteSpace(prefix))
            {
                prefix = listenAll
                    ? $"http://+:{port}/"
                    : $"http://{host}:{port}/";
            }

            logger.Info($"Config: prefix={prefix} (host={host}, port={port}, listenAll={listenAll})");

            return new ServerConfig
            {
                Host = host,
                Port = port,
                ListenOnAllInterfaces = listenAll,
                Prefix = prefix
            };
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            return null;
        }

        private static int ParseInt(string? value, int fallback)
        {
            if (int.TryParse(value, out var i) && i > 0 && i < 65536) return i;
            return fallback;
        }

        private static bool ParseBool(string? value, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            value = value.Trim().ToLowerInvariant();
            return value is "1" or "true" or "yes" or "y" or "on";
        }
    }

    internal sealed class DotEnv
    {
        private readonly string _path;
        private readonly System.Collections.Generic.Dictionary<string, string> _values =
            new(System.StringComparer.OrdinalIgnoreCase);

        public bool Exists => File.Exists(_path);

        public DotEnv(string path)
        {
            _path = path;
            if (File.Exists(_path))
                Load();
        }

        public string? Get(string key)
        {
            return _values.TryGetValue(key, out var v) ? v : null;
        }

        private void Load()
        {
            foreach (var raw in File.ReadAllLines(_path))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith(";")) continue;

                var idx = line.IndexOf('=');
                if (idx <= 0) continue;

                var k = line.Substring(0, idx).Trim();
                var v = line.Substring(idx + 1).Trim();

                // strip optional quotes
                if ((v.StartsWith('"') && v.EndsWith('"')) || (v.StartsWith('\'') && v.EndsWith('\'')))
                    v = v.Substring(1, v.Length - 2);

                _values[k] = v;
            }
        }

        public void WriteTemplateIfMissing(string baseDir)
        {
            // Only create if the file does not exist
            if (File.Exists(_path)) return;

            var template = 
@"# PrintAgent .env
# Cambiás esto y reiniciás el EXE, sin recompilar.
#
# OJO en Windows: si usás http://+:{PORT}/ podés necesitar:
# netsh http add urlacl url=http://+:{PORT}/ user=Everyone
#
# HOST: IP o hostname (se usa si LISTEN_ALL=false)
HOST=localhost
PORT=5000
# LISTEN_ALL: true -> escucha en toda la LAN (http://+:{PORT}/)
LISTEN_ALL=false
# PREFIX: si lo definís, tiene prioridad (ej: http://+:7000/ o http://192.168.0.50:7000/)
#PREFIX=http://+:5000/
";
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, template);
        }
    }
}
