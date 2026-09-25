using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowPosition.App.Services;
using WindowPosition.Core;
using Forms = System.Windows.Forms;

namespace WindowPosition.App;

public partial class MainWindow : Window
{
    private readonly NativeWindowSystem _windows = new();
    private readonly TrackingEngine _engine;
    private readonly WindowCaptureService _capture;
    private readonly SettingsStore _store;
    private readonly ObservableCollection<RuleItem> _items = new();
    private readonly DispatcherTimer _timer;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _pauseMenu;
    private readonly System.Drawing.Icon _icon;
    private bool _paused;
    private bool _exiting;
    private bool _loading;
    private bool _captureHidden;
    private bool _balloonShown;
    private bool _aligning;
    private int _wheelDelta;
    private string? _lastEngineError;

    public MainWindow()
    {
        InitializeComponent();
        _engine = new TrackingEngine(_windows);
        _capture = new WindowCaptureService(_windows);
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.cfg");
        _store = new SettingsStore(settingsPath);
        _loading = true;
        foreach (var rule in _store.Load().Rules) _items.Add(CreateItem(rule));
        App.Log.Write("SETTINGS_LOADED", $"ruleCount={_items.Count}; warning={_store.LoadWarning ?? "none"}");
        _loading = false;
        RuleList.ItemsSource = _items;
        RefreshCount();
        if (_items.Count > 0) RuleList.SelectedIndex = 0;
        _capture.Captured += (_, snapshot) => OnCaptured(snapshot);
        _capture.Failed += (_, error) => EndCapture(error);
        _capture.Cancelled += (_, _) => EndCapture("キャプチャをキャンセルしました。");

        using var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        _icon = new System.Drawing.Icon(iconStream);
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "Window Position — 監視中", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("設定画面を開く", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("ウィンドウをキャプチャ", null, (_, _) => Dispatcher.Invoke(BeginCapture));
        _pauseMenu = new Forms.ToolStripMenuItem("一時停止", null, (_, _) => Dispatcher.Invoke(TogglePause));
        menu.Items.Add(_pauseMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        if (!File.Exists(settingsPath) && string.IsNullOrWhiteSpace(_store.LoadWarning))
            Loaded += (_, _) => Persist();
        if (!string.IsNullOrWhiteSpace(_store.LoadWarning))
            Loaded += (_, _) => { SetStatus(_store.LoadWarning); System.Windows.MessageBox.Show(this, _store.LoadWarning, "設定の読み込み", MessageBoxButton.OK, MessageBoxImage.Warning); };
        if (App.Log.LastError is { } logError)
            Loaded += (_, _) => SetStatus("診断ログを書き込めません: " + logError);
    }

    private RuleItem CreateItem(WindowRule rule) => new(rule, item =>
    {
        if (_loading) return;
        var previous = item.Rule with { Enabled = !item.Rule.Enabled };
        if (!Persist()) item.Replace(previous);
        else { _engine.ResetRule(item.Rule.Id); Tick(); }
    });

    private void Tick()
    {
        if (_capture.IsCapturing || _aligning) return;
        try
        {
            var statuses = _engine.Tick(_items.Select(x => x.Rule).ToList(), _paused);
            foreach (var status in statuses)
                _items.FirstOrDefault(x => x.Rule.Id == status.RuleId)?.SetStatus(status.Message);
            _lastEngineError = null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (_lastEngineError != ex.Message)
            {
                App.Log.Write("TRACKING_ERROR", error: ex);
                SetStatus("監視エラー: " + ex.Message);
            }
            _lastEngineError = ex.Message;
        }
    }

    private bool Persist()
    {
        var resumeTimer = _timer?.IsEnabled == true;
        _timer?.Stop();
        try { _store.Save(new AppSettings { Rules = _items.Select(x => x.Rule).ToList() }); return true; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            App.Log.Write("SETTINGS_SAVE_ERROR", error: ex);
            SetStatus("設定を保存できません: " + ex.Message);
            System.Windows.MessageBox.Show(this, "設定を保存できませんでした。\n" + ex.Message, "保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally { if (resumeTimer) _timer!.Start(); }
    }

    private void OnCapture(object sender, RoutedEventArgs e) => BeginCapture();
    private void BeginCapture()
    {
        if (_capture.IsCapturing || _aligning) return;
        _captureHidden = IsVisible;
        Hide();
        try
        {
            _capture.Start();
            if (!_capture.IsCapturing) return;
            _tray.Text = "Window Position — クリックしてキャプチャ (Escで中止)";
            _tray.ShowBalloonTip(2500, "ウィンドウをキャプチャ", "対象ウィンドウの中をクリックしてください。Escでキャンセル（20秒で解除）。", Forms.ToolTipIcon.Info);
        }
        catch (Exception ex) { EndCapture("キャプチャを開始できません: " + ex.Message); }
    }

    private void OnCaptured(WindowSnapshot snapshot)
    {
        EndCapture("キャプチャしました: " + snapshot.Title);
        var existing = _items.FirstOrDefault(x => x.Rule.MatchMode == TitleMatchMode.Exact && string.Equals(x.Rule.Title, snapshot.Title, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RuleList.SelectedItem = existing;
            FillEditor(existing.Rule with { X = snapshot.Bounds.X, Y = snapshot.Bounds.Y, Width = snapshot.Bounds.Width, Height = snapshot.Bounds.Height });
            SetStatus("登録済みのタイトルです。取得した配置を保存するには「保存して適用」を押してください。");
            return;
        }
        var rule = new WindowRule { Title = snapshot.Title, X = snapshot.Bounds.X, Y = snapshot.Bounds.Y, Width = snapshot.Bounds.Width, Height = snapshot.Bounds.Height };
        var item = CreateItem(rule);
        _items.Add(item);
        if (!Persist()) { _items.Remove(item); RefreshCount(); return; }
        RuleList.SelectedItem = item;
        RefreshCount();
        Tick();
    }

    private void EndCapture(string message)
    {
        _tray.Text = _paused ? "Window Position — 一時停止" : "Window Position — 監視中";
        if (_captureHidden || !IsVisible) ShowFromTray();
        _captureHidden = false;
        SetStatus(message);
    }

    public void ShowFromTray()
    {
        App.Log.Write("WINDOW_SHOW");
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EditorPanel is null) return;
        EditorPanel.IsEnabled = RuleList.SelectedItem is RuleItem;
        if (RuleList.SelectedItem is RuleItem item) FillEditor(item.Rule);
    }

    private void OnRuleListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scroll = FindVisualChild<ScrollViewer>(RuleList);
        if (scroll is null) return;

        // WPF normally uses the Windows setting (commonly three items). Treat
        // one standard wheel detent as one logical item, without changing the OS.
        e.Handled = true;
        _wheelDelta += e.Delta;
        const int deltaPerItem = 120;
        while (_wheelDelta >= deltaPerItem)
        {
            scroll.LineUp();
            _wheelDelta -= deltaPerItem;
        }
        while (_wheelDelta <= -deltaPerItem)
        {
            scroll.LineDown();
            _wheelDelta += deltaPerItem;
        }
    }

    private void OnRuleListMouseLeave(object sender, MouseEventArgs e) => _wheelDelta = 0;

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void FillEditor(WindowRule rule)
    {
        TitleInput.Text = rule.Title;
        MatchInput.SelectedIndex = (int)rule.MatchMode;
        ModeInput.SelectedIndex = (int)rule.Mode;
        XInput.Text = rule.X.ToString(); YInput.Text = rule.Y.ToString();
        WidthInput.Text = rule.Width.ToString(); HeightInput.Text = rule.Height.ToString();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeHelp is null) return;
        ModeHelp.Text = ModeInput.SelectedIndex == 1
            ? "登録した位置・サイズを維持します。移動やリサイズを検出すると自動で元の配置へ戻します。"
            : "新しく検出したウィンドウに一度だけ適用します。その後は自由に移動・リサイズできます。";
    }

    private void OnSaveRule(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleItem item) return;
        if (string.IsNullOrWhiteSpace(TitleInput.Text)) { SetStatus("ウィンドウタイトルを入力してください。"); TitleInput.Focus(); return; }
        if (!int.TryParse(XInput.Text, out var x) || !int.TryParse(YInput.Text, out var y) || !int.TryParse(WidthInput.Text, out var width) || !int.TryParse(HeightInput.Text, out var height) || width < 1 || height < 1 || width > 100000 || height > 100000 || x < -100000 || x > 100000 || y < -100000 || y > 100000)
        { SetStatus("位置は -100000～100000、サイズは 1～100000 の整数で入力してください。"); return; }
        var previous = item.Rule;
        item.Replace(previous with { Title = TitleInput.Text, MatchMode = (TitleMatchMode)MatchInput.SelectedIndex, Mode = (FollowMode)ModeInput.SelectedIndex, X = x, Y = y, Width = width, Height = height });
        if (!Persist()) { item.Replace(previous); return; }
        _engine.ResetRule(item.Rule.Id);
        if (!_paused && item.Rule.Enabled) SetStatus("保存しました。" + _engine.ApplyNow(item.Rule).Message);
        else SetStatus("保存しました。" + (_paused ? "一時停止中のため、自動配置は再開後に反映されます。" : "この項目はOFFです。"));
        Tick();
    }

    private void OnApplyNow(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleItem item) return;
        if (_paused || !item.Rule.Enabled) { SetStatus("一時停止を解除し、項目をONにしてから適用してください。"); return; }
        SetStatus("保存済みの配置: " + _engine.ApplyNow(item.Rule).Message);
    }

