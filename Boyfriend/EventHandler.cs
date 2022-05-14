﻿using Boyfriend.Commands;
using Discord;
using Discord.Commands;
using Discord.WebSocket;

namespace Boyfriend;

public class EventHandler {
    private readonly DiscordSocketClient _client = Boyfriend.Client;

    public void InitEvents() {
        _client.Ready += ReadyEvent;
        _client.MessageDeleted += MessageDeletedEvent;
        _client.MessageReceived += MessageReceivedEvent;
        _client.MessageUpdated += MessageUpdatedEvent;
        _client.UserJoined += UserJoinedEvent;
        _client.GuildScheduledEventCreated += ScheduledEventCreatedEvent;
        _client.GuildScheduledEventCancelled += ScheduledEventCancelledEvent;
        _client.GuildScheduledEventStarted += ScheduledEventStartedEvent;
        _client.GuildScheduledEventCompleted += ScheduledEventCompletedEvent;
    }

    private static async Task ReadyEvent() {
        var i = Utils.Random.Next(3);

        foreach (var guild in Boyfriend.Client.Guilds) {
            var config = Boyfriend.GetGuildConfig(guild.Id);
            var channel = guild.GetTextChannel(Convert.ToUInt64(config["BotLogChannel"]));
            Utils.SetCurrentLanguage(guild.Id);

            if (config["ReceiveStartupMessages"] != "true" || channel == null) continue;
            await channel.SendMessageAsync(string.Format(Messages.Ready, Utils.GetBeep(i)));
        }
    }

    private static async Task MessageDeletedEvent(Cacheable<IMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel) {
        var msg = message.Value;
        if (msg is null or ISystemMessage || msg.Author.IsBot) return;

        var guild = Boyfriend.FindGuild(channel.Value.Id);

        Utils.SetCurrentLanguage(guild.Id);

        var auditLogEntry = (await guild.GetAuditLogsAsync(1).FlattenAsync()).First();
        var mention = auditLogEntry.User.Mention;
        if (auditLogEntry.Action != ActionType.MessageDeleted ||
            DateTimeOffset.Now.Subtract(auditLogEntry.CreatedAt).TotalMilliseconds > 500 ||
            auditLogEntry.User.IsBot) mention = msg.Author.Mention;

        await Utils.SendFeedback(
            string.Format(Messages.CachedMessageDeleted, msg.Author.Mention, Utils.MentionChannel(channel.Id),
                Utils.WrapAsNeeded(msg.CleanContent)), guild.Id, mention);
    }

    private static async Task MessageReceivedEvent(SocketMessage messageParam) {
        if (messageParam is not SocketUserMessage message) return;

        var user = (SocketGuildUser) message.Author;
        var guild = user.Guild;
        var guildConfig = Boyfriend.GetGuildConfig(guild.Id);

        Utils.SetCurrentLanguage(guild.Id);

        if ((message.MentionedUsers.Count > 3 || message.MentionedRoles.Count > 2) &&
            !user.GuildPermissions.MentionEveryone) {
            await BanCommand.BanUser(guild, guild.CurrentUser, user, TimeSpan.FromMilliseconds(-1),
                Messages.AutobanReason);
            return;
        }

        var argPos = 0;
        var prev = "";
        var prevFailsafe = "";
        var prevs = await message.Channel.GetMessagesAsync(3).FlattenAsync();
        var prevsArray = prevs as IMessage[] ?? prevs.ToArray();

        if (prevsArray.Length >= 3) {
            prev = prevsArray[1].Content;
            prevFailsafe = prevsArray[2].Content;
        }

        if (!(message.HasStringPrefix(guildConfig["Prefix"], ref argPos) ||
              message.HasMentionPrefix(Boyfriend.Client.CurrentUser, ref argPos)) || user == guild.CurrentUser ||
            (user.IsBot && (message.Content.Contains(prev) || message.Content.Contains(prevFailsafe))))
            return;

        await CommandHandler.HandleCommand(message);
    }

    private static async Task MessageUpdatedEvent(Cacheable<IMessage, ulong> messageCached, SocketMessage messageSocket,
        ISocketMessageChannel channel) {
        var msg = messageCached.Value;

        if (msg is null or ISystemMessage || msg.CleanContent == messageSocket.CleanContent || msg.Author.IsBot) return;

        var guildId = Boyfriend.FindGuild(channel.Id).Id;

        Utils.SetCurrentLanguage(guildId);

        await Utils.SendFeedback(
            string.Format(Messages.CachedMessageEdited, Utils.MentionChannel(channel.Id),
                Utils.WrapAsNeeded(msg.CleanContent), Utils.WrapAsNeeded(messageSocket.Content)), guildId,
            msg.Author.Mention);
    }

