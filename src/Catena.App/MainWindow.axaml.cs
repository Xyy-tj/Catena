using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Catena.App;
public sealed partial class MainWindow : Window
{
    private bool canClose;
    private bool closing;
    private readonly Dictionary<Guid, PaneView> views = [];
    private Avalonia.Media.Imaging.Bitmap? skinImage;
    private MainViewModel? Model => DataContext as MainViewModel;
    public bool SmokeTest { get; init; }
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (Model is { } model)
            {
                model.LayoutChanged += RebuildLayout;
                model.PropertyChanged += ModelPropertyChanged;
                await model.InitializeAsync();
                if (model.Settings is { } settings)
                { settings.Saved += ApplyAppearance; GlobalSearchScope.SelectedIndex = settings.Current.DefaultSearchScope; ApplyAppearance(); }
                if (SmokeTest) { Console.WriteLine($"CATENA_SMOKE_READY panes={model.PaneCount} persistence={model.CanManage}"); Close(); }
            }
        };
        Closing += async (_, e) =>
        {
            if (canClose || Model is null) return;
            e.Cancel = true;
            if (Model.IsBusy) { Model.Message = "工作区正在切换或恢复，请稍后退出。"; return; }
            if (closing) return;
            closing = true;
            if (await Model.SaveSafelyAsync()) { canClose = true; Close(); }
            else if (await ConfirmAsync("工作区尚未保存。仍要退出吗？", "仍然退出")) { canClose = true; Close(); }
            closing = false;
        };
        Closed += (_, _) => { if (Model is { } model) { if (model.Settings is { } settings) settings.Saved -= ApplyAppearance; model.LayoutChanged -= RebuildLayout; model.PropertyChanged -= ModelPropertyChanged; model.Dispose(); } SkinBackground.Source = null; skinImage?.Dispose(); };
        ActualThemeVariantChanged += (_, _) => UpdateSkinSurface();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }
    private void ApplyAppearance()
    {
        if (Model?.Settings is not { } settings) return;
        var current = settings.Current;
        if (Application.Current is { } app)
            app.RequestedThemeVariant = current.Theme switch { 1 => Avalonia.Styling.ThemeVariant.Light, 2 => Avalonia.Styling.ThemeVariant.Dark, _ => Avalonia.Styling.ThemeVariant.Default };
        if (current.Skin == 1 && skinImage is null)
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Catena.App/Assets/blue-mist.png"));
            skinImage = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 1600);
        }
        SkinBackground.Source = current.Skin == 1 ? skinImage : null;
        SkinBackground.Opacity = current.BackgroundOpacity;
        if (current.Skin == 0) { skinImage?.Dispose(); skinImage = null; }
        GlobalSearchScope.SelectedIndex = current.DefaultSearchScope;
        UpdateSkinSurface();
    }
    private void UpdateSkinSurface()
    {
        if (Model?.Settings?.Current.Skin != 1) { Resources.Remove("CatenaSurface"); return; }
        Resources["CatenaSurface"] = Avalonia.Media.Brush.Parse(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "#ED192332" : "#EDFFFFFF");
    }
    private void RebuildLayout()
    {
        if (Model is not { } model) return;
        UpdatePreviewLayout();
        PaneHost.Children.Clear(); views.Clear();
        PaneHost.ColumnDefinitions.Clear(); PaneHost.RowDefinitions.Clear();
        var columns = model.PaneCount == 4 || model.PaneCount == 2 && !model.TwoPaneRows;
        var rows = model.PaneCount == 4 || model.PaneCount == 2 && model.TwoPaneRows;
        PaneHost.ColumnDefinitions.Add(new(new GridLength(columns ? model.HorizontalRatio : 1, GridUnitType.Star)) { MinWidth = 240 });
        PaneHost.RowDefinitions.Add(new(new GridLength(rows ? model.VerticalRatio : 1, GridUnitType.Star)) { MinHeight = 180 });
        if (columns)
        {
            PaneHost.ColumnDefinitions.Add(new(new GridLength(8)));
            PaneHost.ColumnDefinitions.Add(new(new GridLength(1 - model.HorizontalRatio, GridUnitType.Star)) { MinWidth = 240 });
        }
        if (rows)
        {
            PaneHost.RowDefinitions.Add(new(new GridLength(8)));
            PaneHost.RowDefinitions.Add(new(new GridLength(1 - model.VerticalRatio, GridUnitType.Star)) { MinHeight = 180 });
            var horizontal = CreateSplitter("PaneRowSplitter", GridResizeDirection.Rows);
            Grid.SetRow(horizontal, 1); Grid.SetColumnSpan(horizontal, columns ? 3 : 1); PaneHost.Children.Add(horizontal);
            horizontal.DragCompleted += (_, _) => model.VerticalRatio = PaneHost.RowDefinitions[0].ActualHeight / (PaneHost.RowDefinitions[0].ActualHeight + PaneHost.RowDefinitions[2].ActualHeight);
            horizontal.DoubleTapped += (_, e) => { model.VerticalRatio = .5; PaneHost.RowDefinitions[0].Height = GridLength.Star; PaneHost.RowDefinitions[2].Height = GridLength.Star; e.Handled = true; };
        }
        if (columns)
        {
            var vertical = CreateSplitter("PaneColumnSplitter", GridResizeDirection.Columns);
            Grid.SetColumn(vertical, 1); Grid.SetRowSpan(vertical, rows ? 3 : 1); PaneHost.Children.Add(vertical);
            vertical.DragCompleted += (_, _) => model.HorizontalRatio = PaneHost.ColumnDefinitions[0].ActualWidth / (PaneHost.ColumnDefinitions[0].ActualWidth + PaneHost.ColumnDefinitions[2].ActualWidth);
            vertical.DoubleTapped += (_, e) => { model.HorizontalRatio = .5; PaneHost.ColumnDefinitions[0].Width = GridLength.Star; PaneHost.ColumnDefinitions[2].Width = GridLength.Star; e.Handled = true; };
        }
        for (var i = 0; i < model.PaneCount; i++)
        {
            var pane = model.Panes[i]; var view = new PaneView { DataContext = pane }; views[pane.Id] = view;
            Grid.SetColumn(view, columns ? i % 2 * 2 : 0); Grid.SetRow(view, rows ? (columns ? i / 2 : i) * 2 : 0); PaneHost.Children.Add(view);
        }
    }    private static GridSplitter CreateSplitter(string name, GridResizeDirection direction)
    {
        var splitter = new GridSplitter
        {
            Name = name, ResizeDirection = direction, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            ShowsPreview = false, DragIncrement = 1,
            Cursor = new Cursor(direction == GridResizeDirection.Rows ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast)
        };
        ToolTip.SetTip(splitter, "拖动调整窗格 · 双击均分");
        return splitter;
    }
    private void ModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(MainViewModel.PreviewVisible)) UpdatePreviewLayout(); }
    private void ContentSizeChanged(object? sender, SizeChangedEventArgs e) => UpdatePreviewLayout();
    private async void SelectWorkspace(object? sender, SelectionChangedEventArgs e)
    { if (Model is { CanManage: true } model) await model.SwitchWorkspaceCommand.ExecuteAsync(null); }
    public void FocusSearch(PaneViewModel? pane = null)
    {
        if (pane is not null && Model is { } main)
        {
            if (main.Panes.IndexOf(pane) >= main.PaneCount) main.SetLayoutCommand.Execute(main.Panes.IndexOf(pane) < 2 ? "2" : "4");
            main.Activate(pane); GlobalSearchScope.SelectedIndex = 1; GlobalSearchBox.Text = main.GetSearch(pane).Query;
        }
        GlobalSearchBox.Focus(); GlobalSearchBox.SelectAll();
    }
    public void EditSearch(PaneSearchViewModel search)
    { FocusSearch(search.Pane); GlobalSearchScope.SelectedIndex = search.ScopeIndex; GlobalSearchMethod.SelectedIndex = search.LastUsedAi ? 1 : 0; GlobalSearchBox.Text = search.Query; }
    private void SearchOptionsChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GlobalSearchBox is null) return;
        GlobalSearchBox.PlaceholderText = GlobalSearchMethod?.SelectedIndex == 1 ? "描述要找的文件…" : GlobalSearchScope?.SelectedIndex switch
        { 0 => "搜索此电脑…", 1 => "搜索当前窗格文件…", _ => "搜索工作区文件…" };
    }
    private async void SubmitGlobalSearch(object? sender, RoutedEventArgs e) => await SubmitSearchAsync();
    private async void GlobalSearchKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await SubmitSearchAsync(); } }
    private async Task SubmitSearchAsync()
    {
        if (Model is not { IsBusy: false, ActivePane: { } pane } model || string.IsNullOrWhiteSpace(GlobalSearchBox.Text)) return;
        SearchOptionsButton.Flyout?.Hide();
        var search = model.GetSearch(pane);
        search.LiveInput = false;
        search.ScopeIndex = GlobalSearchScope.SelectedIndex;
        search.Query = GlobalSearchBox.Text;
        OpenSearch(pane);
        await (GlobalSearchMethod.SelectedIndex == 1 ? search.AiSearchCommand : search.SearchCommand).ExecuteAsync(null);
    }
    private void ClosePreview(object? sender, RoutedEventArgs e)
    { if (Model is { } model) model.PreviewVisible = false; }
    private void ToggleWorkspaceTools(object? sender, RoutedEventArgs e)
    { if (Model is { } model) model.WorkspaceToolsVisible = !model.WorkspaceToolsVisible; }
    private void MinimizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; MaximizeIcon.Kind = WindowState == WindowState.Maximized ? "restore" : "maximize"; }
    private static bool IsTitleSource(object? source) => source is Control control && control is not (Button or ComboBox or ToggleSwitch or TextBox) &&
        !control.GetVisualAncestors().Any(v => v is Button or ComboBox or ToggleSwitch or TextBox);
    private void TitlePointerPressed(object? sender, PointerPressedEventArgs e)
    { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && IsTitleSource(e.Source)) BeginMoveDrag(e); }
    private void TitleDoubleTapped(object? sender, TappedEventArgs e)
    { if (IsTitleSource(e.Source)) { ToggleMaximize(); e.Handled = true; } }
    private void UpdatePreviewLayout()
    {
        if (Model is not { } model) return;
        ContentGrid.ColumnDefinitions[0].MinWidth = 500;
        ContentGrid.ColumnDefinitions[1].Width = new GridLength(model.PreviewVisible ? 8 : 0);
        var column = ContentGrid.ColumnDefinitions[2];
        column.MinWidth = model.PreviewVisible ? 200 : 0;
        column.MaxWidth = Math.Max(200, (ContentGrid.Bounds.Width > 0 ? ContentGrid.Bounds.Width : Width - 24) - 508);
        column.Width = new GridLength(model.PreviewVisible ? Math.Min(model.PreviewWidth, column.MaxWidth) : 0);
    }
    private void PreviewDragCompleted(object? sender, VectorEventArgs e)
    { if (Model is { PreviewVisible: true } model) model.PreviewWidth = ContentGrid.ColumnDefinitions[2].ActualWidth; }
    private void ResetPreviewWidth(object? sender, TappedEventArgs e)
    { if (Model is { } model) { model.PreviewWidth = 380; UpdatePreviewLayout(); e.Handled = true; } }
    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model || model.IsBusy || model.ActivePane is not { } pane) return;
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.L) { views[pane.Id].FocusAddress(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.K) { FocusSearch(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.P) { model.PreviewVisible = !model.PreviewVisible; e.Handled = true; }
        else if (e.Key == Key.F6)
        {
            var index = (model.Panes.IndexOf(pane) + 1) % model.PaneCount; model.Activate(model.Panes[index]); views[model.Panes[index].Id].FocusAddress(); e.Handled = true;
        }
        else if (e.Key == Key.F5) { e.Handled = true; await pane.RefreshCommand.ExecuteAsync(null); }
        else if (e.KeyModifiers == KeyModifiers.Alt)
        {
            var command = e.Key switch { Key.Left => pane.BackCommand, Key.Right => pane.ForwardCommand, Key.Up => pane.UpCommand, Key.Home => pane.HomeCommand, _ => null };
            if (command is not null) { e.Handled = true; await command.ExecuteAsync(null); }
        }
    }
    private readonly Dictionary<Guid, SearchWindow> searchWindows = [];
    private readonly Dictionary<Guid, ChatWindow> chatWindows = [];
    public SearchWindow OpenSearch(PaneViewModel pane)
    {
        if (searchWindows.TryGetValue(pane.Id, out var existing)) { existing.Activate(); return existing; }
        var window = new SearchWindow { DataContext = Model!.GetSearch(pane) };
        searchWindows[pane.Id] = window;
        window.Closed += (_, _) => searchWindows.Remove(pane.Id);
        window.Show(this); return window;
    }
    public ChatWindow OpenChat(PaneViewModel pane)
    {
        var model = Model!.GetChat(pane);
        if (chatWindows.TryGetValue(pane.Id, out var existing)) { existing.Activate(); return existing; }
        var window = new ChatWindow { DataContext = model };
        chatWindows[pane.Id] = window;
        window.Closed += (_, _) => chatWindows.Remove(pane.Id);
        window.Show(this); return window;
    }    private async void DeleteWorkspace(object? sender, RoutedEventArgs e)
    { if (Model is { } model && await ConfirmAsync("删除当前工作区？此操作只删除布局和导航记录。", "删除工作区")) await model.DeleteCurrentAsync(); }
    private async void OpenSettings(object? sender, RoutedEventArgs e)
    {
        if (Model?.Settings is not { } settings) return;
        settings.ResetDraft(); await new SettingsWindow { DataContext = settings }.ShowDialog(this);
    }
    private async void OpenLocations(object? sender, RoutedEventArgs e)
    {
        if (Model?.Library is not { } library) return;
        library.SelectedRoot ??= library.Roots.FirstOrDefault();
        await new LocationsWindow { DataContext = library }.ShowDialog(this);
    }
    private async Task<bool> ConfirmAsync(string text, string action)
    {
        var dialog = new Window { Title = "Catena", Width = 450, Height = 165, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "取消" }; var confirm = new Button { Content = action };
        cancel.Click += (_, _) => dialog.Close(false); confirm.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 20, Children =
        { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, confirm } } } };
        return await dialog.ShowDialog<bool>(this);
    }
}


