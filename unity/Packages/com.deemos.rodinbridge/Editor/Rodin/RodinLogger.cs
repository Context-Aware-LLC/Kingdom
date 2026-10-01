using System;
using System.IO;
using UnityEngine;

using Debug = UnityEngine.Debug;

namespace Rodin
{
    // 面向客户排障的文件日志：每条带毫秒级时间戳（task Id 未必等于 Web 有效 id，时间是首要关联依据）。
    // 线程安全（Server/Task 在后台线程写入），写入异常一律静默吞掉，绝不影响插件功能。
    public static class RodinLogger
    {
        public enum Level { INFO, WARN, ERROR }

        private static readonly object _lock = new object();

        // 缓存日志目录：Application.dataPath 仅能在主线程访问，故需提前在主线程 Initialize。
        private static string _logDir;
        private static string _currentDateStamp;

        private const int MaxFiles = 7;

        // 在主线程调用一次（如打开插件窗口时），以便后台线程也能安全写文件。
        public static void Initialize()
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(_logDir))
                {
                    return;
                }
                try
                {
                    string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                    _logDir = Path.Combine(projectRoot, "Logs", "RodinBridge");
                    Directory.CreateDirectory(_logDir);
                }
                catch
                {
                    // 目录不可用时，文件写入将被跳过，但 Console 仍可用。
                }
            }
        }

        public static void Info(string message, string sid = null) => Write(Level.INFO, message, sid);
        public static void Warn(string message, string sid = null) => Write(Level.WARN, message, sid);
        public static void Error(string message, string sid = null) => Write(Level.ERROR, message, sid);

        private static void Write(Level level, string message, string sid)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [sid={(string.IsNullOrEmpty(sid) ? "-" : sid)}] {message}";

            // 镜像到 Unity Console（Debug.Log 跨线程安全）。
            switch (level)
            {
                case Level.WARN: Debug.LogWarning(line); break;
                case Level.ERROR: Debug.LogError(line); break;
                default: Debug.Log(line); break;
            }

            lock (_lock)
            {
                if (string.IsNullOrEmpty(_logDir))
                {
                    return;
                }
                try
                {
                    string today = DateTime.Now.ToString("yyyyMMdd");
                    string path = Path.Combine(_logDir, $"rodinbridge-{today}.log");
                    File.AppendAllText(path, line + Environment.NewLine);

                    // 跨天后才执行清理：按文件数量保留最近 MaxFiles 份，避免“久未使用”时误删。
                    if (_currentDateStamp != today)
                    {
                        _currentDateStamp = today;
                        Cleanup();
                    }
                }
                catch
                {
                    // 写入失败不影响业务。
                }
            }
        }

        // 当日志文件数超过 MaxFiles 时，删除最早的若干份（文件名 yyyyMMdd 字典序即时间序）。
        private static void Cleanup()
        {
            try
            {
                string[] files = Directory.GetFiles(_logDir, "rodinbridge-*.log");
                if (files.Length <= MaxFiles)
                {
                    return;
                }
                Array.Sort(files, StringComparer.Ordinal);
                int toDelete = files.Length - MaxFiles;
                for (int i = 0; i < toDelete; i++)
                {
                    File.Delete(files[i]);
                }
            }
            catch
            {
                // 清理失败不影响业务。
            }
        }
    }
}
