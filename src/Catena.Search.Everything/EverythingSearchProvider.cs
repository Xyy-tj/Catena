using System.Diagnostics;
using System.Globalization;
using System.Text;
using Catena.Contracts;
using Microsoft.VisualBasic.FileIO;

namespace Catena.Search.Everything;

public sealed record EsOutput(int ExitCode, string Text);
public interface IEsProcessRunner
{
    Task<EsOutput> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token);
}

public sealed class EsProcessRunner : IEsProcessRunner
{
    public async Task<EsOutput> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        // ES console output uses the active Windows code page (even with -utf8-bom).
        // Its EFU export is Unicode-safe UTF-8, including characters outside that code page.
        var exportPath = Path.Combine(Path.GetTempPath(), "catena-search-" + Guid.NewGuid().ToString("N") + ".efu");
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 } };
        process.StartInfo.ArgumentList.Add("-export-efu");
        process.StartInfo.ArgumentList.Add(exportPath);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            process.Start();
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            try { await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token); await process.WaitForExitAsync(deadline.Token); }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                try { await Task.WhenAll(stdout, stderr); } catch (Exception ex) when (ex is OperationCanceledException or IOException or SearchAssistanceException) { }
            }
            if (process.ExitCode != 0) return new(process.ExitCode, "");
            using var exported = new StreamReader(exportPath, Encoding.UTF8);
            return new(process.ExitCode, await ReadBoundedAsync(exported, deadline.Token));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new SearchAssistanceException("Everything 响应超时。请确认客户端正在运行，且索引已经就绪。"); }
        catch (System.ComponentModel.Win32Exception)
        { throw new SearchAssistanceException("无法启动 ES 客户端，请在设置中选择有效的 es.exe。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new SearchAssistanceException("无法读取 Everything 查询结果，请检查临时目录是否可写。"); }
        finally
        {
            try { File.Delete(exportPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > 1048576) throw new SearchAssistanceException("Everything 返回数据超过限制，已停止本次查询。");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
}

public sealed class EverythingSearchProvider(ILocationProvider locations, IEsProcessRunner runner, IEverythingRuntime? runtime = null, IEverythingIpcClient? ipc = null) : IIndexedSearchProvider
{
    public async Task<IndexedSearchResponse> SearchAsync(IndexedSearchRequest request, string executablePath, string instanceName, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var query = BuildQuery(request.Plan, native: ipc is not null);
        if (runtime is not null && (ipc is not null || string.IsNullOrWhiteSpace(executablePath)) && string.IsNullOrWhiteSpace(instanceName))
            instanceName = await runtime.EnsureRunningAsync(token);
        var limit = Math.Clamp(request.Limit, 1, 1000);
        if (ipc is not null)
        {
            var scopes = request.Scopes ?? (request.Scope is { } scope ? [scope] : []);
            if (scopes.Count > 0)
                query = "<" + string.Join('|', scopes.Select(s => "\"" + locations.GetPath(s).TrimEnd('\\', '/') + "\\\"").Distinct(StringComparer.OrdinalIgnoreCase)) + "> " + query;
            var timer = Stopwatch.StartNew();
            var bytes = await ipc.QueryAsync(query, instanceName, limit + 1, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var rows = EverythingIpcClient.ParseResults(bytes, locations);
            return new(rows.Take(limit).ToArray(), rows.Count > limit, query, timer.Elapsed);
        }
        var executable = ResolveExecutable(executablePath);
        // -search consumes exactly one native argument. User/model text can never become an ES switch.
        var arguments = new List<string> { "-utf8-bom", "-efu", "-date-created", "-timeout", "1500", "-n", (limit + 1).ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(instanceName)) { arguments.Add("-instance"); arguments.Add(instanceName.Trim()); }
        if (request.Scope is not null) { arguments.Add("-path"); arguments.Add(locations.GetPath(request.Scope)); }
        arguments.Add("-search"); arguments.Add(query);
        var clock = Stopwatch.StartNew();
        var result = await runner.RunAsync(executable, arguments, token);
        token.ThrowIfCancellationRequested();
        if (result.ExitCode != 0) throw new SearchAssistanceException(result.ExitCode switch
        {
            8 => "搜索组件尚未就绪。请稍后重试，或在设置中检查搜索组件；自定义实例请核对高级设置。",
            7 => "无法查询 Everything 索引，请检查客户端是否已就绪及权限是否一致。",
            4 or 6 => "ES 客户端版本不兼容，请使用随包提供的 ES 1.1.0.38。",
            _ => $"Everything 查询失败（代码 {result.ExitCode}）。请在设置中检测连接。"
        });
        var entries = ParseEfu(result.Text, locations);
        return new(entries.Take(limit).ToArray(), entries.Count > limit, query, clock.Elapsed);
    }

    public static string ResolveExecutable(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured)) throw new SearchAssistanceException("ES 路径无效，请填写 es.exe 的完整路径。");
            return configured;
        }
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "everything", "es.exe");
        if (File.Exists(bundled)) return bundled;
        throw new SearchAssistanceException("搜索组件文件不完整，请重新运行 Catena 安装包修复。");
    }

    public static string BuildQuery(FileSearchPlan plan, bool native = false)
    {
        plan.Validate();
        // Encode Unicode scalars as PCRE literals: no quotes, spaces or search operators
        // can be reinterpreted by ES's legacy native command-line parser.
        var parts = plan.Terms.Select(term => native && term.IndexOfAny(['*', '?', '"']) < 0
            ? "\"" + term + "\""
            : "regex:" + string.Concat(term.EnumerateRunes().Select(rune =>
            "\\x{" + rune.Value.ToString("X", CultureInfo.InvariantCulture) + "}"))).ToList();
        if (plan.Extensions.Length > 0) parts.Add("ext:" + string.Join(';', plan.Extensions));
        if (plan.Kind != "any") parts.Add(plan.Kind + ":");
        if (plan.ModifiedFrom is { } from) parts.Add("dm:>=" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (plan.ModifiedBefore is { } before) parts.Add("dm:<" + before.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (plan.MinimumBytes is { } minimum) parts.Add("size:>=" + minimum.ToString(CultureInfo.InvariantCulture));
        if (plan.MaximumBytes is { } maximum) parts.Add("size:<=" + maximum.ToString(CultureInfo.InvariantCulture));
        return string.Join(' ', parts);
    }

    public static IReadOnlyList<FileEntry> ParseEfu(string text, ILocationProvider locations)
    {
        var entries = new List<FileEntry>();
        using var parser = new TextFieldParser(new StringReader(text.TrimStart('\uFEFF'))) { HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        try
        {
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields();
                if (fields is null || fields.Length != 5) throw new FormatException();
                if (fields[0] == "Filename") continue;
                var directory = uint.TryParse(fields[4], out var attributes) && (attributes & 16) != 0;
                var path = Path.TrimEndingDirectorySeparator(fields[0]);
                var location = locations.Normalize(path);
                long? size = long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && !directory ? length : null;
                DateTimeOffset? modified = long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileTime) && fileTime > 0 ? DateTimeOffset.FromFileTime(fileTime) : null;
                entries.Add(new(location, Path.GetFileName(path), directory ? EntryKind.Directory : EntryKind.File, size, modified,
                    EntryCapabilities.Open | EntryCapabilities.Reveal | (directory ? EntryCapabilities.Browse : EntryCapabilities.None)));
            }
        }
        catch (Exception ex) when (ex is MalformedLineException or FormatException or ArgumentException or FileSystemException)
        { throw new SearchAssistanceException("无法解析 Everything 结果，请使用兼容的 ES 客户端。"); }
        return entries;
    }
}
