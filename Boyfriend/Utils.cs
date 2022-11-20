﻿using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Boyfriend.Commands;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Humanizer;
using Humanizer.Localisation;

namespace Boyfriend;

public static class Utils {
    private static readonly Dictionary<string, string> ReflectionMessageCache = new();

    public static readonly Dictionary<string, CultureInfo> CultureInfoCache = new() {
        { "ru", new CultureInfo("ru-RU") },
        { "en", new CultureInfo("en-US") },
        { "mctaylors-ru", new CultureInfo("tt-RU") }
    };

    private static readonly Dictionary<ulong, SocketRole> MuteRoleCache = new();

    private static readonly AllowedMentions AllowRoles = new() {
        AllowedTypes = AllowedMentionTypes.Roles
    };

    public static string GetBeep(int i = -1) {
        return GetMessage($"Beep{(i < 0 ? Random.Shared.Next(3) + 1 : ++i)}");
    }

    public static SocketTextChannel? GetBotLogChannel(ulong id) {
        return Boyfriend.Client.GetGuild(id)
            .GetTextChannel(ParseMention(Boyfriend.GetGuildConfig(id)["BotLogChannel"]));
    }

    public static string? Wrap(string? original, bool limitedSpace = false) {
        if (original is null) return null;
        var maxChars = limitedSpace ? 970 : 1940;
        if (original.Length > maxChars) original = original[..maxChars];
        var style = original.Contains('\n') ? "```" : "`";
        return $"{style}{original}{(original.Equals("") ? " " : "")}{style}";
    }

    public static string MentionChannel(ulong id) {
        return $"<#{id}>";
    }

    public static ulong ParseMention(string mention) {
        return ulong.TryParse(Regex.Replace(mention, "[^0-9]", ""), out var id) ? id : 0;
    }

    public static async Task SendDirectMessage(SocketUser user, string toSend) {
        try { await user.SendMessageAsync(toSend); } catch (HttpException e) {
            if (e.DiscordCode is not DiscordErrorCode.CannotSendMessageToUser) throw;
        }
    }

    public static SocketRole? GetMuteRole(SocketGuild guild) {
        var id = ulong.Parse(Boyfriend.GetGuildConfig(guild.Id)["MuteRole"]);
        if (MuteRoleCache.TryGetValue(id, out var cachedMuteRole)) return cachedMuteRole;
        foreach (var x in guild.Roles) {
            if (x.Id != id) continue;
            MuteRoleCache.Add(id, x);
            return x;
        }

        return null;
    }

    public static void RemoveMuteRoleFromCache(ulong id) {
        if (MuteRoleCache.ContainsKey(id)) MuteRoleCache.Remove(id);
    }

    public static async Task SilentSendAsync(SocketTextChannel? channel, string text, bool allowRoles = false) {
        try {
            if (channel is null || text.Length is 0 or > 2000)
                throw new Exception($"Message length is out of range: {text.Length}");

            await channel.SendMessageAsync(text, false, null, null, allowRoles ? AllowRoles : AllowedMentions.None);
        } catch (Exception e) {
            await Boyfriend.Log(new LogMessage(LogSeverity.Error, nameof(Utils),
                "Exception while silently sending message", e));
        }
    }

    public static RequestOptions GetRequestOptions(string reason) {
        var options = RequestOptions.Default;
        options.AuditLogReason = reason;
        return options;
    }

