using Discord;
using Discord.WebSocket;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Services;

public static class SlashCommandService
{
    public static async Task RegisterSlashCommandsForGuildAsync(SocketGuild guild)
    {
        try
        {
            var perf = new SlashCommandBuilder()
                .WithName("performance")
                .WithDescription("Show your current performance and rank in this VTC.");

            var leaderboard = new SlashCommandBuilder()
                .WithName("leaderboard")
                .WithDescription("Show the top drivers leaderboard for this VTC.")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("take")
                    .WithDescription("How many drivers to show (default 10, max 25).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false));

            try
            {
                var existing = await guild.GetApplicationCommandsAsync();
                foreach (var cmd in existing)
                {
                    if (cmd.Name.Equals("performance", StringComparison.OrdinalIgnoreCase) ||
                        cmd.Name.Equals("leaderboard", StringComparison.OrdinalIgnoreCase))
                    {
                        try { await cmd.DeleteAsync(); } catch { }
                    }
                }
            }
            catch { }

            await guild.CreateApplicationCommandAsync(perf.Build());
            await guild.CreateApplicationCommandAsync(leaderboard.Build());
        }
        catch { }
    }

    public static async Task HandleInteractionAsync(SocketInteraction inter, PerformanceStore? perfStore)
    {
        try
        {
            if (inter is not SocketSlashCommand slash)
                return;

            var name = (slash.Data?.Name ?? "").Trim().ToLowerInvariant();
            if (name == "performance")
            {
                await HandleSlashPerformanceAsync(slash, perfStore);
                return;
            }

            if (name == "leaderboard")
            {
                await HandleSlashLeaderboardAsync(slash, perfStore);
                return;
            }
        }
        catch { }

        try
        {
            if (!inter.HasResponded)
                await inter.RespondAsync("⚠️ Command not handled.", ephemeral: true);
        }
        catch { }
    }

    public static async Task HandleSlashPerformanceAsync(SocketSlashCommand cmd, PerformanceStore? perfStore)
    {
        if (perfStore == null)
        {
            await cmd.RespondAsync("❌ Performance store not ready.", ephemeral: true);
            return;
        }

        if (cmd.GuildId == null || cmd.GuildId.Value == 0)
        {
            await cmd.RespondAsync("❌ Run this command inside your VTC server (not DM).", ephemeral: true);
            return;
        }

        var guildIdStr = cmd.GuildId.Value.ToString();
        var userIdStr = cmd.User.Id.ToString();

        var (perf, rank, total) = perfStore.GetWithRank(guildIdStr, userIdStr);
        if (perf == null)
        {
            await cmd.RespondAsync("📊 No performance data yet for you.\nMake sure your ELD is paired and sending performance updates.", ephemeral: true);
            return;
        }

        var top5 = perfStore.GetTop(guildIdStr, 5);
        var topLines = new List<string>();
        for (int i = 0; i < top5.Count; i++)
        {
            var p = top5[i];
            var tag = p.DiscordUserId == userIdStr ? "**(you)**" : "";
            topLines.Add($"`#{i + 1}` <@{p.DiscordUserId}> — **{p.Score:n0}** {tag}");
        }

        var eb = new EmbedBuilder()
            .WithTitle("🏁 Your Performance")
            .WithDescription($"Rank **#{rank}** of **{total}** drivers\n\n**Top 5 (Score):**\n{string.Join("\n", topLines)}")
            .AddField("Week Miles", $"{perf.MilesWeek:n0}", true)
            .AddField("Week Loads", $"{perf.LoadsWeek:n0}", true)
            .AddField("Performance %", $"{perf.PerformancePct:0.0}%", true)
            .AddField("Month Miles", $"{perf.MilesMonth:n0}", true)
            .AddField("Month Loads", $"{perf.LoadsMonth:n0}", true)
            .AddField("Score", $"{perf.Score:n0}", true)
            .WithFooter($"Last updated: {perf.UpdatedUtc:yyyy-MM-dd HH:mm} UTC");

        await cmd.RespondAsync(embed: eb.Build(), ephemeral: false);
    }

    public static async Task HandleSlashLeaderboardAsync(SocketSlashCommand cmd, PerformanceStore? perfStore)
    {
        if (perfStore == null)
        {
            await cmd.RespondAsync("❌ Performance store not ready.", ephemeral: true);
            return;
        }

        if (cmd.GuildId == null || cmd.GuildId.Value == 0)
        {
            await cmd.RespondAsync("❌ Run this command inside your VTC server (not DM).", ephemeral: true);
            return;
        }

        int take = 10;
        try
        {
            var opt = cmd.Data.Options.FirstOrDefault(x => x.Name == "take");
            if (opt?.Value != null && int.TryParse(opt.Value.ToString(), out var v))
                take = v;
        }
        catch { }

        take = Math.Clamp(take, 1, 25);
        var top = perfStore.GetTop(cmd.GuildId.Value.ToString(), take);

        if (top.Count == 0)
        {
            await cmd.RespondAsync("📊 No performance data yet.\nOnce ELD starts sending updates, the leaderboard will populate.", ephemeral: true);
            return;
        }

        var lines = new List<string>();
        for (int i = 0; i < top.Count; i++)
        {
            var p = top[i];
            lines.Add($"`#{i + 1}` <@{p.DiscordUserId}> — **{p.Score:n0}** | {p.MilesWeek:n0} mi | {p.LoadsWeek} loads | {p.PerformancePct:0.0}%");
        }

        var eb = new EmbedBuilder()
            .WithTitle("🏆 VTC Leaderboard")
            .WithDescription(string.Join("\n", lines))
            .WithFooter("Ranking = Miles (week) + Loads (week) + Performance% (overall)");

        await cmd.RespondAsync(embed: eb.Build(), ephemeral: false);
    }
}
