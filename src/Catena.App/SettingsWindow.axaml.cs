using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Catena.App;
public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { if (DataContext is SettingsViewModel model) { model.CancelTestCommand.Execute(null); model.ResetDraft(); } };
    }
    private void CloseSettings(object? sender, RoutedEventArgs e) => Close();
}
