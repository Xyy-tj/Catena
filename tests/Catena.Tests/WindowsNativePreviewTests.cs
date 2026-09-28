using System.Runtime.InteropServices;
using Catena.Contracts;
using Catena.Platform.Windows;
using Catena.Storage.Local;
using Catena.App;

namespace Catena.Tests;

public sealed class WindowsNativePreviewTests
{
    [Theory]
    [InlineData("sample.docx")]
    public async Task RegisteredHandlerCreatesPreviewChildWindow(string name)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows-only integration test."); return; }
        var root = Environment.GetEnvironmentVariable("CATENA_PREVIEW_TEST_FIXTURES");
        if (root is null) Assert.Skip("设置本地预览测试样本目录后运行。");
        if (WindowsDocumentPreview.FindHandler(Path.GetExtension(name)) is null) Assert.Skip("本机没有该格式的预览处理程序。");
        var path = Path.Combine(root, name); Assert.True(File.Exists(path));
        var provider = new LocalFileSystemProvider();
        var metadata = await provider.GetEntryAsync(provider.Normalize(path), TestContext.Current.CancellationToken);
        var content = await new WindowsDocumentPreviewProvider(new LocalFilePreviewProvider()).ReadAsync(metadata, TestContext.Current.CancellationToken);
        Assert.Equal(PreviewKind.Native, content.Kind); Assert.Equal(path, content.NativePath);
        nint window = 0; WindowsDocumentPreview? preview = null;
        try
        {
            await PreviewApartment.RunAsync(() =>
            {
                if (!OperatingSystem.IsWindows()) return;
                window = CreateWindowEx(0, "STATIC", "Catena preview integration test", 0x82000000, 0, 0, 700, 700, 0, 0, 0, 0);
                Assert.NotEqual(0, window);
                preview = new(); preview.Load(path, window);
            }).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            await PreviewApartment.RunAsync(() =>
            {
                if (!OperatingSystem.IsWindows()) return;
                preview!.Resize(window);
                Assert.NotEqual(0, FindWindowEx(window, 0, null, null));
            });
        }
        finally
        {
            await PreviewApartment.RunAsync(() =>
            { if (OperatingSystem.IsWindows()) preview?.Dispose(); if (window != 0) DestroyWindow(window); });
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
    [Fact] public async Task ShellMenuExposesActualCopyRenameDeleteAndPropertiesVerbs()
    {
        using var temp = new TestDirectory(); var path = Path.Combine(temp.Path, "menu-fixture.txt"); File.WriteAllText(path, "fixture");
        await PreviewApartment.RunAsync(() =>
        {
            var verbs = WindowsShellMenu.GetAvailableVerbs([path]);
            Assert.Contains("copy", verbs); Assert.Contains("rename", verbs); Assert.Contains("delete", verbs); Assert.Contains("properties", verbs);
        });
        Assert.True(File.Exists(path));
    }
    [Fact] public async Task OfflineDocumentDoesNotEnterNativeRenderer()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TestDirectory(); var path = Path.Combine(temp.Path, "online.docx"); File.WriteAllText(path, "fixture");
        File.SetAttributes(path, FileAttributes.Offline);
        try
        {
            var files = new LocalFileSystemProvider(); var entry = await files.GetEntryAsync(files.Normalize(path), TestContext.Current.CancellationToken);
            var result = await new WindowsDocumentPreviewProvider(new LocalFilePreviewProvider()).ReadAsync(entry, TestContext.Current.CancellationToken);
            Assert.Equal(PreviewKind.Information, result.Kind); Assert.Empty(result.NativePath); Assert.Contains("云端", result.Notice);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}

