// This file is intentionally NOT a runnable top-level Program.
// The OverWatchELD.VtcBot project is the Railway entrypoint.
// Keeping this file as a harmless namespaced placeholder prevents CS8804
// when the nested Hub folder is accidentally included by SDK default globs.
namespace OverWatchELD.Hub;

internal static class HubProgramPlaceholder
{
    public static string Name => "OverWatchELD.Hub placeholder - not used by VtcBot";
}
