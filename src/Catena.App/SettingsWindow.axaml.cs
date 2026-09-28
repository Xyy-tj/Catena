using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Catena.App;
public sealed partial class SettingsWindow : Window
{
    public static readonly Uri RepositoryUri = new("https://github.com/Xyy-tj/Catena");
    public SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { if (DataContext is SettingsViewModel model) { model.CancelTestCommand.Execute(null); model.ResetDraft(); } };
    }
    private void CloseSettings(object? sender, RoutedEventArgs e) => Close();
    private async void OpenGitHub(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(RepositoryUri) && DataContext is SettingsViewModel model)
                model.Status = "无法打开浏览器。项目地址：" + RepositoryUri;
        }
        catch (Exception) { if (DataContext is SettingsViewModel model) model.Status = "无法打开浏览器。项目地址：" + RepositoryUri; }
    }
}
