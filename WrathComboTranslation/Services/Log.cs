using System;

namespace WrathComboTranslation.Services;

/// <summary>
///     Thin facade over Dalamud's logger.
/// </summary>
/// <remarks>
///     The services below can run before <see cref="Plugin" />'s services are injected, and
///     are exercised by the offline test harness where no Dalamud logger exists at all, so
///     every call has to tolerate a missing sink rather than throwing.
/// </remarks>
internal static class Log
{
    public static void Information(string message)
        => Plugin.Log?.Information(message);

    public static void Warning(string message)
        => Plugin.Log?.Warning(message);

    public static void Warning(Exception ex, string message)
        => Plugin.Log?.Warning(ex, message);

    public static void Error(string message)
        => Plugin.Log?.Error(message);

    public static void Error(Exception ex, string message)
        => Plugin.Log?.Error(ex, message);
}
