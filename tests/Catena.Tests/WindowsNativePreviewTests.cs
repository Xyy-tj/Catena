using System.Runtime.InteropServices;
using Catena.Contracts;
using Catena.Platform.Windows;
using Catena.Storage.Local;
using Catena.App;

namespace Catena.Tests;

public sealed class WindowsNativePreviewTests
{
    [Fact] public async Task MeasureUserSelectedLocalPresentation()
    {
        var path = Environment.GetEnvironmentVariable("CATENA_PREVIEW_PERF_FILE");
        if (path is null) { Assert.Skip("Opt-in read-only performance sample."); return; }
        Assert.Equal(0u, (uint)File.GetAttributes(path) & 0x441000u);
        var timings = new List<string>();
        var round = 0;
        await PreviewApartment.RunAsync(() =>
        {
            var host = CreateWindowEx(0, "STATIC", "Preview benchmark", 0x82000000, 0, 0, 700, 700, 0, 0, 0, 0);
            using var preview = new WindowsDocumentPreview { Timing = (stage, time) => timings.Add($"round {round} {stage}: {time.TotalMilliseconds:F1} ms") };
            try { for (round = 1; round <= 2; round++) preview.Load(path, host); }
            finally { preview.Dispose(); DestroyWindow(host); }
        }).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var folder = Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS");
        if (folder is not null) await File.WriteAllLinesAsync(Path.Combine(folder, "ppt-large-timing.txt"), timings, TestContext.Current.CancellationToken);
    }
    [Theory]
    [InlineData("sample.docx")]
    [InlineData("sample.pptx")]
    public async Task RegisteredHandlerCreatesPreviewChildWindow(string name)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows-only integration test."); return; }
        var root = Environment.GetEnvironmentVariable("CATENA_PREVIEW_TEST_FIXTURES");
        if (root is null) Assert.Skip("设置本地预览测试样本目录后运行。");
        if (WindowsDocumentPreview.FindHandler(Path.GetExtension(name)) is null) Assert.Skip("本机没有该格式的预览处理程序。");
        var path = Path.Combine(root, name); Assert.True(File.Exists(path));
        var provider = new LocalFileSystemProvider();
        var metadata = await provider.GetEntryAsync(provider.Normalize(path), TestContext.Current.CancellationToken);
        var content = await new WindowsDocumentPreviewProvider(new PowerPointPreviewProvider(new LocalFilePreviewProvider())).ReadAsync(metadata, TestContext.Current.CancellationToken);
        Assert.Equal(PreviewKind.Native, content.Kind); Assert.Equal(path, content.NativePath);
        nint window = 0; WindowsDocumentPreview? preview = null;
        var timings = new List<string>();
        try
        {
            await PreviewApartment.RunAsync(() =>
            {
                if (!OperatingSystem.IsWindows()) return;
                window = CreateWindowEx(0, "STATIC", "Catena preview integration test", 0x82000000, 0, 0, 700, 700, 0, 0, 0, 0);
                Assert.NotEqual(0, window);
                preview = new() { Timing = (stage, elapsed) => timings.Add($"{name} {stage}: {elapsed.TotalMilliseconds:F1} ms") }; preview.Load(path, window);
            }).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            await PreviewApartment.RunAsync(() =>
            {
                if (!OperatingSystem.IsWindows()) return;
                preview!.Resize(window);
                Assert.NotEqual(0, FindWindowEx(window, 0, null, null));
                SetWindowPos(window, 0, 0, 0, 360, 240, 0x0006);
                preview.Resize(window);
                // Office handlers own their internal padding/DPI scaling. Verify that the document
                // viewport follows the resize and fits inside the host; don't require edge-to-edge fill.
                GetWindowRect(window, out var viewport);
                var children = new List<(string Name, int Width, int Height)>();
                for (var childWindow = FindWindowEx(window, 0, null, null); childWindow != 0; childWindow = FindWindowEx(window, childWindow, null, null))
                {
                    GetWindowRect(childWindow, out var child);
                    var className = new System.Text.StringBuilder(256); GetClassName(childWindow, className, className.Capacity);
                    children.Add((className.ToString(), child.Right - child.Left, child.Bottom - child.Top));
                }
                var width = viewport.Right - viewport.Left; var height = viewport.Bottom - viewport.Top;
                Assert.True(children.Any(child => child.Width >= width / 2 && child.Width <= width + 4 && child.Height >= height / 2 && child.Height <= height + 4), string.Join("; ", children));
                preview.Clear();
                preview.Load(path, window);
                Assert.Contains(timings, line => line.Contains(" reuse:"));
                Assert.Equal(1, timings.Count(line => line.Contains(" activate:")));
                preview.Load(path, window);
                Assert.Contains(timings, line => line.Contains(" cache-hit:"));
            });
        }
        finally
        {
            await PreviewApartment.RunAsync(() =>
            { if (OperatingSystem.IsWindows()) preview?.Dispose(); if (window != 0) DestroyWindow(window); });
            var artifacts = Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS");
            if (artifacts is not null) await File.WriteAllLinesAsync(Path.Combine(artifacts, "timing-" + name + ".txt"), timings, TestContext.Current.CancellationToken);
        }
    }
    [Theory]
    [InlineData("sample.pdf")]
    [InlineData("sample.pptx")]
    public async Task PagedPreviewRendersWithoutExternalOfficeOrPdfApplications(string name)
    {
        var root = Environment.GetEnvironmentVariable("CATENA_PREVIEW_TEST_FIXTURES") ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "Preview");
        var files = new LocalFileSystemProvider(); var entry = await files.GetEntryAsync(files.Normalize(Path.Combine(root, name)), TestContext.Current.CancellationToken);
        var renderer = new PowerPointPreviewProvider(new WindowsDocumentPreviewProvider(new LocalFilePreviewProvider()));
        var first = await renderer.ReadPageAsync(entry, 0, TestContext.Current.CancellationToken);
        Assert.True(first.Kind == PreviewKind.Image, first.Notice); Assert.Equal(2, first.PageCount);
        var second = await renderer.ReadPageAsync(entry, 1, TestContext.Current.CancellationToken);
        Assert.Equal(PreviewKind.Image, second.Kind); Assert.Equal(1, second.PageIndex); Assert.NotEqual(first.ImageBytes, second.ImageBytes);
        var artifacts = Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS");
        if (artifacts is not null) await File.WriteAllBytesAsync(Path.Combine(artifacts, "preview-" + name + ".png"), first.ImageBytes!, TestContext.Current.CancellationToken);
    }
    [Theory]
    [InlineData(".docx")]
    [InlineData(".pptx")]
    [InlineData(".ppt")]
    public async Task OfflineDocumentDoesNotEnterNativeRenderer(string extension)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TestDirectory(); var path = Path.Combine(temp.Path, "online" + extension); File.WriteAllText(path, "fixture");
        File.SetAttributes(path, FileAttributes.Offline);
        try
        {
            var files = new LocalFileSystemProvider(); var entry = await files.GetEntryAsync(files.Normalize(path), TestContext.Current.CancellationToken);
            var result = await new WindowsDocumentPreviewProvider(new PowerPointPreviewProvider(new LocalFilePreviewProvider())).ReadAsync(entry, TestContext.Current.CancellationToken);
            Assert.Equal(PreviewKind.Information, result.Kind); Assert.Empty(result.NativePath); Assert.Contains("云端", result.Notice);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out WindowsDocumentPreview.NativeRect bounds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder name, int count);
}
