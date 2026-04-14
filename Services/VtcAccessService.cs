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

        foreach (var userGuild in userGuilds)
        {
            var botGuild = _client.Guilds.FirstOrDefault(x => x.Id.ToString() == userGuild.Id);
            if (botGuild == null)
                continue;

            bool isManager = false;
            string role = "Driver";

            try
            {
                var member = botGuild.GetUser(ulong.Parse(discordUserId));
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
            catch
            {
            }

            results.Add(new VtcMatchDto
            {
                GuildId = botGuild.Id.ToString(),
                VtcName = botGuild.Name,
                LogoUrl = botGuild.IconId != null
                    ? $"https://cdn.discordapp.com/icons/{botGuild.Id}/{botGuild.IconId}.png?size=256"
                    : null,
                Role = role,
                IsManager = isManager
            });
        }

        return results
            .OrderByDescending(x => x.IsManager)
            .ThenBy(x => x.VtcName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
