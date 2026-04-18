using Discord.WebSocket;
using OverWatchELD.VtcBot.Models;

namespace OverWatchELD.VtcBot.Services;

public sealed class VtcAccessService
{
    private readonly DiscordSocketClient? _client;

    public VtcAccessService(DiscordSocketClient? client)
    {
        _client = client;
    }

    public List<VtcMatchDto> MatchSupportedVtcs(
        string discordUserId,
        IReadOnlyCollection<DiscordGuildDto> userGuilds)
    {
        var results = new List<VtcMatchDto>();
        if (_client == null)
            return results;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Normal path: mutual guilds between the logged-in Discord user and the bot.
        foreach (var userGuild in userGuilds ?? Array.Empty<DiscordGuildDto>())
        {
            var botGuild = _client.Guilds.FirstOrDefault(x => x.Id.ToString() == userGuild.Id);
            if (botGuild == null)
                continue;

            seen.Add(botGuild.Id.ToString());
            results.Add(CreateMatch(botGuild, discordUserId));
        }

        // Fallback path:
        // if mutual matching comes back empty, still show every guild the bot is currently in.
        // This makes the Connect VTC page usable even when Discord mutual-guild matching
        // is not coming back correctly from OAuth for this user/session.
        if (results.Count == 0)
        {
            foreach (var botGuild in _client.Guilds)
            {
                var guildId = botGuild.Id.ToString();
                if (!seen.Add(guildId))
                    continue;

                results.Add(CreateMatch(botGuild, discordUserId));
            }
        }

        return results
            .OrderByDescending(x => x.IsManager)
            .ThenBy(x => x.VtcName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static VtcMatchDto CreateMatch(SocketGuild botGuild, string discordUserId)
    {
        bool isManager = false;
        string role = "Driver";

        try
        {
            if (ulong.TryParse(discordUserId, out var userId))
            {
                var member = botGuild.GetUser(userId);
                if (member != null)
                {
                    if (member.GuildPermissions.Administrator || member.GuildPermissions.ManageGuild)
                    {
                        isManager = true;
                        role = "Admin";
                    }
                    else if (member.GuildPermissions.ManageMessages ||
                             member.GuildPermissions.ManageChannels ||
                             member.GuildPermissions.KickMembers)
                    {
                        isManager = true;
                        role = "Manager";
                    }
                }
            }
        }
        catch
        {
        }

        return new VtcMatchDto
        {
            GuildId = botGuild.Id.ToString(),
            VtcName = botGuild.Name,
            LogoUrl = botGuild.IconId != null
                ? $"https://cdn.discordapp.com/icons/{botGuild.Id}/{botGuild.IconId}.png?size=256"
                : null,
            Role = role,
            IsManager = isManager
        };
    }
}
