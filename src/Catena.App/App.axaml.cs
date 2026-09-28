using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Catena.Contracts;
using Catena.Core;
using Catena.Persistence;
using Catena.Platform.Windows;
using Catena.Storage.Local;
using Catena.AI;
using Catena.Search.Everything;
using Microsoft.Extensions.DependencyInjection;

namespace Catena.App;
public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var directory = Environment.GetEnvironmentVariable("CATENA_DATA_DIR") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Catena");
            var services = new ServiceCollection()
                .AddSingleton<LocalFileSystemProvider>()
                .AddSingleton<ILocationProvider>(s => s.GetRequiredService<LocalFileSystemProvider>())
                .AddSingleton<ISearchProvider, LocalSearchProvider>()
                .AddSingleton<LocalFilePreviewProvider>()
                .AddSingleton<IFilePreviewProvider>(s => new PowerPointPreviewProvider(new WindowsDocumentPreviewProvider(s.GetRequiredService<LocalFilePreviewProvider>())))
                .AddSingleton<IEsProcessRunner, EsProcessRunner>()
                .AddSingleton<IEverythingIpcClient, EverythingIpcClient>()
                .AddSingleton<IEverythingHost, WindowsEverythingHost>()
                .AddSingleton<IEverythingRuntime, EverythingRuntime>()
                .AddSingleton<IIndexedSearchProvider, EverythingSearchProvider>()
                .AddSingleton(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan })
                .AddSingleton<IAiSearchPlanner, OpenAiSearchPlanner>()
                .AddSingleton<IAiChatClient, OpenAiChatClient>()
                .AddSingleton<ISecretProtector, WindowsSecretProtector>()
                .AddSingleton<IAppSettingsStore>(new JsonAppSettingsStore(Path.Combine(directory, "settings.json")))
                .AddSingleton<SettingsViewModel>()
                .AddSingleton<IPlatformActions, WindowsPlatformActions>()
                .AddSingleton<IWorkspaceRepository>(new SqliteWorkspaceRepository(Path.Combine(directory, "workspaces.db")))
                .AddSingleton<ILocationCatalogStore>(new SqliteLocationCatalog(Path.Combine(directory, "navigation.db")))
                .AddSingleton<LocalStructureScanner>()
                .AddSingleton<LocationLibraryViewModel>()
                .AddSingleton<MainViewModel>().BuildServiceProvider();
            desktop.MainWindow = new MainWindow
            {
                DataContext = services.GetRequiredService<MainViewModel>(),
                SmokeTest = desktop.Args?.Contains("--smoke-test") == true
            };
            desktop.Exit += (_, _) => services.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

