using System;
using System.Runtime.CompilerServices;

namespace Atelia.Completion.Tests;

/// <summary>
/// Keeps expected negative-path library warnings out of normal test console output.
/// Debug file sinks continue to record them; set ATELIA_DEBUG_CONSOLE_LEVEL before
/// running tests to override this default.
/// </summary>
internal static class TestHostDiagnostics {
    [ModuleInitializer]
    internal static void Initialize() {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ATELIA_DEBUG_CONSOLE_LEVEL")
        )) {
            Environment.SetEnvironmentVariable("ATELIA_DEBUG_CONSOLE_LEVEL", "Error");
        }
    }
}