    public static string GetMessage(string name) {
        var propertyName = name;
        name = $"{Messages.Culture}/{name}";
        if (ReflectionMessageCache.TryGetValue(name, out var cachedMessage)) return cachedMessage;

        var toReturn =
            typeof(Messages).GetProperty(propertyName, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                ?.ToString();
        if (toReturn is null) {
            Console.Error.WriteLine($@"Could not find localized property: {propertyName}");
            return name;
        }

        ReflectionMessageCache.Add(name, toReturn);
        return toReturn;
    }

    public static async Task
        SendFeedbackAsync(string feedback, ulong guildId, string mention, bool sendPublic = false) {
        var adminChannel = GetBotLogChannel(guildId);
        var systemChannel = Boyfriend.Client.GetGuild(guildId).SystemChannel;
        var toSend = $"*[{mention}: {feedback}]*";
        if (adminChannel is not null) await SilentSendAsync(adminChannel, toSend);
        if (sendPublic && systemChannel is not null) await SilentSendAsync(systemChannel, toSend);
    }

    public static string GetHumanizedTimeOffset(TimeSpan span) {
        return span.TotalSeconds > 0
            ? $" {span.Humanize(2, minUnit: TimeUnit.Second, maxUnit: TimeUnit.Month, culture: Messages.Culture)}"
            : Messages.Ever;
    }

    public static void SetCurrentLanguage(ulong guildId) {
        Messages.Culture = CultureInfoCache[Boyfriend.GetGuildConfig(guildId)["Lang"]];
    }

    public static void SafeAppendToBuilder(StringBuilder appendTo, string appendWhat, SocketTextChannel? channel) {
        if (channel is null) return;
        if (appendTo.Length + appendWhat.Length > 2000) {
            _ = SilentSendAsync(channel, appendTo.ToString());
            appendTo.Clear();
        }

        appendTo.AppendLine(appendWhat);
    }

    public static void SafeAppendToBuilder(StringBuilder appendTo, string appendWhat, SocketUserMessage message) {
        if (appendTo.Length + appendWhat.Length > 2000) {
            _ = message.ReplyAsync(appendTo.ToString(), false, null, AllowedMentions.None);
            appendTo.Clear();
        }

        appendTo.AppendLine(appendWhat);
    }

    public static async Task DelayedUnbanAsync(CommandProcessor cmd, ulong banned, string reason, TimeSpan duration) {
        await Task.Delay(duration);
        SetCurrentLanguage(cmd.Context.Guild.Id);
        await UnbanCommand.UnbanUserAsync(cmd, banned, reason);
    }

    public static async Task DelayedUnmuteAsync(CommandProcessor cmd, SocketGuildUser muted, string reason,
        TimeSpan duration) {
        await Task.Delay(duration);
        SetCurrentLanguage(cmd.Context.Guild.Id);
        await UnmuteCommand.UnmuteMemberAsync(cmd, muted, reason);
    }

    public static async Task SendEarlyEventStartNotificationAsync(SocketTextChannel? channel,
        SocketGuildEvent scheduledEvent, int minuteOffset) {
        try {
            await Task.Delay(scheduledEvent.StartTime.Subtract(DateTimeOffset.Now)
                .Subtract(TimeSpan.FromMinutes(minuteOffset)));
            var guild = scheduledEvent.Guild;
            if (guild.GetEvent(scheduledEvent.Id) is null) return;
            var eventConfig = Boyfriend.GetGuildConfig(guild.Id);
            SetCurrentLanguage(guild.Id);

            var receivers = eventConfig["EventStartedReceivers"];
            var role = guild.GetRole(ulong.Parse(eventConfig["EventNotificationRole"]));
            var mentions = Boyfriend.StringBuilder;

            if (receivers.Contains("role") && role is not null) mentions.Append($"{role.Mention} ");
            if (receivers.Contains("users") || receivers.Contains("interested"))
                mentions = (await scheduledEvent.GetUsersAsync(15)).Aggregate(mentions,
                    (current, user) => current.Append($"{user.Mention} "));
            await channel?.SendMessageAsync(string.Format(Messages.EventEarlyNotification, mentions,
                Wrap(scheduledEvent.Name), scheduledEvent.StartTime.ToUnixTimeSeconds().ToString()))!;
            mentions.Clear();
        } catch (Exception e) {
            await Boyfriend.Log(new LogMessage(LogSeverity.Error, nameof(Utils),
                "Exception while sending early event start notification", e));
        }
    }

    public static SocketTextChannel? GetEventNotificationChannel(SocketGuild guild) {
        return guild.GetTextChannel(ParseMention(Boyfriend.GetGuildConfig(guild.Id)["EventNotificationChannel"]));
    }
}
