using System;
using System.IO;

namespace EngineStateManager
{
    internal static class ModLogger
    {
        private static string _logPath;
        private static bool _enabled;

        // Unique per script launch
        public static string SessionId { get; private set; } = "";

        public static bool Enabled => _enabled;

        public static void Init(bool enabled, string preferredPath, string fallbackPath)
        {
            _enabled = enabled;
            if (!_enabled)
                return;

            // Create a stable, readable session id (timestamp + short guid)
            SessionId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N").Substring(0, 8)}";

            if (!TryInitPath(preferredPath) && !TryInitPath(fallbackPath))
            {
                _enabled = false;
                return;
            }

            // Header
            Info("=== EngineStateManager Log Started ===");
            Info("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Info("SessionId: " + SessionId);
            Info("LogPath: " + _logPath);
        }

        private static bool TryInitPath(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                _logPath = path;

                // HARD RESET LOG ON STARTUP
                File.WriteAllText(_logPath, string.Empty);
                return true;
            }
            catch
            {
                return false;
            }
        }


        // Throttle repeated log lines by key to avoid spamming thousands of lines per second.
        private static readonly object _throttleLock = new object();
        private static readonly System.Collections.Generic.Dictionary<string, int> _lastLogTickByKey =
            new System.Collections.Generic.Dictionary<string, int>();

        public static void InfoThrottled(string key, string message, int minIntervalMs)
        {
            if (!_enabled) return;
            if (string.IsNullOrEmpty(key)) { Info(message); return; }
            int now = Environment.TickCount;
            bool allow = false;
            lock (_throttleLock)
            {
                int last;
                if (!_lastLogTickByKey.TryGetValue(key, out last) || unchecked(now - last) >= minIntervalMs)
                {
                    _lastLogTickByKey[key] = now;
                    allow = true;
                }
            }
            if (allow) Info(message);
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message, Exception ex = null)
        {
            if (ex != null)
                message = message + Environment.NewLine + ex;

            Write("ERROR", message);
        }

        private static void Write(string level, string message)
        {
            if (!_enabled || string.IsNullOrEmpty(_logPath))
                return;

            try
            {
                // Include SessionId in every line
                string line = $"[{DateTime.Now:HH:mm:ss}] [{SessionId}] {level}: {message}";
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch
            {
                // Never allow logging to crash the game.
            }
        }
    }
}
