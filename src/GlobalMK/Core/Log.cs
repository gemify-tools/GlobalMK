using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GlobalMK
{
    /// <summary>Thread-safe log: keeps recent lines in memory, mirrors to a small file, raises an event for the UI.</summary>
    public static class Log
    {
        private const int MaxRecent = 500;
        private const long MaxFileBytes = 1024 * 1024;

        private static readonly object Sync = new object();
        private static readonly Queue<string> RecentLines = new Queue<string>();
        private static string _filePath;

        public static event Action<string> Line;

        public static void Init(string filePath)
        {
            lock (Sync)
            {
                _filePath = filePath;
                try
                {
                    var fi = new FileInfo(filePath);
                    if (fi.Exists && fi.Length > MaxFileBytes)
                    {
                        string old = filePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(filePath, old);
                    }
                }
                catch { /* logging must never throw */ }
            }
        }

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);
        public static void Error(string message) => Write("ERROR", message);

        public static string[] Recent()
        {
            lock (Sync) return RecentLines.ToArray();
        }

        private static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + message;
            lock (Sync)
            {
                RecentLines.Enqueue(line);
                while (RecentLines.Count > MaxRecent) RecentLines.Dequeue();
                if (_filePath != null)
                {
                    try { File.AppendAllText(_filePath, line + Environment.NewLine, Encoding.UTF8); }
                    catch { }
                }
            }
            try { Line?.Invoke(line); } catch { }
        }
    }
}
