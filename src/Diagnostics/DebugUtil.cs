using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Atelia.Diagnostics {
    /// <summary>
    /// 用法：DebugUtil.Print("TypeHash", "内容");
    /// 环境变量 ATELIA_DEBUG_CATEGORIES 支持逗号、分号分隔的类别列表，如：TypeHash,Test,Outline。
    /// 设置为 ALL 可输出所有调试信息。
    /// 调试信息同时输出到控制台和 .codecortex/logs/{category}.log（默认，用于Agent实时读取），若不可用回退到 gitignore/debug-logs。
    /// </summary>
    public static class DebugUtil {
        private static readonly HashSet<string> _enabledCategories;
        private static readonly bool _allEnabled;
        private static readonly string _logDir;

        static DebugUtil() {
            string folderName = "debug-logs";
            var env = Environment.GetEnvironmentVariable("ATELIA_DEBUG_CATEGORIES") ?? string.Empty;
            var cats = new HashSet<string>(
                env.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(s => s.Trim().ToUpperInvariant())
            );
            _enabledCategories = cats;
            _allEnabled = cats.Contains("ALL");

            // 设置日志目录：优先 .codecortex/logs（便于Agent实时读取），其次 gitignore/debug-logs，最后当前目录
            var cwd = Directory.GetCurrentDirectory();
            string selected = null;
            var candidates = new[] {
                Path.Combine(cwd, ".codecortex", folderName),
                Path.Combine(cwd, "gitignore", folderName)
            };
            foreach (var c in candidates) {
                try {
                    Directory.CreateDirectory(c);
                    selected = c;
                    break;
                }
                catch { }
            }
            _logDir = selected ?? cwd;
        }

        /// <summary>
        /// 输出调试信息（仅当类别被允许时）。
        /// </summary>
        /// <param name="category">类别，如"TypeHash"</param>
        /// <param name="text">调试内容</param>
        public static void Print(string category, string text) {
            var message = $"[DBG {category}] {text}";
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var logMessage = $"{timestamp} {message}";

            // 始终写入文件，便于事后分析与Agent实时读取
            WriteToLogFile(category, logMessage);

            // 仅在类别启用时输出到控制台，避免干扰单元测试视线
            if (_allEnabled || _enabledCategories.Contains(category.ToUpperInvariant())) {
                Console.WriteLine(logMessage);
            }
        }

        /// <summary>
        /// 清空指定类别的日志文件
        /// </summary>
        /// <param name="category">类别名</param>
        public static void ClearLog(string category) {
            try {
                var logFile = Path.Combine(_logDir, $"{category.ToLowerInvariant()}.log");
                if (File.Exists(logFile)) {
                    File.WriteAllText(logFile, string.Empty);
                }
            }
            catch {
                // 忽略清空失败
            }
        }

        private static void WriteToLogFile(string category, string message) {
            try {
                var logFile = Path.Combine(_logDir, $"{category.ToLowerInvariant()}.log");
                File.AppendAllText(logFile, message + Environment.NewLine);
            }
            catch {
                // 忽略写入失败，避免影响主流程
            }
        }
    }
}
