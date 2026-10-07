using Zombies.Engine.Core.Modding;
using Zombies.Modding.Api;

namespace Zombies.Engine.Net;

/// <summary>
/// Lets a Server accept the messages Code mods registered through the modding API. Each one is a Domain command under the
/// number <see cref="ModMessageTable"/> assigned: the Server reads it with the mod's reader, runs the mod's handler, and sends
/// the client a rejection when the payload is malformed or the handler refused it. A handler that throws is refused the same
/// way, as invalid, and logged with the mod and message but never the payload, so a mod's bug cannot stop the Server.
/// </summary>
public static class CodeModMessages
{
    /// <exception cref="InvalidOperationException">A message's number is already taken by another command.</exception>
    public static void Register(CommandRegistry commands, CodeModLoadResult codeMods, TextWriter? log = null)
    {
        ArgumentNullException.ThrowIfNull(codeMods);
        Register(commands, codeMods.MessageHandlers, log);
    }

    /// <param name="log">Where a handler that throws is reported, one line each time; standard error by default.</param>
    /// <exception cref="InvalidOperationException">A message's number is already taken by another command.</exception>
    public static void Register(CommandRegistry commands, IEnumerable<ModMessageRegistration> messages, TextWriter? log = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var message in messages)
        {
            var handle = message.Handle;
            var (modId, messageId) = (message.ModId, message.Id);
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
                catch (Exception ex)
                {
                    // Trusted code on untrusted input: the mod's bug costs this message, not the Server.
                    var reason = ex.Message.ReplaceLineEndings(" ");
                    (log ?? Console.Error).WriteLine($"Zombies: code mod '{modId}' threw on message '{messageId}' from '{context.Player.Name}': {ex.GetType().Name}: {reason}");
                    return CommandResult.Invalid("The Server could not handle the message.");
                }

                return result.IsAccepted ? CommandResult.Accepted : CommandResult.Invalid(result.Reason ?? "The message was refused.");
            });
        }
    }
}
