using Avalonia.Controls;
using Avalonia.Input;

namespace Catena.App;
public sealed partial class SearchWindow : Window
{
    private PaneSearchViewModel? subscribed;
    public SearchWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            subscribed = DataContext as PaneSearchViewModel;
            if (subscribed is not null) { subscribed.Located += Close; subscribed.Disposed += Close; }
            SearchResultList.Focus();
        };
        Closed += (_, _) => { if (subscribed is not null) { subscribed.Located -= Close; subscribed.Disposed -= Close; subscribed.Suspend(); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }
    private void RefineSearch(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (Owner is MainWindow main && DataContext is PaneSearchViewModel search) { main.Activate(); main.EditSearch(search); } }
    private async void LocateResult(object? sender, TappedEventArgs e)
    { if (DataContext is PaneSearchViewModel model) await model.LocateCommand.ExecuteAsync(null); }
    private async void ResultKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && DataContext is PaneSearchViewModel model) { e.Handled = true; await model.LocateCommand.ExecuteAsync(null); } }
}
