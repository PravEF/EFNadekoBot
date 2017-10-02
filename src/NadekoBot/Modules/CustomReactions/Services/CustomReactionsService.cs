﻿using Discord;
using Discord.WebSocket;
using NadekoBot.Services.Database.Models;
using NLog;
using System.Collections.Concurrent;
using System.Linq;
using System;
using System.Threading.Tasks;
using NadekoBot.Common;
using NadekoBot.Common.ModuleBehaviors;
using NadekoBot.Extensions;
using NadekoBot.Services.Database;
using NadekoBot.Services;
using NadekoBot.Modules.CustomReactions.Extensions;
using NadekoBot.Modules.Permissions.Common;
using NadekoBot.Modules.Permissions.Services;
using NadekoBot.Services.Impl;
using Newtonsoft.Json;

namespace NadekoBot.Modules.CustomReactions.Services
{
    public class CustomReactionsService : IEarlyBlockingExecutor, INService, IAliasableCustomCommandExecutor
    {
        public CustomReaction[] GlobalReactions = new CustomReaction[] { };
        public ConcurrentDictionary<ulong, CustomReaction[]> GuildReactions { get; } = new ConcurrentDictionary<ulong, CustomReaction[]>();

        public ConcurrentDictionary<string, uint> ReactionStats { get; } = new ConcurrentDictionary<string, uint>();

        private readonly Logger _log;
        private readonly DbService _db;
        private readonly DiscordSocketClient _client;
        private readonly PermissionService _perms;
        private readonly CommandHandler _cmd;
        private readonly IBotConfigProvider _bc;
        private readonly NadekoStrings _strings;
        private readonly IDataCache _cache;

        public CustomReactionsService(PermissionService perms, DbService db, NadekoStrings strings,
            DiscordSocketClient client, CommandHandler cmd, IBotConfigProvider bc, IUnitOfWork uow,
            IDataCache cache)
        {
            _log = LogManager.GetCurrentClassLogger();
            _db = db;
            _client = client;
            _perms = perms;
            _cmd = cmd;
            _bc = bc;
            _strings = strings;
            _cache = cache;

            var sub = _cache.Redis.GetSubscriber();
            sub.Subscribe(_client.CurrentUser.Id + "_gcr.added", (ch, msg) =>
            {
                Array.Resize(ref GlobalReactions, GlobalReactions.Length + 1);
                GlobalReactions[GlobalReactions.Length - 1] = JsonConvert.DeserializeObject<CustomReaction>(msg);
            }, StackExchange.Redis.CommandFlags.FireAndForget);
            sub.Subscribe(_client.CurrentUser.Id + "_gcr.deleted", (ch, msg) =>
            {
                var id = int.Parse(msg);
                GlobalReactions = GlobalReactions.Where(cr => cr?.Id != id).ToArray();
            }, StackExchange.Redis.CommandFlags.FireAndForget);
            sub.Subscribe(_client.CurrentUser.Id + "_gcr.edited", (ch, msg) =>
            {
                var obj = new { Id = 0, Message = "" };
                obj = JsonConvert.DeserializeAnonymousType(msg, obj);
                var gcr = GlobalReactions.FirstOrDefault(x => x.Id == obj.Id);
                if (gcr != null)
                    gcr.Response = obj.Message;
            }, StackExchange.Redis.CommandFlags.FireAndForget);
            sub.Subscribe(_client.CurrentUser.Id + "_crad.toggle", (ch, msg) =>
            {
                var obj = new { Id = 0, Value = false };
                obj = JsonConvert.DeserializeAnonymousType(msg, obj);
                var gcr = GlobalReactions.FirstOrDefault(x => x.Id == obj.Id);
                if (gcr != null)
                    gcr.AutoDeleteTrigger = obj.Value;
            }, StackExchange.Redis.CommandFlags.FireAndForget);
            sub.Subscribe(_client.CurrentUser.Id + "_crdm.toggle", (ch, msg) =>
            {
                var obj = new { Id = 0, Value = false };
                obj = JsonConvert.DeserializeAnonymousType(msg, obj);
                var gcr = GlobalReactions.FirstOrDefault(x => x.Id == obj.Id);
                if(gcr != null)
                    gcr.DmResponse = obj.Value;
            }, StackExchange.Redis.CommandFlags.FireAndForget);
            sub.Subscribe(_client.CurrentUser.Id + "_crca.toggle", (ch, msg) =>
            {
                var obj = new { Id = 0, Value = false };
                obj = JsonConvert.DeserializeAnonymousType(msg, obj);
                var gcr = GlobalReactions.FirstOrDefault(x => x.Id == obj.Id);
                if (gcr != null)
                    gcr.ContainsAnywhere = obj.Value;
            }, StackExchange.Redis.CommandFlags.FireAndForget);


            var items = uow.CustomReactions.GetAll();

            GuildReactions = new ConcurrentDictionary<ulong, CustomReaction[]>(items.Where(g => g.GuildId != null && g.GuildId != 0).GroupBy(k => k.GuildId.Value).ToDictionary(g => g.Key, g => g.ToArray()));
            GlobalReactions = items.Where(g => g.GuildId == null || g.GuildId == 0).ToArray();
        }

        public Task EditGcr(int id, string message)
        {
            var sub = _cache.Redis.GetSubscriber();

            return sub.PublishAsync(_client.CurrentUser.Id + "_gcr.edited", JsonConvert.SerializeObject(new
            {
                Id = id,
                Message = message,
            }));
        }

