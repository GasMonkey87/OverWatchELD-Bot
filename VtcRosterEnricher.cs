using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OverWatchELD.VtcBot
{
    public static class VtcRosterEnricher
    {
        /// <summary>
        /// Try to resolve a Discord display name from a linkedDiscordId.
        /// Uses the first visible guild as a lookup source (good for single-server VTC setups).
        /// </summary>
        public static string? TryResolveDiscordName(DiscordSocketClient? client, string? linkedDiscordId)
        {
            try
            {
                if (client == null) return null;
                if (string.IsNullOrWhiteSpace(linkedDiscordId)) return null;
                if (!ulong.TryParse(linkedDiscordId.Trim(), out var uid)) return null;

                // Prefer guild nickname/display name when available
                var guild = client.Guilds?.FirstOrDefault();
                if (guild != null)
                {
                    var guser = guild.GetUser(uid);
                    if (guser != null)
                    {
                        var dn = (guser.DisplayName ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(dn)) return dn;

                        var un = (guser.Username ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(un)) return un;
                    }
                }

                // Fallback: global cache
                var user = client.GetUser(uid);
                if (user != null)
                {
                    var un = (user.Username ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(un)) return un;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}