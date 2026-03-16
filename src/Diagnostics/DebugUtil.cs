using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Atelia.Diagnostics {
    public enum DebugLevel {
        Trace = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
    }

    public enum DebugEventKind {
        Message = 0,
        Start = 1,
        Success = 2,
        Skip = 3,
        Failure = 4,
        Exception = 5,
    }

    /// <summary>
    /// 轻量级调试日志工具。
    /// - 推荐优先使用 <see cref="Trace"/>, <see cref="Info"/>, <see cref="Warning"/>, <see cref="Error"/>.
    /// - <see cref="Trace"/> / <see cref="Info"/> 在 Release 调用点默认被裁掉（<c>[Conditional("DEBUG")]</c>）。
    /// - 文件/控制台 sink 默认按 Build 配置分层：
    ///   DEBUG 默认记录 Trace+；RELEASE 默认记录 Warning+。
    /// - 可通过环境变量覆盖：
    ///   <c>ATELIA_DEBUG_CATEGORIES</c>,
    ///   <c>ATELIA_DEBUG_FILE_LEVEL</c>,
    ///   <c>ATELIA_DEBUG_CONSOLE_LEVEL</c>.
    /// </summary>
    public static class DebugUtil {
        private static readonly HashSet<string> _enabledCategories;
        private static readonly bool _allEnabled;
        private static readonly string _logDir;
        private static readonly DebugLevel _fileMinLevel;
        private static readonly DebugLevel _consoleMinLevel;

        static DebugUtil() {
            string folderName = "debug-logs";
            string categoriesRaw = Environment.GetEnvironmentVariable("ATELIA_DEBUG_CATEGORIES") ?? string.Empty;
            _enabledCategories = new HashSet<string>(
                categoriesRaw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().ToUpperInvariant())
            );
            _allEnabled = _enabledCategories.Contains("ALL");

            _fileMinLevel = ParseLevelOrDefault(
                Environment.GetEnvironmentVariable("ATELIA_DEBUG_FILE_LEVEL"),
                GetDefaultFileMinLevel()
            );
            _consoleMinLevel = ParseLevelOrDefault(
                Environment.GetEnvironmentVariable("ATELIA_DEBUG_CONSOLE_LEVEL"),
                GetDefaultConsoleMinLevel()
            );

            var cwd = Directory.GetCurrentDirectory();
            string selected = null;
            var candidates = new[] {
                Path.Combine(cwd, ".atelia", folderName),
                Path.Combine(cwd, "gitignore", folderName)
            };
            foreach (var candidate in candidates) {
                try {
                    Directory.CreateDirectory(candidate);
                    selected = candidate;
                    break;
                }
                catch {
                }
            }
            _logDir = selected ?? cwd;
        }

        [Conditional("DEBUG")]
        public static void Trace(string category, string text, DebugEventKind eventKind = DebugEventKind.Message) {
            Log(DebugLevel.Trace, category, text, null, eventKind);
        }

        [Conditional("DEBUG")]
        public static void Info(string category, string text, DebugEventKind eventKind = DebugEventKind.Message) {
            Log(DebugLevel.Info, category, text, null, eventKind);
        }

        public static void Warning(
            string category,
            string text,
            Exception exception = null,
            DebugEventKind eventKind = DebugEventKind.Message
        ) {
            Log(DebugLevel.Warning, category, text, exception, eventKind);
        }

        public static void Error(
            string category,
            string text,
            Exception exception = null,
            DebugEventKind eventKind = DebugEventKind.Exception
        ) {
            Log(DebugLevel.Error, category, text, exception, eventKind);
        }

        public static void Log(
            DebugLevel level,
            string category,
            string text,
            Exception exception = null,
            DebugEventKind eventKind = DebugEventKind.Message
        ) {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string formatted = FormatMessage(timestamp, level, category, text, exception, eventKind);

            if (ShouldWriteToLogFile(level)) {
                WriteToLogFile(category, formatted);
            }

            if (ShouldWriteToConsole(level, category)) {
                Console.WriteLine(formatted);
            }
        }

        [Obsolete("Use DebugUtil.Trace/Info/Warning/Error instead.")]
        public static void Print(string category, string text) {
            Log(DebugLevel.Trace, category, text);
        }

        public static void ClearLog(string category) {
            try {
                string logFile = Path.Combine(_logDir, $"{category.ToLowerInvariant()}.log");
                if (File.Exists(logFile)) {
                    File.WriteAllText(logFile, string.Empty);
                }
            }
            catch {
            }
        }

        private static string FormatMessage(
            string timestamp,
            DebugLevel level,
            string category,
            string text,
            Exception exception,
            DebugEventKind eventKind
        ) {
            string message = $"{timestamp} [{GetLevelCode(level)} {category}]";
            if (eventKind != DebugEventKind.Message) {
                message += $" [{eventKind}]";
            }
            message += $" {text}";

            if (exception != null) {
                message += Environment.NewLine + exception;
            }

            return message;
        }

        private static bool ShouldWriteToLogFile(DebugLevel level) {
            return level >= _fileMinLevel;
        }

        private static bool ShouldWriteToConsole(DebugLevel level, string category) {
            if (level < _consoleMinLevel) { return false; }

            if (level >= DebugLevel.Warning) { return true; }

            return _allEnabled || _enabledCategories.Contains(category.ToUpperInvariant());
        }

        private static void WriteToLogFile(string category, string message) {
            try {
                string logFile = Path.Combine(_logDir, $"{category.ToLowerInvariant()}.log");
                File.AppendAllText(logFile, message + Environment.NewLine);
            }
            catch {
            }
        }

        private static DebugLevel ParseLevelOrDefault(string raw, DebugLevel fallback) {
            if (string.IsNullOrWhiteSpace(raw)) { return fallback; }

            switch (raw.Trim().ToUpperInvariant()) {
                case "TRACE":
                case "TRC":
                    return DebugLevel.Trace;
                case "INFO":
                case "INF":
                    return DebugLevel.Info;
                case "WARN":
                case "WARNING":
                case "WRN":
                    return DebugLevel.Warning;
                case "ERROR":
                case "ERR":
                    return DebugLevel.Error;
                default:
                    return fallback;
            }
        }

        private static string GetLevelCode(DebugLevel level) {
            switch (level) {
                case DebugLevel.Trace:
                    return "TRC";
                case DebugLevel.Info:
                    return "INF";
                case DebugLevel.Warning:
                    return "WRN";
                case DebugLevel.Error:
                    return "ERR";
                default:
                    return level.ToString().ToUpperInvariant();
            }
        }

        private static DebugLevel GetDefaultFileMinLevel() {
#if DEBUG
            return DebugLevel.Trace;
#else
            return DebugLevel.Warning;
#endif
        }

        private static DebugLevel GetDefaultConsoleMinLevel() {
#if DEBUG
            return DebugLevel.Trace;
#else
            return DebugLevel.Warning;
#endif
        }
    }
}