    private async void OnAlignToEdge(object sender, RoutedEventArgs e)
    {
        if (_aligning || RuleList.SelectedItem is not RuleItem item || sender is not Button button
            || !Enum.TryParse<WindowEdge>(button.Tag?.ToString(), out var edge)) return;

        var matches = _windows.EnumerateWindows().Where(w => Matches(item.Rule, w.Title)).ToList();
        if (matches.Count != 1)
        {
            SetStatus(matches.Count == 0
                ? "対象が見つかりません。端に揃えるウィンドウを起動してください。"
                : "対象が複数あるため端寄せできません。完全一致のタイトルで1つに絞ってください。");
            return;
        }
        var current = matches[0];
        if (current.IsMinimized || current.IsMaximized)
        {
            SetStatus(current.IsMinimized
                ? "最小化中です。通常表示に戻してから端寄せしてください。"
                : "最大化中はすでにモニターの端に揃っています。通常表示に戻すと端寄せできます。");
            return;
        }
        if (!_windows.TryGetEdgeBounds(current.Handle, edge, out var target, out var error))
        { SetStatus(error ?? "端寄せの位置を取得できませんでした。"); return; }

        _aligning = true;
        var resumeTimer = _timer.IsEnabled;
        _timer.Stop();
        RuleList.IsEnabled = false;
        EditorPanel.IsEnabled = false;
        CaptureButton.IsEnabled = false;
        try
        {
            if (!_windows.TrySetBounds(current.Handle, target, out error))
            { SetStatus(error ?? "端寄せできませんでした。設定は変更していません。"); return; }

            WindowSnapshot? applied = null;
            for (var attempt = 0; attempt < 30 && !_exiting; attempt++)
            {
                applied = _windows.GetWindow(current.Handle);
                if (applied is null || applied.ProcessId != current.ProcessId) break;
                if (applied.Bounds == target && !applied.IsMaximized && !applied.IsMinimized) break;
                await Task.Delay(50);
            }
            if (_exiting) return;
            if (applied is null || applied.ProcessId != current.ProcessId || applied.Bounds != target
                || applied.IsMaximized || applied.IsMinimized)
            {
                SetStatus("端寄せの反映を確認できませんでした。対象アプリのサイズ制限や権限を確認してください。設定は変更していません。");
                return;
            }

            var previous = item.Rule;
            // Manual placement works while automatic tracking is OFF or paused.
            // Preserve the saved matching/mode settings; take size from the live window.
            item.Replace(previous with { X = target.X, Y = target.Y, Width = target.Width, Height = target.Height });
            if (!Persist())
            {
                item.Replace(previous);
                _windows.TrySetBounds(current.Handle, current.Bounds, out _);
                return;
            }
            _engine.ResetRule(item.Rule.Id);
            // Only geometry changes here; leave any uncommitted title/mode edits intact.
            XInput.Text = target.X.ToString(); YInput.Text = target.Y.ToString();
            WidthInput.Text = target.Width.ToString(); HeightInput.Text = target.Height.ToString();
            var direction = edge switch { WindowEdge.Up => "上", WindowEdge.Down => "下", WindowEdge.Left => "左", _ => "右" };
            SetStatus($"サイズを変えずにモニターの{direction}端へ揃え、位置を保存しました。" + (_paused || !item.Rule.Enabled ? "自動配置のON/OFF状態は変更していません。" : ""));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        { SetStatus("端寄せできませんでした: " + ex.Message); }
        finally
        {
            _aligning = false;
            if (!_exiting)
            {
                RuleList.IsEnabled = true;
                EditorPanel.IsEnabled = RuleList.SelectedItem is RuleItem;
                CaptureButton.IsEnabled = true;
                if (resumeTimer) _timer.Start();
            }
        }
    }

    private void OnReadCurrent(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleItem item) return;
        var matches = _windows.EnumerateWindows().Where(w => !w.IsMinimized && Matches(item.Rule, w.Title)).ToList();
        if (matches.Count == 0) { SetStatus("対象が見つかりません。起動・最小化状態とタイトルを確認してください。"); return; }
        var b = matches[0].Bounds;
        XInput.Text = b.X.ToString(); YInput.Text = b.Y.ToString(); WidthInput.Text = b.Width.ToString(); HeightInput.Text = b.Height.ToString();
        SetStatus("現在の配置を取得しました。「保存して適用」で確定します。" + (matches.Count > 1 ? "複数あるため最前面の対象を使用しました。" : ""));
    }

