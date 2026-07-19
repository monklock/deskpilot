using DeskPilot.Core.Commands;

namespace DeskPilot.Application.Intents;

internal static class CommandRequestIdentity
{
    public static string Create(CommandRequest request)
    {
        var arguments = request.Arguments is null
            ? string.Empty
            : string.Join(
                '\u001f',
                request.Arguments
                    .OrderBy(static item => item.Key, StringComparer.Ordinal)
                    .Select(static item => $"{item.Key}\u001e{item.Value}"));
        return $"{request.CommandId.Value}\u001d{arguments}";
    }
}
