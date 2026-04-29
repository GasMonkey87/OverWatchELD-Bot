using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot.Routes;

public static class AutoSetupRoutes
{
    public static void Register(IEndpointRouteBuilder app, BotServices services, JsonSerializerOptions jsonWrite)
    {
        // 🔥 IMPORTANT:
        // The /api/vtc/setup/auto-discord route was REMOVED from this file
        // to prevent conflicts with:
        //
        // Routes/VtcDiscordAutoSetupRoutes.cs
        //
        // Do NOT add it back here.

        // This file remains so Program.cs does not break:
        // AutoSetupRoutes.Register(app, services, JsonWriteOpts);

        // You can add OTHER setup routes here later if needed.
    }
}