    private static bool Matches(WindowRule rule, string title) => rule.MatchMode == TitleMatchMode.Contains ? title.Contains(rule.Title, StringComparison.OrdinalIgnoreCase) : string.Equals(title, rule.Title, StringComparison.OrdinalIgnoreCase);

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleItem item) return;
        var index = _items.IndexOf(item);
        _items.Remove(item);
        if (!Persist()) { _items.Insert(index, item); RuleList.SelectedItem = item; return; }
        _engine.ResetRule(item.Rule.Id);
        RuleList.SelectedIndex = _items.Count == 0 ? -1 : Math.Min(index, _items.Count - 1);
        RefreshCount();
        SetStatus("登録を削除しました。対象ウィンドウは閉じません。");
    }

    private void RefreshCount() { CountText.Text = $"{_items.Count} 件"; EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
    private void SetStatus(string message) => StatusText.Text = (_paused ? "Ⅱ  " : "●  ") + message;
    private void OnPause(object sender, RoutedEventArgs e) => TogglePause();
    private void TogglePause()
    {
        _paused = !_paused;
        App.Log.Write("TRACKING_STATE", _paused ? "paused" : "running");
        PauseButton.Content = _paused ? "監視を再開" : "一時停止";
        _pauseMenu.Text = _paused ? "監視を再開" : "一時停止";
        _pauseMenu.Checked = _paused;
        _tray.Text = _paused ? "Window Position — 一時停止" : "Window Position — 監視中";
        SetStatus(_paused ? "自動配置を一時停止しました。" : "監視を再開しました。");
        Tick();
    }

    private void OnHide(object sender, RoutedEventArgs e) => HideToTray();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting) { e.Cancel = true; HideToTray(); }
        base.OnClosing(e);
    }
    private void HideToTray()
    {
        App.Log.Write("WINDOW_HIDE", "Resident monitoring continues");
        Hide();
        if (_balloonShown) return;
        _balloonShown = true;
        _tray.ShowBalloonTip(2500, "Window Position は常駐中です", "トレイアイコンをダブルクリックすると設定を開きます。終了は右クリックメニューから。", Forms.ToolTipIcon.Info);
    }
    private void ExitApplication()
    {
        ((App)System.Windows.Application.Current).NoteExitReason("TrayMenuExit");
        _exiting = true;
        _timer.Stop();
        _capture.Dispose();
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _icon.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }
}

public sealed class RuleItem : INotifyPropertyChanged
{
    private readonly Action<RuleItem> _onToggle;
    private string _status = "待機中";
    public WindowRule Rule { get; private set; }
    public RuleItem(WindowRule rule, Action<RuleItem> onToggle) { Rule = rule; _onToggle = onToggle; }
    public string Title => Rule.Title;
    public string Summary => $"{(Rule.Mode == FollowMode.Continuous ? "常時追従" : "初回起動時のみ")} · {Rule.Width} × {Rule.Height} · ({Rule.X}, {Rule.Y})";
    public string Status => _status;
    public bool Enabled { get => Rule.Enabled; set { if (Rule.Enabled == value) return; Rule = Rule with { Enabled = value }; Raise(); _onToggle(this); } }
    public void Replace(WindowRule rule) { Rule = rule; Raise(nameof(Title)); Raise(nameof(Summary)); Raise(nameof(Enabled)); }
    public void SetStatus(string status) { if (_status == status) return; _status = status; Raise(nameof(Status)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
