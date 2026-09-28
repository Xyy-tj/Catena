namespace Catena.Contracts;

public sealed record AiAttachment(string Name, string Kind, string Text = "", string DataUrl = "");
public sealed record AiChatMessage(string Role, string Text, IReadOnlyList<AiAttachment>? Attachments = null);
public sealed record AiChatChunk(string Text = "", string Notice = "");
public interface IAiChatClient
{
    IAsyncEnumerable<AiChatChunk> StreamAsync(IReadOnlyList<AiChatMessage> messages, string directoryContext, AiConnection connection, CancellationToken token = default);
}