    private static async Task UserJoinedEvent(SocketGuildUser user) {
        var guild = user.Guild;
        var config = Boyfriend.GetGuildConfig(guild.Id);

        if (config["SendWelcomeMessages"] == "true")
            await Utils.SilentSendAsync(guild.SystemChannel,
                string.Format(config["WelcomeMessage"], user.Mention, guild.Name));

        if (config["StarterRole"] != "0")
            await user.AddRoleAsync(ulong.Parse(config["StarterRole"]));
    }

    private static async Task ScheduledEventCreatedEvent(SocketGuildEvent scheduledEvent) {
        var guild = scheduledEvent.Guild;
        var eventConfig = Boyfriend.GetGuildConfig(guild.Id);
        var channel = guild.GetTextChannel(Convert.ToUInt64(eventConfig["EventCreatedChannel"]));

        if (channel != null) {
            var roleMention = "";
            var role = guild.GetRole(Convert.ToUInt64(eventConfig["EventNotifyReceiverRole"]));
            if (role != null)
                roleMention = $"{role.Mention} ";

            var location = Utils.WrapInline(scheduledEvent.Location) ?? Utils.MentionChannel(scheduledEvent.Channel.Id);

            await Utils.SilentSendAsync(channel,
                string.Format(Messages.EventCreated, "\n", roleMention, scheduledEvent.Creator.Mention,
                    Utils.WrapInline(scheduledEvent.Name), location,
                    scheduledEvent.StartTime.ToUnixTimeSeconds().ToString(), Utils.Wrap(scheduledEvent.Description)),
                true);
        }
    }

    private static async Task ScheduledEventCancelledEvent(SocketGuildEvent scheduledEvent) {
        var guild = scheduledEvent.Guild;
        var eventConfig = Boyfriend.GetGuildConfig(guild.Id);
        var channel = guild.GetTextChannel(Convert.ToUInt64(eventConfig["EventCancelledChannel"]));
        if (channel != null)
            await channel.SendMessageAsync(string.Format(Messages.EventCancelled, Utils.WrapInline(scheduledEvent.Name),
                eventConfig["FrowningFace"] == "true" ? $" {Messages.SettingsFrowningFace}" : ""));
    }

    private static async Task ScheduledEventStartedEvent(SocketGuildEvent scheduledEvent) {
        var guild = scheduledEvent.Guild;
        var eventConfig = Boyfriend.GetGuildConfig(guild.Id);
        var channel = guild.GetTextChannel(Convert.ToUInt64(eventConfig["EventStartedChannel"]));

        if (channel != null) {
            var receivers = eventConfig["EventStartedReceivers"];
            var role = guild.GetRole(Convert.ToUInt64(eventConfig["EventNotifyReceiverRole"]));
            var mentions = Boyfriend.StringBuilder;

            if (receivers.Contains("role") && role != null) mentions.Append($"{role.Mention} ");
            if (receivers.Contains("users") || receivers.Contains("interested"))
                foreach (var user in await scheduledEvent.GetUsersAsync(15))
                    mentions = mentions.Append($"{user.Mention} ");

            await channel.SendMessageAsync(string.Format(Messages.EventStarted, mentions,
                Utils.WrapInline(scheduledEvent.Name),
                Utils.WrapInline(scheduledEvent.Location) ?? Utils.MentionChannel(scheduledEvent.Channel.Id)));
            mentions.Clear();
        }
    }

    private static async Task ScheduledEventCompletedEvent(SocketGuildEvent scheduledEvent) {
        var guild = scheduledEvent.Guild;
        var eventConfig = Boyfriend.GetGuildConfig(guild.Id);
        var channel = guild.GetTextChannel(Convert.ToUInt64(eventConfig["EventCompletedChannel"]));
        if (channel != null)
            await channel.SendMessageAsync(string.Format(Messages.EventCompleted, Utils.WrapInline(scheduledEvent.Name),
                Utils.WrapInline(scheduledEvent.StartTime.Subtract(DateTimeOffset.Now).Negate().ToString())));
    }
}
