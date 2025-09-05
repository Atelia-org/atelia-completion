using System;
using System.Collections.Generic;
using System.Linq;

namespace Atelia.Diagnostics {
    /// <summary>
    /// 用法：DebugUtil.Print("TypeHash", "内容");
    /// 环境变量 ATELIA_DEBUG_CATEGORIES 支持逗号、分号分隔的类别列表，如：TypeHash,Test,Outline。
    /// 设置为 ALL 可输出所有调试信息。
    /// </summary>
    public static class DebugUtil {
        private static readonly HashSet<string> _enabledCategories;
        private static readonly bool _allEnabled;

        static DebugUtil() {
            var env = Environment.GetEnvironmentVariable("ATELIA_DEBUG_CATEGORIES") ?? string.Empty;
            var cats = new HashSet<string>(
                env.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(s => s.Trim().ToUpperInvariant())
            );
            _enabledCategories = cats;
            _allEnabled = cats.Contains("ALL");
        }

        /// <summary>
        /// 输出调试信息（仅当类别被允许时）。
        /// </summary>
        /// <param name="category">类别，如"TypeHash"</param>
        /// <param name="text">调试内容</param>
        public static void Print(string category, string text) {
            if (_allEnabled || _enabledCategories.Contains(category.ToUpperInvariant())) {
                Console.WriteLine($"[DBG {category}] {text}");
            }
        }
    }
}
