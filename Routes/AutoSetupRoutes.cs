using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot.Routes;

public static class AutoSetupRoutes
{
    public static void Register(IEndpointRouteBuilder app, BotServices services, JsonSerializerOptions jsonWrite)
    {
        // The old /api/vtc/setup/auto-discord endpoint was removed from this file
        // to prevent duplicate route conflicts.
        //
        // The only active auto-discord endpoint is now:
        // Routes/VtcDiscordAutoSetupRoutes.cs
        //
        // Keep this file so Program.cs can still call:
        // AutoSetupRoutes.Register(app, services, JsonWriteOpts);
    }
}
