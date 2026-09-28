using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Catena.Contracts;

namespace Catena.Storage.Local;

public sealed class LocalSearchProvider(LocalFileSystemProvider provider) : ISearchProvider
{
    public string Name => "当前目录及子目录";
    public async IAsyncEnumerable<SearchBatch> SearchAsync(SearchQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Text) || query.Limit < 1) yield break;
        var terms = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        var channel = Channel.CreateBounded<SearchBatch>(2);
        var producer = Task.Run(async () =>
        {
            try
            {
                var pending = new Stack<string>(); pending.Push(provider.GetPath(query.Scope));
                var buffer = new List<FileEntry>(); var skipped = 0; var count = 0;
                while (pending.TryPop(out var directory))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                // Never descend into reparse points, including junctions and symlinks.
                                if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory)
                                    pending.Push(info.FullName);
                                if (!terms.All(term => info.Name.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;
                                buffer.Add(provider.Describe(info)); count++;
                                if (buffer.Count >= 32 || count >= query.Limit)
                                {
                                    await channel.Writer.WriteAsync(new(buffer.ToArray(), skipped, count >= query.Limit), token);
                                    buffer.Clear();
                                }
                                if (count >= query.Limit) { channel.Writer.TryComplete(); return; }
                            }
                            catch (Exception ex) when (LocalFileSystemProvider.IsIoError(ex)) { skipped++; }
                        }
                    }
                    catch (Exception ex) when (LocalFileSystemProvider.IsIoError(ex))
                    {
                        if (directory == provider.GetPath(query.Scope)) throw LocalFileSystemProvider.Translate(ex);
                        skipped++;
                    }
                }
                await channel.Writer.WriteAsync(new(buffer.ToArray(), skipped, false), token);
                channel.Writer.TryComplete();
            }
            catch (Exception ex) { channel.Writer.TryComplete(ex); }
        }, CancellationToken.None);
        try { await foreach (var batch in channel.Reader.ReadAllAsync(token)) yield return batch; }
        finally { linked.Cancel(); await producer; }
    }
}
