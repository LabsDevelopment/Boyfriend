﻿using System.Text;
using Boyfriend.Commands;
using Discord;
using Discord.Commands;
using Discord.WebSocket;

namespace Boyfriend;

public sealed class CommandProcessor {
    private const string Success = ":white_check_mark: ";
    private const string MissingArgument = ":keyboard: ";
    private const string InvalidArgument = ":construction: ";
    private const string NoAccess = ":no_entry_sign: ";
    private const string CantInteract = ":vertical_traffic_light: ";

    private const string Mention = "<@855023234407333888>";

    public static readonly ICommand[] Commands = {
        new BanCommand(), new ClearCommand(), new HelpCommand(),
        new KickCommand(), new MuteCommand(), new PingCommand(),
        new SettingsCommand(), new UnbanCommand(), new UnmuteCommand()
    };

    private readonly StringBuilder _stackedPrivateFeedback = new();
    private readonly StringBuilder _stackedPublicFeedback = new();
    private readonly StringBuilder _stackedReplyMessage = new();
    private readonly List<Task> _tasks = new();

    public readonly SocketCommandContext Context;
    private bool _serverBlacklisted;

    public bool ConfigWriteScheduled = false;

    public CommandProcessor(SocketUserMessage message) {
        Context = new SocketCommandContext(Boyfriend.Client, message);
    }

    public async Task HandleCommandAsync() {
        var guild = Context.Guild;
        var config = Boyfriend.GetGuildConfig(guild.Id);
        var muteRole = Utils.GetMuteRole(guild);

        if (GetMember().Roles.Contains(muteRole)) {
            await Context.Message.ReplyAsync(Messages.UserCannotUnmuteThemselves);
            return;
        }

        var list = Context.Message.Content.Split("\n");
        var cleanList = Context.Message.CleanContent.Split("\n");
        for (var i = 0; i < list.Length; i++) {
            RunCommandOnLine(list[i], cleanList[i], config["Prefix"]);
            if (_serverBlacklisted) {
                await Context.Message.ReplyAsync(Messages.ServerBlacklisted);
                return;
            }

            if (_stackedReplyMessage.Length > 0) _ = Context.Channel.TriggerTypingAsync();
            var member = Boyfriend.Client.GetGuild(Context.Guild.Id)
                .GetUser(Context.User.Id); // Getting an up-to-date copy
            if (member == null || member.Roles.Contains(muteRole)
                               || member.TimedOutUntil.GetValueOrDefault(DateTimeOffset.UnixEpoch).ToUnixTimeSeconds() >
                               DateTimeOffset.Now.ToUnixTimeSeconds())
                break;
        }

        await Task.WhenAll(_tasks);
        _tasks.Clear();

        if (ConfigWriteScheduled) await Boyfriend.WriteGuildConfigAsync(guild.Id);

        SendFeedbacks();
    }

    private void RunCommandOnLine(string line, string cleanLine, string prefix) {
        var prefixed = line[..prefix.Length] == prefix;
        if (!prefixed && line[..Mention.Length] is not Mention) return;
        foreach (var command in Commands) {
            var lineNoMention = line.Remove(0, prefixed ? prefix.Length : Mention.Length);
            if (!command.Aliases.Contains(lineNoMention.Trim().Split()[0])) continue;
            if (Utils.IsServerBlacklisted(Context.Guild)) {
                _serverBlacklisted = true;
                return;
            }

            var args = lineNoMention.Trim().Split().Skip(1).ToArray();
            var cleanArgs = cleanLine.Split().Skip(lineNoMention.StartsWith(" ") ? 2 : 1).ToArray();
            _tasks.Add(command.RunAsync(this, args, cleanArgs));
            return;
        }
    }

    public void Reply(string response, string? customEmoji = null) {
        Utils.SafeAppendToBuilder(_stackedReplyMessage, $"{customEmoji ?? Success}{response}", Context.Message);
    }

    public void Audit(string action, bool isPublic = true) {
        var format = string.Format(Messages.FeedbackFormat, Context.User.Mention, action);
        if (isPublic) Utils.SafeAppendToBuilder(_stackedPublicFeedback, format, Context.Guild.SystemChannel);
        Utils.SafeAppendToBuilder(_stackedPrivateFeedback, format, Utils.GetAdminLogChannel(Context.Guild.Id));
        if (_tasks.Count == 0) SendFeedbacks(false);
    }