        public Task AddGcr(CustomReaction cr)
        {
            var sub = _cache.Redis.GetSubscriber();
            return sub.PublishAsync(_client.CurrentUser.Id + "_gcr.added", JsonConvert.SerializeObject(cr));
        }

        public Task DelGcr(int id)
        {
            var sub = _cache.Redis.GetSubscriber();
            return sub.PublishAsync(_client.CurrentUser.Id + "_gcr.deleted", id);
        }

        public void ClearStats() => ReactionStats.Clear();

        public CustomReaction[] TryGetCustomReaction(IUserMessage umsg, String newContent)
        {
            var channel = umsg.Channel as SocketTextChannel;
            if (channel == null)
                return null;

            var content = umsg.Content.Trim().ToLowerInvariant();
            if (!content.Equals(newContent)){
                content = newContent;
            }

            if (GuildReactions.TryGetValue(channel.Guild.Id, out CustomReaction[] reactions))
                if (reactions != null && reactions.Any())
                {
                    var rs = reactions.Where(cr =>
                    {
                        if (cr == null)
                            return false;

                        var hasTarget = cr.Response.ToLowerInvariant().Contains("%target%");
                        var trigger = cr.TriggerWithContext(umsg, _client).Trim().ToLowerInvariant();
                        return ((cr.ContainsAnywhere &&
                            (content.GetWordPosition(trigger) != WordPosition.None))
                            || (hasTarget && content.StartsWith(trigger + " "))
                            || (_bc.BotConfig.CustomReactionsStartWith && content.StartsWith(trigger + " "))
                            || content == trigger);
                    }).ToArray();

                    if (rs.Length != 0)
                    {
                        return rs;
                    }
                }

            var grs = GlobalReactions.Where(cr =>
            {
                if (cr == null)
                    return false;
                var hasTarget = cr.Response.ToLowerInvariant().Contains("%target%");
                var trigger = cr.TriggerWithContext(umsg, _client).Trim().ToLowerInvariant();
                return ((cr.ContainsAnywhere &&
                            (content.GetWordPosition(trigger) != WordPosition.None))
                        || (hasTarget && content.StartsWith(trigger + " "))
                        || (_bc.BotConfig.CustomReactionsStartWith && content.StartsWith(trigger + " "))
                        || content == trigger);
            }).ToArray();
            if (grs.Length == 0)
                return null;

            return grs;
        }

        public async Task<bool> TryExecuteEarly(DiscordSocketClient client, IGuild guild, IUserMessage msg)
        {
            return await TryExecuteEarly(client, guild, msg, msg.Content);
        }

        public async Task<bool> TryExecuteEarly(DiscordSocketClient client, IGuild guild, IUserMessage msg, string newContent)
        {
            // maybe this message is a custom reaction
            var crs = await Task.Run(() => TryGetCustomReaction(msg, newContent)).ConfigureAwait(false);
            string messageContent = msg.Content;
            if (crs != null)
            {
                foreach (var cr in crs)
                {
                    try
                    {
                        if (guild is SocketGuild sg)
                        {
                            var pc = _perms.GetCache(guild.Id);
                            if (!pc.Permissions.CheckPermissions(msg, cr.Trigger, "ActualCustomReactions",
                                out int index))
                            {
                                if (pc.Verbose)
                                {
                                    var returnMsg = _strings.GetText("trigger", guild.Id, "Permissions".ToLowerInvariant(), index + 1, Format.Bold(pc.Permissions[index].GetCommand(_cmd.GetPrefix(guild), (SocketGuild)guild)));
                                    try { await msg.Channel.SendErrorAsync(returnMsg).ConfigureAwait(false); } catch { }
                                    _log.Info(returnMsg);
                                }
                                return true;
                            }
                        }
                        await cr.Send(msg, _client, this).ConfigureAwait(false);

                        if (cr.AutoDeleteTrigger)
                        {
                            try { await msg.DeleteAsync().ConfigureAwait(false); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Sending CREmbed failed");
                        _log.Warn(ex);
                    }
                }
                return true;
            }
            return false;
        }

        public Task SetCrDmAsync(int id, bool setValue)
        {
            using (var uow = _db.UnitOfWork)
            {
                uow.CustomReactions.Get(id).DmResponse = setValue;
                uow.Complete();
            }

            var sub = _cache.Redis.GetSubscriber();
            var data = new { Id = id, Value = setValue };
            return sub.PublishAsync(_client.CurrentUser.Id + "_crdm.toggle", JsonConvert.SerializeObject(data));
        }

        public Task SetCrAdAsync(int id, bool setValue)
        {
            using (var uow = _db.UnitOfWork)
            {
                uow.CustomReactions.Get(id).AutoDeleteTrigger = setValue;
                uow.Complete();
            }

            var sub = _cache.Redis.GetSubscriber();
            var data = new { Id = id, Value = setValue };
            return sub.PublishAsync(_client.CurrentUser.Id + "_crad.toggle", JsonConvert.SerializeObject(data));
        }

        public Task SetCrCaAsync(int id, bool setValue)
        {
            using (var uow = _db.UnitOfWork)
            {
                uow.CustomReactions.Get(id).ContainsAnywhere = setValue;
                uow.Complete();
            }

            var sub = _cache.Redis.GetSubscriber();
            var data = new { Id = id, Value = setValue };
            return sub.PublishAsync(_client.CurrentUser.Id + "_crca.toggle", JsonConvert.SerializeObject(data));
        }
    }
}
