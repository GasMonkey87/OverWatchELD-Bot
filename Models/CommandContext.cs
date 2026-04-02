using Discord.WebSocket;

namespace OverWatchELD.VtcBot.Models;

public sealed class CommandContext
{
    public SocketUserMessage Message { get; init; } = default!;
    public string Content { get; init; } = "";
    public string Cmd { get; init; } = "";
    public string Arg { get; init; } = "";
    public string Arg0 { get; init; } = "";
    public string Arg1 { get; init; } = "";
    public SocketGuild? Guild { get; init; }
    public string GuildIdStr { get; init; } = "";
}