    private void SendFeedbacks(bool reply = true) {
        if (reply && _stackedReplyMessage.Length > 0)
            _ = Context.Message.ReplyAsync(_stackedReplyMessage.ToString(), false, null, AllowedMentions.None);

        var adminChannel = Utils.GetAdminLogChannel(Context.Guild.Id);
        var systemChannel = Context.Guild.SystemChannel;
        if (_stackedPrivateFeedback.Length > 0 && adminChannel != null &&
            adminChannel.Id != Context.Message.Channel.Id) {
            _ = Utils.SilentSendAsync(adminChannel, _stackedPrivateFeedback.ToString());
            _stackedPrivateFeedback.Clear();
        }

        if (_stackedPublicFeedback.Length > 0 && systemChannel != null && systemChannel.Id != adminChannel?.Id
            && systemChannel.Id != Context.Message.Channel.Id) {
            _ = Utils.SilentSendAsync(systemChannel, _stackedPublicFeedback.ToString());
            _stackedPublicFeedback.Clear();
        }
    }

    public string? GetRemaining(string[] from, int startIndex, string? argument) {
        if (startIndex >= from.Length && argument != null)
            Utils.SafeAppendToBuilder(_stackedReplyMessage,
                $"{MissingArgument}{Utils.GetMessage($"Missing{argument}")}", Context.Message);
        else
            return string.Join(" ", from, startIndex, from.Length - startIndex);
        return null;
    }

    public SocketUser? GetUser(string[] args, string[] cleanArgs, int index, string? argument) {
        if (index >= args.Length) {
            Utils.SafeAppendToBuilder(_stackedReplyMessage, $"{MissingArgument}{Messages.MissingUser}",
                Context.Message);
            return null;
        }

        var user = Utils.ParseUser(args[index]);
        if (user == null && argument != null)
            Utils.SafeAppendToBuilder(_stackedReplyMessage,
                $"{InvalidArgument}{string.Format(Messages.InvalidUser, Utils.Wrap(cleanArgs[index]))}",
                Context.Message);
        return user;
    }

    public bool HasPermission(GuildPermission permission) {
        if (!Context.Guild.CurrentUser.GuildPermissions.Has(permission)) {
            Utils.SafeAppendToBuilder(_stackedReplyMessage, $"{NoAccess}{Utils.GetMessage($"BotCannot{permission}")}",
                Context.Message);
            return false;
        }

        if (Context.Guild.GetUser(Context.User.Id).GuildPermissions.Has(permission)
            || Context.Guild.Owner.Id == Context.User.Id) return true;

        Utils.SafeAppendToBuilder(_stackedReplyMessage, $"{NoAccess}{Utils.GetMessage($"UserCannot{permission}")}",
            Context.Message);
        return false;
    }

    public SocketGuildUser? GetMember(SocketUser user, string? argument) {
        var member = Context.Guild.GetUser(user.Id);
        if (member == null && argument != null)
            Utils.SafeAppendToBuilder(_stackedReplyMessage, $":x: {Messages.UserNotInGuild}", Context.Message);
        return member;
    }

    public SocketGuildUser? GetMember(string[] args, string[] cleanArgs, int index, string? argument) {
        if (index >= args.Length) {
            Utils.SafeAppendToBuilder(_stackedReplyMessage, $"{MissingArgument}{Messages.MissingMember}",
                Context.Message);
            return null;
        }

        var member = Context.Guild.GetUser(Utils.ParseMention(args[index]));
        if (member == null && argument != null)
            Utils.SafeAppendToBuilder(_stackedReplyMessage,
                $"{InvalidArgument}{string.Format(Messages.InvalidMember, Utils.Wrap(cleanArgs[index]))}",
                Context.Message);
        return member;
    }

    private SocketGuildUser GetMember() {
        return Context.Guild.GetUser(Context.User.Id);
    }

    public ulong? GetBan(string[] args, int index) {
        if (index >= args.Length) {
            Utils.SafeAppendToBuilder(_stackedReplyMessage, $"{MissingArgument}{Messages.MissingUser}",
                Context.Message);
            return null;
        }

        var id = Utils.ParseMention(args[index]);
        if (Context.Guild.GetBanAsync(id) == null) {
            Utils.SafeAppendToBuilder(_stackedReplyMessage, Messages.UserNotBanned, Context.Message);
            return null;
        }

        return id;
    }

