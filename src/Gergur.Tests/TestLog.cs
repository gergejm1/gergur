using System.Runtime.CompilerServices;
using Gergur.Diagnostics;

namespace Gergur.Tests;

/// <summary>
/// Keeps the test run out of the real debug.log. Tests fail view builds and writes on
/// purpose, and every such failure is logged whether or not tracing is on, so a full run
/// put about ten fake failures into the file the browser's error answers send a person to,
/// with nothing to tell them from real ones.
/// </summary>
internal static class TestLog
{
    internal static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gergur-tests", "debug.log");

    // A module initializer is meant for applications, which is what the analyzer says. It
    // is used here because this has to happen before any test touches the app, whatever
    // order they run in, and xunit 2 has no fixture that spans the whole assembly.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void KeepOutOfTheRealLog() => DebugLog.FilePath = Path;
#pragma warning restore CA2255
}
