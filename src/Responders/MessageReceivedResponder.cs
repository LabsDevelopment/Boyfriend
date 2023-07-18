using JetBrains.Annotations;
using Remora.Discord.API.Abstractions.Gateway.Events;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.Gateway.Responders;
using Remora.Rest.Core;
using Remora.Results;

namespace Boyfriend.Responders;

/// <summary>
///     Handles sending replies to easter egg messages.
/// </summary>
[UsedImplicitly]
public class MessageCreateResponder : IResponder<IMessageCreate> {
    private readonly IDiscordRestChannelAPI _channelApi;

    public MessageCreateResponder(IDiscordRestChannelAPI channelApi) {
        _channelApi = channelApi;
    }

    public Task<Result> RespondAsync(IMessageCreate gatewayEvent, CancellationToken ct = default) {
        _ = _channelApi.CreateMessageAsync(
            gatewayEvent.ChannelID, ct: ct, content: gatewayEvent.Content.ToLowerInvariant() switch {
                "whoami"  => "`nobody`",
                "сука !!" => "`root`",
                "воооо"   => "`removing /...`",
                "пон" => "https://cdn.upload.systems/uploads/2LNfUSwM.jpg",
                "++++" => "#",
                "осу"  => "https://github.com/ppy/osu",
                _      => default(Optional<string>)
            });
        return Task.FromResult(Result.FromSuccess());
    }
}