    public int? GetNumberRange(string[] args, int index, int min, int max, string? argument) {
        if (index >= args.Length) {
            Utils.SafeAppendToBuilder(_stackedReplyMessage,
                $"{MissingArgument}{string.Format(Messages.MissingNumber, min.ToString(), max.ToString())}",
                Context.Message);
            return null;
        }

        if (!int.TryParse(args[index], out var i)) {
            Utils.SafeAppendToBuilder(_stackedReplyMessage,
                $"{InvalidArgument}{string.Format(Utils.GetMessage($"{argument}Invalid"), min.ToString(), max.ToString(), Utils.Wrap(args[index]))}",
                Context.Message);
            return null;
        }

        if (argument == null) return i;
        if (i < min) {
            Utils.SafeAppendToBuilder(_stackedReplyMessage,
                $"{InvalidArgument}{string.Format(Utils.GetMessage($"{argument}TooSmall"), min.ToString())}",
                Context.Message);
            return null;
        }

        if (i <= max) return i;
        Utils.SafeAppendToBuilder(_stackedReplyMessage,
            $"{InvalidArgument}{string.Format(Utils.GetMessage($"{argument}TooLarge"), max.ToString())}",
            Context.Message);
        return null;
    }

    public static TimeSpan GetTimeSpan(string[] args, int index) {
        var infinity = TimeSpan.FromMilliseconds(-1);
        if (index >= args.Length)
            return infinity;
        var chars = args[index].AsSpan();
        var numberBuilder = Boyfriend.StringBuilder;
        int days = 0, hours = 0, minutes = 0, seconds = 0;
        foreach (var c in chars)
            if (char.IsDigit(c)) { numberBuilder.Append(c); } else {
                if (numberBuilder.Length == 0) return infinity;
                switch (c) {
                    case 'd' or 'D' or 'д' or 'Д':
                        days += int.Parse(numberBuilder.ToString());
                        numberBuilder.Clear();
                        break;
                    case 'h' or 'H' or 'ч' or 'Ч':
                        hours += int.Parse(numberBuilder.ToString());
                        numberBuilder.Clear();
                        break;
                    case 'm' or 'M' or 'м' or 'М':
                        minutes += int.Parse(numberBuilder.ToString());
                        numberBuilder.Clear();
                        break;
                    case 's' or 'S' or 'с' or 'С':
                        seconds += int.Parse(numberBuilder.ToString());
                        numberBuilder.Clear();
                        break;
                    default: return infinity;
                }
            }

        numberBuilder.Clear();
        return new TimeSpan(days, hours, minutes, seconds);
    }

    public bool CanInteractWith(SocketGuildUser user, string action) {
        if (Context.User.Id == user.Id) {
            Utils.SafeAppendToBuilder(_stackedReplyMessage,
                $"{CantInteract}{Utils.GetMessage($"UserCannot{action}Themselves")}", Context.Message);
            return false;
        }

        if (Context.Guild.CurrentUser.Id == user.Id) {
            Utils.SafeAppendToBuilder(_stackedReplyMessage,
                $"{CantInteract}{Utils.GetMessage($"UserCannot{action}Bot")}", Context.Message);
            return false;
        }

        if (Context.Guild.Owner.Id == user.Id) {
            Utils.SafeAppendToBuilder(_stackedReplyMessage,
                $"{CantInteract}{Utils.GetMessage($"UserCannot{action}Owner")}", Context.Message);
            return false;
        }

        if (Context.Guild.CurrentUser.Hierarchy <= user.Hierarchy) {
            Utils.SafeAppendToBuilder(_stackedReplyMessage,
                $"{CantInteract}{Utils.GetMessage($"BotCannot{action}Target")}", Context.Message);
            return false;
        }

        if (Context.Guild.Owner.Id == Context.User.Id || GetMember().Hierarchy > user.Hierarchy) return true;
        Utils.SafeAppendToBuilder(_stackedReplyMessage,
            $"{CantInteract}{Utils.GetMessage($"UserCannot{action}Target")}", Context.Message);
        return false;
    }
}
