using System;
using System.Runtime.CompilerServices;

namespace Atelia.Completion.Tests;

/// <summary>
/// Test-host diagnostics policy. After the library's file-sink default moved to
/// DEBUG, this host pins ATELIA_DEBUG_FILE_LEVEL=Warning so expected negative-path
/// warnings still land in the file sink while the test working directory does not
/// accumulate Debug-level log noise. External explicit settings win. The console
/// policy keeps Error-only output; set ATELIA_DEBUG_CONSOLE_LEVEL before running
/// tests to override it.
/// </summary>
internal static class TestHostDiagnostics {
    [ModuleInitializer]
    internal static void Initialize() {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ATELIA_DEBUG_FILE_LEVEL")
        )) {
            Environment.SetEnvironmentVariable("ATELIA_DEBUG_FILE_LEVEL", "Warning");
        }
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ATELIA_DEBUG_CONSOLE_LEVEL")
        )) {
            Environment.SetEnvironmentVariable("ATELIA_DEBUG_CONSOLE_LEVEL", "Error");
        }
    }
}
