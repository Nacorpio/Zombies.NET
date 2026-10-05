using Zombies.Engine.Core.Modding;
using Zombies.Modding.Api;

namespace Zombies.Engine.Net;

/// <summary>
/// Lets a Server accept the messages Code mods registered through the modding API. Each one is a Domain command under the
/// number <see cref="ModMessageTable"/> assigned: the Server reads it with the mod's reader, runs the mod's handler, and sends
/// the client a rejection when the payload is malformed or the handler refused it.
/// </summary>
public static class CodeModMessages
{
    /// <exception cref="InvalidOperationException">A message's number is already taken by another command.</exception>
    public static void Register(CommandRegistry commands, CodeModLoadResult codeMods)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(codeMods);
        foreach (var message in codeMods.MessageHandlers)
        {
            var handle = message.Handle;
            commands.Register(message.Number, (payload, context) =>
            {
                ModMessageResult result;
                try
                {
                    result = handle(payload, new ModMessageSender(context.Player.Name, context.Player.EntityId));
                }
                catch (MalformedModMessageException ex)
                {
                    return new CommandResult(CommandRejection.Malformed, ex.Message);
                }

                return result.IsAccepted ? CommandResult.Accepted : CommandResult.Invalid(result.Reason ?? "The message was refused.");
            });
        }
    }
}
