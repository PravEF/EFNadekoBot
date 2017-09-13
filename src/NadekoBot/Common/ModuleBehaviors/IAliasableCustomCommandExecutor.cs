using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

namespace NadekoBot.Common.ModuleBehaviors
{
    /// <summary>
    /// Implemented by Aliasable Executors that want to execute content that is aliased
    /// </summary>
    public interface IAliasableCustomCommandExecutor
    {
        /// <summary>
        /// Try to execute some logic within some module's service given an alias.
        /// </summary>
        /// <returns>Whether it should block other command executions after it.</returns>
        Task<bool> TryExecuteEarly(DiscordSocketClient client, IGuild guild, IUserMessage msg, string alias);
    }
}
