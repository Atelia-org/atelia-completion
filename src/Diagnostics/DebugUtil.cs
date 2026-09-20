using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Atelia.Diagnostics {
    /// <summary>
    /// 轻量级诊断输出工具。控制台与文件均为 best-effort，不得作为审计回执。
    /// </summary>
    public static class DebugUtil {
        private const int MaximumTextLength = 2048;
        private const int MaximumCategoryLength = 64;

        private static readonly DebugLevel _fileLevel = ParseLevelOrDefault(
            Environment.GetEnvironmentVariable("ATELIA_DEBUG_FILE_LEVEL"),
            DebugLevel.Warning
        );
        private static readonly DebugLevel _consoleLevel = ParseLevelOrDefault(
            Environment.GetEnvironmentVariable("ATELIA_DEBUG_CONSOLE_LEVEL"),
            DebugLevel.Warning
        );

        [Conditional("DEBUG")]
        public static void Debug(string category, string text) {
            Write(DebugLevel.Debug, category, text);
        }

        public static void Warning(string category, string text) {
            Write(DebugLevel.Warning, category, text);
        }

        public static void Error(string category, string text) {
            Write(DebugLevel.Error, category, text);
        }

        private static void Write(DebugLevel level, string category, string text) {
            try {
                bool writeFile = level >= _fileLevel;
                bool writeConsole = level >= _consoleLevel;
                if (!writeFile && !writeConsole) {
                    return;
                }

                string message = FormatMessage(
                    DateTime.UtcNow.ToString(
                        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                        CultureInfo.InvariantCulture
                    ),
                    level,
                    category,
                    text
                );

                if (writeFile) {
                    WriteToLogFile(category, message);
                }

                if (writeConsole) {
                    Console.Error.WriteLine(message);
                }
            }
            catch {
                // Diagnostics must never rewrite a provider or tool outcome.
            }
        }

        private static string FormatMessage(
            string timestamp,
            DebugLevel level,
            string category,
            string text
        ) {
            return $"{timestamp} [{GetLevelCode(level)} {SanitizeSingleLine(category, MaximumCategoryLength)}] "
                + SanitizeSingleLine(text, MaximumTextLength);
        }

        private static void WriteToLogFile(string category, string message) {
            string logDirectory = Path.Combine(".atelia", "debug-logs");
            Directory.CreateDirectory(logDirectory);
            string logFile = Path.Combine(
                logDirectory,
                GetSafeCategoryFileName(category) + ".log"
            );
            File.AppendAllText(logFile, message + Environment.NewLine, new UTF8Encoding(false));
        }

        private static string GetSafeCategoryFileName(string category) {
            string safeCategory = SanitizeSingleLine(category, MaximumCategoryLength)
                .ToLowerInvariant();
            var builder = new StringBuilder(safeCategory.Length);
            foreach (char character in safeCategory) {
                if (char.IsLetterOrDigit(character)
                    || character == '-'
                    || character == '_') {
                    _ = builder.Append(character);
                }
                else {
                    _ = builder.Append('_');
                }
            }

            if (builder.Length == 0) { return "unknown"; }

            string fileName = builder.ToString();
            return IsReservedWindowsDeviceName(category)
                ? "_" + fileName
                : fileName;
        }

        private static bool IsReservedWindowsDeviceName(string category) {
            string baseName = category.Split('.')[0];
            switch (baseName.ToUpperInvariant()) {
                case "CON":
                case "PRN":
                case "AUX":
                case "NUL":
                case "COM1":
                case "COM2":
                case "COM3":
                case "COM4":
                case "COM5":
                case "COM6":
                case "COM7":
                case "COM8":
                case "COM9":
                case "LPT1":
                case "LPT2":
                case "LPT3":
                case "LPT4":
                case "LPT5":
                case "LPT6":
                case "LPT7":
                case "LPT8":
                case "LPT9":
                    return true;
                default:
                    return false;
            }
        }

        private static string SanitizeSingleLine(string value, int maximumLength) {
            if (string.IsNullOrEmpty(value)) {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (char character in value) {
                _ = builder.Append(char.IsControl(character) ? ' ' : character);
            }

            string result = builder.ToString();
            if (result.Length <= maximumLength) {
                return result;
            }

            const string truncationSuffix = "...<truncated>";
            return result.Substring(
                    0,
                    maximumLength - truncationSuffix.Length
                )
                + truncationSuffix;
        }

        private static DebugLevel ParseLevelOrDefault(string raw, DebugLevel fallback) {
            if (string.IsNullOrWhiteSpace(raw)) {
                return fallback;
            }

            switch (raw.Trim().ToUpperInvariant()) {
                case "DEBUG":
                    return DebugLevel.Debug;
                case "WARNING":
                    return DebugLevel.Warning;
                case "ERROR":
                    return DebugLevel.Error;
                case "OFF":
                    return DebugLevel.Off;
                default:
                    return fallback;
            }
        }

        private static string GetLevelCode(DebugLevel level) {
            switch (level) {
                case DebugLevel.Debug:
                    return "DBG";
                case DebugLevel.Warning:
                    return "WRN";
                case DebugLevel.Error:
                    return "ERR";
                default:
                    return level.ToString().ToUpperInvariant();
            }
        }

        private enum DebugLevel {
            Debug = 0,
            Warning = 1,
            Error = 2,
            Off = 3,
        }
    }
}
