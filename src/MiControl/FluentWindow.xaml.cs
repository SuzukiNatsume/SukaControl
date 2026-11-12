using System.Diagnostics;
using Microsoft.Win32;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using S = System.Windows.Shapes;

namespace SukaControl;

internal partial class FluentWindow : W.Window
{
    readonly Firmware firmware = new();
    readonly Lighting lighting = new();
    readonly Telemetry telemetry = new();
    readonly DisplayControl displayControl = new();
    readonly Preferences preferences = Preferences.Load();
    readonly LightingSession lightingSession;
    readonly SoftwareLighting softwareLighting;
    readonly SemaphoreSlim lightingUiGate = new(1);
    SoftwareLightingPreset? activeSoftwareLighting;
    bool keyboardPreviewChanged;
    Func<SoftwareLightingPreset?>? readSoftwareLighting;
    readonly List<C.Button> lightApplyButtons = new();
    readonly List<C.ComboBox> lightEffectChoices = new();
    readonly List<Action<LightingReadback>> lightingBindings = new();
    readonly List<Action> clearLightingDrafts = new();
    readonly List<Func<Dictionary<string, bool>>> lightingControlStates = new();
    C.Button? saveLightingButton;
    bool closingPreview;
    readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly System.Windows.Threading.DispatcherTimer keyTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly System.Windows.Threading.DispatcherTimer warmthTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly System.Windows.Threading.DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly List<W.UIElement> displayControls = new();
    readonly TaskCompletionSource hardwareReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task HardwareReady => hardwareReady.Task;
    readonly NotifyIcon tray;
    readonly Dictionary<string, C.TextBlock> readings = new();
    readonly Dictionary<string, Trend> trends = new();
    readonly Dictionary<string, C.StackPanel> pages = new();
    readonly List<C.CheckBox> lightSwitches = new();
    readonly C.TextBlock firmwareText = new(), lightingText = new(), profileText = new(), modeText = new(), graphicsText = new();
    readonly C.ComboBox displays = new(), profileChoice = new(), modeChoice = new(), gfxChoice = new();
    C.CheckBox? eyeSwitch, overdriveSwitch, fnSwitch, winSwitch;
    C.CheckBox? restorePerformanceSwitch, acFullSpeedSwitch;
    bool policySaving;
    C.Slider? keyboardBrightness;
    C.ComboBox? keyboardAudioSource;
    Action<Color>? setKeyboardColor;
    bool keyPolling;
    bool polling, exiting, updating;
    bool performanceSelectionDirty, graphicsSelectionDirty;
    bool displaysInitialized;
    bool initialized;
    int ticks;
    DisplayTarget Display => displays.SelectedItem as DisplayTarget ?? throw new IOException("No display available.");
    public FluentWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyPalette();
        ApplyPalette(); SystemEvents.UserPreferenceChanged += ThemeChanged;
        softwareLighting = new(lighting);
        lightingSession = new(lighting, () => softwareLighting.HardwareBaseline);
        softwareLighting.Failed += ex => Dispatcher.BeginInvoke(async () => { try { await RevertLighting(); } catch (Exception cleanup) { Store.Log(cleanup.ToString()); } Status(L.Error(ex), true); });
        string[] ids = ["monitor", "lighting", "display", "shortcuts", "settings"], icons = ["\uE9D9", "\uE781", "\uE7F4", "\uE765", "\uE713"];
        for (int i = 0; i < ids.Length; i++)
        {
            var row = new C.StackPanel { Orientation = C.Orientation.Horizontal };
            row.Children.Add(new C.TextBlock { Text = icons[i], FontFamily = new("Segoe Fluent Icons"), FontSize = 18, Margin = new(0, 0, 14, 0), VerticalAlignment = W.VerticalAlignment.Center }); row.Children.Add(Text(L.T(ids[i])));
            Navigation.Items.Add(new C.ListBoxItem { Content = row, Tag = ids[i], Style = (W.Style)Resources["NavigationItem"] });
        }
        BuildMonitor(); BuildLighting(); BuildDisplay(); BuildShortcuts(); BuildSettings();
        Navigation.SelectionChanged += (_, _) => { if (Navigation.SelectedItem is C.ListBoxItem item) ShowPage((string)item.Tag); }; Navigation.SelectedIndex = 0;
        var menu = new C.ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, FontFamily = FontFamily };
        var openItem = new C.MenuItem { Header = L.T("showApp") }; openItem.Click += (_, _) => Restore();
        var exitItem = new C.MenuItem { Header = L.T("exit") }; exitItem.Click += async (_, _) => await Exit();
        menu.Items.Add(openItem); menu.Items.Add(exitItem);
        menu.Opened += (_, _) => { if (W.PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource source) SetForegroundWindow(source.Handle); };
        tray = new() { Icon = LoadTrayIcon(), Text = "SukaControl", Visible = true }; tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(Restore);
        tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) Dispatcher.BeginInvoke(() => menu.IsOpen = true); };
        SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        displayTimer.Tick += (_, _) => RefreshDisplays();
        Loaded += async (_, _) => await InitializeOnce();
        timer.Tick += async (_, _) => await Poll();
        keyTimer.Tick += async (_, _) => await PollKeys();
        warmthTimer.Tick += async (_, _) => { warmthTimer.Stop(); preferences.Save(); if (eyeSwitch?.IsChecked == true) await Execute(() => { displayControl.Eye(Display, true, preferences.Warmth); return Task.CompletedTask; }); };
        Closing += async (_, e) => { if (!exiting) { e.Cancel = true; await CloseToTray(); } };
        Closed += (_, _) => { timer.Stop(); keyTimer.Stop(); warmthTimer.Stop(); displayTimer.Stop(); menu.IsOpen = false; var icon = tray.Icon; tray.Dispose(); icon?.Dispose(); displayControl.Dispose(); firmware.Dispose(); SystemEvents.UserPreferenceChanged -= ThemeChanged; SystemEvents.DisplaySettingsChanged -= DisplaysChanged; };
        Activated += async (_, _) => { if (hardwareReady.Task.IsCompleted) await UpdateModes(); };
    }
    async Task InitializeOnce()
    {
        if (initialized) return;
        initialized = true;
        ApplyPalette(); displayTimer.Start(); await InitializeHardware(); timer.Start(); keyTimer.Start();
    }
    internal void StartInTray()
    {
        ShowActivated = false;
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        Dispatcher.BeginInvoke(async () => await InitializeOnce());
    }
    void ThemeChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(ApplyPalette);
    void DisplaysChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (!exiting) RefreshDisplays(); });
    }
    void RefreshDisplays()
    {
        if (exiting) return;
        try
        {
            var connected = DisplayControl.Displays();
            if (displaysInitialized && displays.Items.Cast<DisplayTarget>().SequenceEqual(connected)) return;
            var previous = displays.SelectedItem as DisplayTarget;
            var selected = connected.FirstOrDefault(d => previous != null && d.DeviceId.Equals(previous.DeviceId, StringComparison.OrdinalIgnoreCase)) ?? connected.FirstOrDefault();
            displayControl.ReconcileDisplays(connected);
            warmthTimer.Stop();
            displays.ItemsSource = connected;
            displays.SelectedItem = selected;
            displaysInitialized = true;
            displays.IsEnabled = connected.Count > 0;
            foreach (var control in displayControls) control.IsEnabled = selected != null;
            if (eyeSwitch != null && (selected == null || selected.DeviceId != previous?.DeviceId)) eyeSwitch.IsChecked = false;
            UpdateProfile();
        }
        catch (Exception ex) { Store.Log("Display refresh: " + ex.Message); }
    }
    static System.Drawing.Icon LoadTrayIcon()
    {
        using var theme = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool lightTaskbar = theme?.GetValue("SystemUsesLightTheme") is int n && n != 0;
        using var stream = W.Application.GetResourceStream(new Uri("/Assets/Tray" + (lightTaskbar ? "Light" : "Dark") + ".ico", UriKind.Relative)).Stream;
        using var icon = new System.Drawing.Icon(stream, SystemInformation.SmallIconSize);
        return (System.Drawing.Icon)icon.Clone();
    }
    void ApplyPalette()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); bool light = key?.GetValue("AppsUseLightTheme") is int n && n != 0;
        M.SolidColorBrush Brush(string color) => new((M.Color)M.ColorConverter.ConvertFromString(color));
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            int darkMode = light ? 0 : 1, backdrop = 2, defaultCaption = -1;
            DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
            DwmSetWindowAttribute(handle, 35, ref defaultCaption, sizeof(int));
            DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)); // Windows Settings uses the Mica backdrop.
        }
        Resources["MainText"] = Brush(light ? "#1A1A1A" : "#F5F5F5"); Resources["MutedText"] = Brush(light ? "#646464" : "#AEAEAE"); Resources["CardSurface"] = Brush(light ? "#CCFFFFFF" : "#C92C2C2C"); Resources["CardStroke"] = Brush(light ? "#0F000000" : "#19000000"); Resources["HoverSurface"] = Brush(light ? "#08000000" : "#0FFFFFFF"); Resources["SelectedSurface"] = Brush(light ? "#0D000000" : "#12FFFFFF"); Resources["Accent"] = W.SystemColors.AccentColorBrush;
        if (tray != null) { var previous = tray.Icon; tray.Icon = LoadTrayIcon(); previous?.Dispose(); }
    }
    C.TextBlock Text(string value, double size = 14, bool muted = false)
    {
        var text = new C.TextBlock { Text = value, FontSize = size, TextWrapping = W.TextWrapping.Wrap, VerticalAlignment = W.VerticalAlignment.Center };
        text.SetResourceReference(C.TextBlock.ForegroundProperty, muted ? "MutedText" : "MainText"); return text;
    }
    C.StackPanel Page(string id) { var panel = new C.StackPanel(); pages[id] = panel; return panel; }
    C.StackPanel Horizontal(params W.UIElement[] children) { var row = new C.StackPanel { Orientation = C.Orientation.Horizontal, VerticalAlignment = W.VerticalAlignment.Center }; foreach (var child in children) row.Children.Add(child); return row; }
    C.Button Button(string key, Func<Task> action) { var button = new C.Button { Content = L.T(key), VerticalAlignment = W.VerticalAlignment.Center }; button.Click += async (_, _) => await Execute(action); return button; }
    C.Button Button(string key, Action action) => Button(key, () => { action(); return Task.CompletedTask; });
    C.ComboBox Choice(IEnumerable<object> values, int width = 190) { var box = new C.ComboBox { Width = width, MinHeight = 34, VerticalAlignment = W.VerticalAlignment.Center, Margin = new(8, 0, 0, 0) }; foreach (var value in values) box.Items.Add(value); if (box.Items.Count > 0) box.SelectedIndex = 0; return box; }
    C.CheckBox Switch(string name, bool value, Func<bool, Task> action, string? successMessage = null)
    {
        var check = new C.CheckBox { IsChecked = value, Style = (W.Style)Resources["Switch"], ToolTip = L.T(name), Margin = new(12, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(check, L.T(name));
        check.Click += async (_, _) => { if (!updating) { bool requested = check.IsChecked == true; bool ok = await Execute(() => action(requested), successMessage); if (!ok) check.IsChecked = !requested; } }; return check;
    }
    C.Border Card(W.UIElement content, int padding = 16)
    {
        var card = new C.Border { CornerRadius = new(8), Padding = new(padding), BorderThickness = new(1), Margin = new(0, 0, 0, 6), Child = content };
        card.SetResourceReference(C.Border.BackgroundProperty, "CardSurface"); card.SetResourceReference(C.Border.BorderBrushProperty, "CardStroke"); return card;
    }
    C.Border Setting(string title, string? description, W.UIElement control)
    {
        var grid = new C.Grid(); grid.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        var labels = new C.StackPanel { Margin = new(0, 0, 18, 0), VerticalAlignment = W.VerticalAlignment.Center }; labels.Children.Add(Text(L.T(title)));
        if (description is "restartNeeded" or "performanceShortcut") { var desc = Text(L.T(description), 12, true); desc.Margin = new(0, 5, 0, 0); labels.Children.Add(desc); }
        grid.Children.Add(labels); C.Grid.SetColumn(control, 1); grid.Children.Add(control); return Card(grid);
    }
    void Section(C.StackPanel page, string key) { var title = Text(L.T(key), 14); title.FontWeight = W.FontWeights.SemiBold; title.Margin = new(2, 20, 0, 10); page.Children.Add(title); }
    void ShowPage(string id) { PageTitle.Text = L.T(id); PageSubtitle.Text = id == "monitor" ? L.T("live") : id == "lighting" ? "" : id == "display" ? L.T("profileNote") : id == "shortcuts" ? L.T("trayNote") : L.T("independent"); PageContent.Content = pages[id]; }
    void BuildMonitor()
    {
        var page = Page("monitor"); var grid = new C.Grid { Margin = new(0, 0, 0, 10) };
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new());
        string[] keys = ["CPU", "GPU", "memory"];
        for (int i = 0; i < keys.Length; i++)
        {
            var panel = new C.StackPanel(); panel.Children.Add(Text(L.T(keys[i]), 14, true)); var value = Text("—", 32); value.FontWeight = W.FontWeights.SemiBold; value.Margin = new(0, 8, 0, 4); readings[keys[i]] = value; panel.Children.Add(value);
            var trend = new Trend { Height = 50, Margin = new(0, 6, 0, 0), Stroke = W.SystemColors.AccentColorBrush }; trends[keys[i]] = trend; panel.Children.Add(trend);
            var card = Card(panel, 18); card.Margin = new(i == 0 ? 0 : 6, 0, i == 2 ? 0 : 6, 0); C.Grid.SetColumn(card, i); grid.Children.Add(card);
        }
        page.Children.Add(grid);
        var sensors = new C.Primitives.UniformGrid { Columns = 3 };
        foreach (string id in new[] { "cpuTemp", "gpuTemp", "gpuPower", "cpuFan", "gpuFan", "gpuClock" }) { var panel = new C.StackPanel { Margin = new(4, 7, 10, 7) }; panel.Children.Add(Text(L.T(id), 12, true)); var value = Text("—", 20); value.Margin = new(0, 5, 0, 0); readings[id] = value; panel.Children.Add(value); sensors.Children.Add(panel); }
        page.Children.Add(Card(sensors));
        Section(page, "performance"); var modes = new C.StackPanel { Orientation = C.Orientation.Horizontal };
        foreach (var (id, code) in new[] { ("quiet", 2), ("balanced", 1), ("turbo", 3), ("full", 4) }) modeChoice.Items.Add(new Option(id, code));
        modeChoice.Width = 150; modeChoice.SelectedIndex = -1; modes.Children.Add(modeChoice); modes.Children.Add(Button("apply", ApplyPerformance));
        modeChoice.SelectionChanged += (_, _) => { if (!updating) performanceSelectionDirty = true; };
        page.Children.Add(Setting("performance", "performanceShortcut", modes)); modeText.Margin = new(4, 2, 0, 10); modeText.SetResourceReference(C.TextBlock.ForegroundProperty, "MutedText"); page.Children.Add(modeText);
        restorePerformanceSwitch = Switch("restorePerformance", true, enabled => SavePerformancePolicy(1, enabled));
        acFullSpeedSwitch = Switch("acFullSpeed", false, enabled => SavePerformancePolicy(2, enabled));
        acFullSpeedSwitch.ToolTip = L.T("acFullSpeedHint");
        restorePerformanceSwitch.IsEnabled = acFullSpeedSwitch.IsEnabled = false;
        page.Children.Add(Setting("restorePerformance", null, restorePerformanceSwitch));
        page.Children.Add(Setting("acFullSpeed", null, acFullSpeedSwitch));
        foreach (var (id, code) in new[] { ("hybrid", 0), ("direct", 1) }) gfxChoice.Items.Add(new Option(id, code)); gfxChoice.Width = 150; gfxChoice.SelectedIndex = -1;
        gfxChoice.SelectionChanged += (_, _) => { if (!updating) graphicsSelectionDirty = true; };
        page.Children.Add(Setting("graphics", "restartNeeded", Horizontal(gfxChoice, Button("apply", async () => { if (gfxChoice.SelectedItem is not Option selected) return; int mode = selected.Code; await Task.Run(() => firmware.SetGraphicsMode(mode)); graphicsSelectionDirty = false; graphicsText.Text = L.F("pendingGraphics", L.T(mode == 1 ? "direct" : "hybrid")); Restart(); }))));
        graphicsText.Margin = new(4, 2, 0, 10); graphicsText.SetResourceReference(C.TextBlock.ForegroundProperty, "MutedText"); page.Children.Add(graphicsText);
    }
    void BuildLighting()
    {
        var page = Page("lighting");
        saveLightingButton = new C.Button { Content = L.T("saveLighting"), IsEnabled = false, HorizontalAlignment = W.HorizontalAlignment.Right };
        saveLightingButton.Click += async (_, _) => await Execute(() => LightingOperation(SaveLighting), activeSoftwareLighting != null ? "softwareLightingSaved" : "lightingSaved");
        var saveRow = new C.StackPanel { Margin = new(0, 0, 0, 12) }; saveRow.Children.Add(saveLightingButton); page.Children.Add(saveRow);
        for (int area = 0; area < 3; area++)
        {
            int index = area; string id = new[] { "keyboard", "logo", "bar" }[area];
            var apply = new C.Button { Content = L.T("apply") };
            lightApplyButtons.Add(apply);
            var dirty = new HashSet<string>(); bool built = false, refreshingControls = false; Action? refreshControls = null;
            clearLightingDrafts.Add(dirty.Clear);
            void Changed(string field) { refreshControls?.Invoke(); if (built && !updating) { dirty.Add(field); apply.IsEnabled = lightSwitches[index].IsChecked == true; Status(L.T("lightingParametersChanged")); } }
            var panel = new C.StackPanel(); var titleRow = new C.DockPanel();
            var enable = Switch(id, true, on => LightingOperation(async () => { if (index == 0) { await softwareLighting.Stop(); activeSoftwareLighting = null; keyboardPreviewChanged = true; } await lightingSession.Apply(() => lighting.Enable(index, on)); saveLightingButton.IsEnabled = lightingSession.Pending; apply.IsEnabled = on; }), "lightingPreviewApplied");
            lightSwitches.Add(enable); C.DockPanel.SetDock(enable, C.Dock.Right); titleRow.Children.Add(enable); var title = Text(L.T(id), 16); title.FontWeight = W.FontWeights.SemiBold; titleRow.Children.Add(title); panel.Children.Add(titleRow);
            var choices = area == 0 ? new[] { new LightEffect("screenSync", -1), new LightEffect("audioSync", -2), new LightEffect("soft", -3), new LightEffect("rainbow", 7), new LightEffect("trigger", 5), new LightEffect("wavelet", 3), new LightEffect("colorwave", 6), new LightEffect("firefly", 10), new LightEffect("snake", 8), new LightEffect("wave", 1), new LightEffect("breath", 2), new LightEffect("keep", 9) } : area == 1 ? new[] { new LightEffect("keep", 9), new LightEffect("breath", 2), new LightEffect("gradient", 3), new LightEffect("linked", 4), new LightEffect("linkedBar", 5) } : new[] { new LightEffect("keep", 9), new LightEffect("breath", 2), new LightEffect("gradient", 3), new LightEffect("linked", 4) };
            var effect = Choice(choices, 210); effect.SelectedIndex = area == 0 ? choices.Length - 1 : 0;
            lightEffectChoices.Add(effect);
            var bright = new C.Slider { Minimum = 0, Maximum = 3, Value = 2, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 150, VerticalAlignment = W.VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
            var speed = new C.Slider { Minimum = 1, Maximum = 4, Value = 2, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 150, VerticalAlignment = W.VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
            var direction = Choice(new[] { new Option("right", 1), new Option("left", 0) }, 150);
            var audio = Choice(new[] { L.T("speaker"), L.T("microphone") }, 150);
            C.StackPanel? audioRow = null;
            effect.SelectionChanged += (_, _) => Changed("effect"); direction.SelectionChanged += (_, _) => Changed("direction"); bright.ValueChanged += (_, _) => Changed("brightness"); speed.ValueChanged += (_, _) => Changed("speed");
            Color color = Color.FromArgb(0, 120, 212);
            if (area == 0 && preferences.SoftwareLighting is { } saved && choices.Any(choice => choice.Id == saved.Effect))
            { effect.SelectedItem = choices.Single(choice => choice.Id == saved.Effect); bright.Value = Math.Clamp(saved.Brightness, 0, 3); speed.Value = Math.Clamp(saved.Speed, 1, 4); audio.SelectedIndex = saved.Microphone ? 1 : 0; color = Color.FromArgb(saved.Argb); }
            C.Button? colorButton = null;
            void ColorLabel() { if (colorButton != null) colorButton.Content = $"{L.T("colorPicker")} · #{color.R:X2}{color.G:X2}{color.B:X2}"; }
            effect.SelectionChanged += (_, _) =>
            {
                if (built && !updating && (effect.SelectedItem as LightEffect)?.Code < 0)
                {
                    color = Color.FromArgb(preferences.SoftwareLighting?.Argb ?? Color.White.ToArgb());
                    ColorLabel(); Changed("color");
                }
            };
            colorButton = new C.Button(); colorButton.Click += (_, _) => { using var dialog = new ColorDialog { Color = color, FullOpen = true }; if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) { color = dialog.Color; ColorLabel(); Changed("color"); } }; ColorLabel();
            var effectRow = new C.Grid { Margin = new(0, 16, 0, 0) }; effectRow.ColumnDefinitions.Add(new() { Width = new(100) }); effectRow.ColumnDefinitions.Add(new()); effectRow.Children.Add(Text(L.T("effect"))); C.Grid.SetColumn(effect, 1); effectRow.Children.Add(effect); panel.Children.Add(effectRow);
            var colorRow = new C.StackPanel { Orientation = C.Orientation.Horizontal, Margin = new(0, 10, 0, 0) }; colorRow.Children.Add(new C.TextBlock { Text = L.T("color"), Width = 100, VerticalAlignment = W.VerticalAlignment.Center }); colorRow.Children.Add(colorButton); panel.Children.Add(colorRow);
            if (area == 0)
            {
                keyboardBrightness = bright; keyboardAudioSource = audio;
                setKeyboardColor = value => { color = value; ColorLabel(); Changed("color"); };
                foreach (var (label, control) in new (string, W.UIElement)[] { ("brightness", bright), ("speed", speed), ("direction", direction) }) { var row = new C.StackPanel { Orientation = C.Orientation.Horizontal, Margin = new(0, 10, 0, 0) }; row.Children.Add(new C.TextBlock { Text = L.T(label), Width = 100, VerticalAlignment = W.VerticalAlignment.Center }); row.Children.Add(control); panel.Children.Add(row); }
                audioRow = new C.StackPanel { Orientation = C.Orientation.Horizontal, Margin = new(0, 10, 0, 0), Visibility = ((LightEffect)effect.SelectedItem).Id == "audioSync" ? W.Visibility.Visible : W.Visibility.Collapsed }; audioRow.Children.Add(new C.TextBlock { Text = L.T("audioSource"), Width = 100, VerticalAlignment = W.VerticalAlignment.Center }); audioRow.Children.Add(audio); panel.Children.Add(audioRow);
                effect.SelectionChanged += (_, _) => { audioRow.Visibility = (effect.SelectedItem as LightEffect)?.Id == "audioSync" ? W.Visibility.Visible : W.Visibility.Collapsed; };
                audio.SelectionChanged += (_, _) => Changed("audio");
                readSoftwareLighting = () => ((LightEffect)effect.SelectedItem).Code < 0 ? new(((LightEffect)effect.SelectedItem).Id, color.ToArgb(), (int)bright.Value, (int)speed.Value, audio.SelectedIndex == 1, (displays.SelectedItem as DisplayTarget)?.DeviceId) : null;
            }
            apply.Click += async (_, _) =>
            {
                if (enable.IsChecked != true) return;
                apply.IsEnabled = false;
                bool ok = await Execute(() => LightingOperation(async () =>
                {
                    var selected = effect.SelectedItem as LightEffect ?? throw new InvalidOperationException(L.T("selectLightingEffect"));
                    if (index == 0)
                    {
                        await softwareLighting.Stop(); activeSoftwareLighting = readSoftwareLighting?.Invoke();
                        keyboardPreviewChanged = true;
                        await lightingSession.Apply(() => activeSoftwareLighting is { } preset ? StartSoftwareLighting(preset) : lighting.Keyboard((byte)selected.Code, (int)bright.Value, (int)speed.Value, color, ((Option)direction.SelectedItem).Code));
                    }
                    else await lightingSession.Apply(() => lighting.Accent(index == 1, (byte)selected.Code, color));
                    saveLightingButton.IsEnabled = lightingSession.Pending;
                    dirty.Clear();
                }), "lightingPreviewApplied");
                if (!ok) apply.IsEnabled = enable.IsChecked == true;
            };
            var actions = Horizontal(apply); actions.HorizontalAlignment = W.HorizontalAlignment.Right; actions.Margin = new(0, 16, 0, 0); panel.Children.Add(actions); page.Children.Add(Card(panel, 20));
            lightingBindings.Add(state =>
            {
                var config = state.Config; var preset = index == 0 && config[5] == 0 && softwareLighting.Running ? activeSoftwareLighting : null;
                int code; Color actualColor;
                if (index == 0)
                {
                    code = preset?.Effect switch { "screenSync" => -1, "audioSync" => -2, "soft" => -3, _ => config[5] == 11 ? 9 : config[5] };
                    int Channel(byte value) => value == 127 ? 255 : Math.Min(255, value * 2);
                    actualColor = preset != null ? Color.FromArgb(preset.Argb) : Color.FromArgb(Channel(config[18]), Channel(config[19]), Channel(config[20]));
                    // Real-time mode uses host-side pixel intensity; entering it forces hardware brightness to 3.
                    if (!dirty.Contains("brightness") && !bright.IsMouseCaptureWithin) bright.Value = preset != null ? Math.Round(preset.Brightness * config[8] / 3.0) : config[8];
                    if (!dirty.Contains("speed") && !speed.IsMouseCaptureWithin) speed.Value = Math.Clamp(preset?.Speed ?? config[9], 1, 4);
                    if (!dirty.Contains("direction")) direction.SelectedItem = direction.Items.Cast<Option>().FirstOrDefault(option => option.Code == config[7]);
                    if (!dirty.Contains("audio") && preset != null) audio.SelectedIndex = preset.Microphone ? 1 : 0;
                }
                else
                {
                    var mode = index == 1 ? state.Logo : state.Bar; var rgb = index == 1 ? state.LogoColor : state.BarColor;
                    int linked = index == 1 ? (config[12] >> 6) & 3 : (config[12] & 4) == 0 ? 0 : 1;
                    code = linked == 0 ? 4 : index == 1 && linked == 2 ? 5 : mode[5] == 1 ? 9 : mode[5];
                    actualColor = Color.FromArgb(rgb[6], rgb[7], rgb[8]);
                }
                if (!dirty.Contains("effect")) effect.SelectedItem = choices.FirstOrDefault(choice => choice.Code == code);
                if (!dirty.Contains("color")) { color = actualColor; ColorLabel(); }
                bool previouslyEnabled = enable.IsChecked == true;
                enable.IsChecked = Lighting.AreaEnabled(config[12], index);
                if (enable.IsChecked != true) apply.IsEnabled = false;
                else if (!previouslyEnabled) apply.IsEnabled = true;
                refreshControls?.Invoke();
            });
            refreshControls = () =>
            {
                if (refreshingControls) return;
                refreshingControls = true;
                try
                {
                int code = (effect.SelectedItem as LightEffect)?.Code ?? 0;
                bool on = enable.IsChecked == true;
                string directionLabel = code == 7 ? "clockwise" : "right";
                if (direction.Items[0] is Option first && first.Id != directionLabel)
                {
                    int selectedDirection = (direction.SelectedItem as Option)?.Code ?? 1;
                    bool wasUpdating = updating; updating = true;
                    try { direction.Items.Clear(); direction.Items.Add(new Option(directionLabel, 1)); direction.Items.Add(new Option(code == 7 ? "counterclockwise" : "left", 0)); direction.SelectedItem = direction.Items.Cast<Option>().First(option => option.Code == selectedDirection); }
                    finally { updating = wasUpdating; }
                }
                effect.IsEnabled = on; bright.IsEnabled = on;
                colorButton.IsEnabled = on && (index == 0 ? code is 2 or 9 or -2 or -3 : code is 9 or 2);
                speed.IsEnabled = on && index == 0 && code is 1 or 2 or 3 or 6 or 7 or 8 or -3;
                direction.IsEnabled = on && index == 0 && code is 1 or 6 or 7;
                audio.IsEnabled = on && index == 0 && code == -2;
                if (!on) apply.IsEnabled = false;
                }
                finally { refreshingControls = false; }
            };
            lightingControlStates.Add(() => new() { ["effect"] = effect.IsEnabled, ["brightness"] = bright.IsEnabled, ["color"] = colorButton.IsEnabled, ["speed"] = speed.IsEnabled, ["direction"] = direction.IsEnabled, ["audio"] = audio.IsEnabled });
            enable.Checked += (_, _) => refreshControls(); enable.Unchecked += (_, _) => refreshControls();
            built = true; refreshControls();
        }
    }
    async Task LightingOperation(Func<Task> action)
    {
        await lightingUiGate.WaitAsync(); pages["lighting"].IsEnabled = false;
        try { await action(); }
        finally { pages["lighting"].IsEnabled = true; lightingUiGate.Release(); }
    }
    Task StartSoftwareLighting(SoftwareLightingPreset preset)
    {
        string? screen = DisplayControl.Displays().FirstOrDefault(display => display.DeviceId == preset.DisplayId)?.GdiName;
        return softwareLighting.Start(preset.Effect, Color.FromArgb(preset.Argb), Math.Clamp(preset.Brightness, 0, 3) / 3.0, Math.Clamp(preset.Speed, 1, 4), preset.Microphone, screen);
    }
    async Task SaveLighting()
    {
        var preset = activeSoftwareLighting;
        try
        {
            await softwareLighting.Stop();
            // Stop restores the last actual hardware effect. Persist that valid
            // boot fallback; software parameters are saved separately below.
            await lightingSession.Save();
            if (keyboardPreviewChanged || preset != null) preferences.SoftwareLighting = preset;
            preferences.Save(); keyboardPreviewChanged = false; saveLightingButton!.IsEnabled = false;
            foreach (var clear in clearLightingDrafts) clear(); ResetLightApplyButtons();
        }
        finally
        {
            if (preset != null)
                try { await StartSoftwareLighting(preset); activeSoftwareLighting = preset; }
                catch { activeSoftwareLighting = null; throw; }
        }
    }
    void ResetLightApplyButtons() { for (int i = 0; i < lightApplyButtons.Count; i++) lightApplyButtons[i].IsEnabled = lightSwitches[i].IsChecked == true; }
    async Task ResumeSavedSoftwareLighting()
    {
        if (preferences.SoftwareLighting is not { Effect: "screenSync" or "audioSync" or "soft" } preset) return;
        try { await LightingOperation(async () => { if (keyboardBrightness != null) { updating = true; keyboardBrightness.Value = Math.Clamp(preset.Brightness, 0, 3); updating = false; } await StartSoftwareLighting(preset); activeSoftwareLighting = preset; }); }
        catch (Exception ex) { Store.Log("Saved software lighting: " + ex); Status(L.Error(ex), true); }
    }
    void BuildDisplay()
    {
        var page = Page("display"); displays.Width = 350; displays.MinHeight = 34;
        displays.SelectionChanged += (_, _) => UpdateProfile(); page.Children.Add(Setting("displayTarget", null, displays));
        eyeSwitch = Switch("eye", false, on => { displayControl.Eye(Display, on, preferences.Warmth); return Task.CompletedTask; }); page.Children.Add(Setting("eye", "eyeDescription", eyeSwitch));
        var warmth = new C.Slider { Minimum = 0, Maximum = 100, Value = preferences.Warmth, Width = 300, TickFrequency = 10, VerticalAlignment = W.VerticalAlignment.Center };
        warmth.ValueChanged += (_, _) => { preferences.Warmth = (int)warmth.Value; warmthTimer.Stop(); warmthTimer.Start(); }; page.Children.Add(Setting("warmth", null, warmth));
        overdriveSwitch = Switch("overdrive", false, on => Task.Run(() => firmware.SetOverdrive(on))); page.Children.Add(Setting("overdrive", "overdriveDescription", overdriveSwitch));
        Section(page, "profiles"); var panel = new C.StackPanel(); profileChoice.Width = 420; profileChoice.HorizontalAlignment = W.HorizontalAlignment.Left;
        foreach (var path in DisplayControl.Profiles()) profileChoice.Items.Add(new Profile(path)); if (profileChoice.Items.Count > 0) profileChoice.SelectedIndex = 0;
        panel.Children.Add(profileChoice); profileText.Margin = new(0, 12, 0, 16); profileText.TextWrapping = W.TextWrapping.Wrap; profileText.SetResourceReference(C.TextBlock.ForegroundProperty, "MutedText"); panel.Children.Add(profileText);
        panel.Children.Add(Horizontal(Button("apply", () => { var display = Display; RememberProfile(display); displayControl.Dispose(); eyeSwitch.IsChecked = false; DisplayControl.ApplyProfile(display, ((Profile)profileChoice.SelectedItem).Path); UpdateProfile(); }), Button("restore", () => { if (preferences.ColorBackups.TryGetValue(Display.DeviceId, out var snapshot)) snapshot.Restore(); else if (preferences.OriginalProfiles.TryGetValue(Display.DeviceId, out var profile) && !string.IsNullOrEmpty(profile)) DisplayControl.ApplyProfile(Display, ProfilePath(profile)); else throw new InvalidOperationException(L.T("noOriginalProfile")); UpdateProfile(); }), Button("systemColor", () => Process.Start(new ProcessStartInfo("colorcpl.exe") { UseShellExecute = true })))); page.Children.Add(Card(panel, 20));
        displayControls.AddRange([eyeSwitch, warmth, panel]); RefreshDisplays();
    }
    void BuildShortcuts()
    {
        var page = Page("shortcuts");
        fnSwitch = Switch("fnLock", false, locked => Task.Run(() => firmware.SetFnLocked(locked))); winSwitch = Switch("winLock", false, locked => Task.Run(() => firmware.SetWinLocked(locked)));
        page.Children.Add(Setting("fnLock", "fnLockNote", fnSwitch)); page.Children.Add(Setting("winLock", "winLockNote", winSwitch));
        Section(page, "captureF7"); page.Children.Add(Setting("captureF7", null, Switch("captureF7", preferences.CaptureF7, on => { preferences.CaptureF7 = on; preferences.Save(); return Task.CompletedTask; })));
        string[] ids = ["copilot", "toggleEye", "launchProgram", "nothing"]; var action = Choice(ids.Select((id, i) => new Option(id, i)), 270); int selected = Array.IndexOf(ids, preferences.F7Action); action.SelectedIndex = selected >= 0 ? selected : 0; preferences.F7Action = ids[action.SelectedIndex];
        action.SelectionChanged += (_, _) => { preferences.F7Action = ((Option)action.SelectedItem).Id; preferences.Save(); }; page.Children.Add(Setting("action", null, action));
        var path = new C.TextBox { Text = preferences.Program, Width = 330, MinHeight = 34, VerticalContentAlignment = W.VerticalAlignment.Center };
        path.TextChanged += (_, _) => { preferences.Program = path.Text; preferences.Save(); }; page.Children.Add(Setting("program", null, Horizontal(path, Button("browse", () => { var file = new Microsoft.Win32.OpenFileDialog { Filter = "Program|*.exe;*.lnk" }; if (file.ShowDialog(this) == true) path.Text = file.FileName; }))));
        Section(page, "background"); page.Children.Add(Setting("startup", null, Switch("startup", StartupTask.Enabled, enabled => { StartupTask.Set(enabled); return Task.CompletedTask; })));
    }
    void BuildSettings()
    {
        var page = Page("settings"); page.Children.Add(Setting("language", "automaticLanguage", Text(L.Language == "zh-CN" ? "简体中文" : L.Language == "zh-TW" ? "繁體中文" : L.Language == "ja" ? "日本語" : "English")));
        Section(page, "connections"); page.Children.Add(Setting("firmware", null, firmwareText)); page.Children.Add(Setting("lighting", null, lightingText));
        var actions = Horizontal(Button("refresh", InitializeHardware), Button("diagnostics", async () => { string path = await Diagnostics.Write(); Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }), Button("logs", () => Process.Start(new ProcessStartInfo(Store.Root) { UseShellExecute = true }))); actions.Margin = new(0, 12, 0, 12); page.Children.Add(actions);
        if (!HardwareClient.Installed) page.Children.Add(Setting("installService", "serviceNote", Button("installService", async () => { var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--install-service --owner-sid " + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value) { UseShellExecute = true, Verb = "runas" }); if (process != null) await process.WaitForExitAsync(); if (!HardwareClient.Installed) throw new IOException("Service install failed."); await InitializeHardware(); })));
        Section(page, "about");
        var about = new C.StackPanel(); about.Children.Add(Text("SukaControl 0.2.9"));
        var author = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("SuzukiNatsume")) { NavigateUri = new Uri("https://github.com/SuzukiNatsume") };
        author.RequestNavigate += (_, e) => { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
        var link = Text(""); link.Margin = new(0, 8, 0, 0); link.Inlines.Add(author); about.Children.Add(link); page.Children.Add(Card(about));
    }
    async Task InitializeHardware()
    {
        try { await Task.Run(firmware.Connect); firmwareText.Text = L.T("connected"); } catch (Exception ex) { firmwareText.Text = L.Error(ex); }
        try { lighting.Detect(); var config = await lighting.ReadConfig(); for (int i = 0; i < 3; i++) lightSwitches[i].IsChecked = Lighting.AreaEnabled(config[12], i); lightingText.Text = L.T("connected"); } catch (Exception ex) { lightingText.Text = L.Error(ex); }
        await UpdateModes(); await PollKeys(); await Poll(); Status(L.T("ready")); await ResumeSavedSoftwareLighting(); hardwareReady.TrySetResult();
    }
    async Task UpdateModes()
    {
        try
        {
            var values = await Task.Run(() => (mode: firmware.Performance(), od: firmware.Overdrive(), graphics: firmware.GraphicsMode()));
            updating = true;
            UpdatePerformanceReadback(values.mode);
            if (!graphicsSelectionDirty) gfxChoice.SelectedIndex = values.graphics is 0 or 1 ? values.graphics : -1;
            if (overdriveSwitch != null) overdriveSwitch.IsChecked = values.od;
        }
        catch (Exception ex) { modeText.Text = L.Error(ex); }
        finally { updating = false; }
    }
    async Task PollKeys()
    {
        if (keyPolling || exiting || !HardwareClient.Installed) return;
        keyPolling = true;
        try
        {
            var reply = await Task.Run(() => HardwareClient.Send(new(0x40)));
            if (!reply.Ok || reply.Keys == null) return;
            var state = reply.Keys;
            updating = true;
            if (lightingUiGate.CurrentCount != 0 && reply.Lights is { } lights) foreach (var bind in lightingBindings) bind(lights);
            if (!policySaving && reply.Policy is { } policy && restorePerformanceSwitch != null && acFullSpeedSwitch != null)
            {
                restorePerformanceSwitch.IsChecked = policy.RestoreOnStartup; acFullSpeedSwitch.IsChecked = policy.FullSpeedOnAC;
                restorePerformanceSwitch.IsEnabled = acFullSpeedSwitch.IsEnabled = true;
            }
            if (state.PerformanceMode > 0) UpdatePerformanceReadback(state.PerformanceMode);
            if (fnSwitch != null) fnSwitch.IsChecked = state.FnLocked;
            if (winSwitch != null) winSwitch.IsChecked = state.WinLocked;
        }
        catch (Exception ex) { Store.Log("Key state: " + ex.Message); }
        finally { updating = false; keyPolling = false; }
    }
    public Task RunF7() => F7();
    async Task SavePerformancePolicy(byte option, bool enabled)
    {
        policySaving = true;
        restorePerformanceSwitch!.IsEnabled = acFullSpeedSwitch!.IsEnabled = false;
        try
        {
            var reply = await Task.Run(() => HardwareClient.Send(new(0x43, true, option, enabled ? (byte)1 : (byte)0)));
            if (!reply.Ok) throw new IOException(reply.Error);
        }
        finally { policySaving = false; restorePerformanceSwitch.IsEnabled = acFullSpeedSwitch.IsEnabled = true; }
        await UpdateModes();
    }
    void UpdatePerformanceReadback(int mode)
    {
        if (!performanceSelectionDirty) modeChoice.SelectedIndex = mode switch { 2 => 0, 1 => 1, 3 => 2, 4 => 3, _ => -1 };
        modeText.Text = L.T(mode switch { 2 => "quiet", 1 => "balanced", 3 => "turbo", 4 => "full", _ => "unavailable" });
    }
    async Task Poll()
    {
        if (polling || exiting) return; polling = true;
        try
        {
            var s = await Task.Run(() => telemetry.Read(firmware));
            string Value(double? value, string unit) => value is null ? "—" : $"{value:0.#} {unit}";
            foreach (var (key, value) in new (string, double?)[] { ("CPU", s.Cpu), ("GPU", s.GpuUsage), ("memory", s.Memory) }) { readings[key].Text = Value(value, "%"); trends[key].Add(value); }
            readings["cpuTemp"].Text = Value(s.CpuTemp, "°C"); readings["gpuTemp"].Text = Value(s.GpuTemp, "°C"); readings["gpuPower"].Text = Value(s.GpuPower, "W"); readings["gpuClock"].Text = Value(s.GpuClock, "MHz"); readings["cpuFan"].Text = Value(s.CpuFan, "RPM"); readings["gpuFan"].Text = Value(s.GpuFan, "RPM");
            if (++ticks % 5 == 0) await UpdateModes();
        }
        catch (Exception ex) { Status(L.Error(ex), true); }
        finally { polling = false; }
    }
    async Task F7() => await Execute(async () =>
    {
        switch (preferences.F7Action)
        {
            case "copilot": using (var protocol = Registry.ClassesRoot.OpenSubKey("ms-copilot")) Process.Start(new ProcessStartInfo(protocol != null ? "ms-copilot:" : "https://copilot.microsoft.com/") { UseShellExecute = true }); break;
            case "toggleEye": ToggleEye(); break;
            case "launchProgram": if (!File.Exists(preferences.Program)) { Status(L.T("chooseProgram"), true); return; } Process.Start(new ProcessStartInfo(preferences.Program) { UseShellExecute = true }); break;
        }
    });
    async Task ApplyPerformance()
    {
        // Read WPF controls before leaving the dispatcher thread.
        if (modeChoice.SelectedItem is not Option selected) return;
        int mode = selected.Code;
        await Task.Run(() => firmware.SetPerformance(mode));
        performanceSelectionDirty = false;
        await UpdateModes();
    }
    internal async Task<Dictionary<string, object?>> TestPerformanceUI()
    {
        var result = new Dictionary<string, object?>(); int before = await Task.Run(firmware.Performance);
        try
        {
            int target = before == 2 ? 1 : 2;
            modeChoice.SelectedItem = modeChoice.Items.Cast<Option>().Single(option => option.Code == target);
            await ApplyPerformance();
            int actual = await Task.Run(firmware.Performance);
            result["before"] = before; result["requested"] = target; result["actual"] = actual;
            result["passed"] = actual == target;
        }
        catch (Exception ex) { result["passed"] = false; result["error"] = ex.ToString(); }
        finally
        {
            await Task.Run(() => firmware.SetPerformance(before)); performanceSelectionDirty = false; await UpdateModes();
            result["restored"] = await Task.Run(firmware.Performance) == before;
        }
        return result;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    void ToggleEye() { displayControl.Eye(Display, !displayControl.EyeEnabled, preferences.Warmth); if (eyeSwitch != null) eyeSwitch.IsChecked = displayControl.EyeEnabled; }
    void RememberProfile(DisplayTarget display) { if (!preferences.ColorBackups.ContainsKey(display.DeviceId)) preferences.ColorBackups[display.DeviceId] = ColorSnapshot.Capture(display); if (!preferences.OriginalProfiles.ContainsKey(display.DeviceId)) preferences.OriginalProfiles[display.DeviceId] = DisplayControl.CurrentProfile(display); preferences.Save(); }
    static string ProfilePath(string profile) => Path.IsPathRooted(profile) ? profile : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", profile);
    void UpdateProfile() { profileText.Text = displays.SelectedItem is DisplayTarget display ? L.F("currentProfile", DisplayControl.CurrentProfile(display) is { Length: > 0 } p ? p : L.T("systemDefault")) : L.T("unavailable"); }
    void Restart() { if (W.MessageBox.Show(this, L.T("restartConfirm"), L.T("restart"), W.MessageBoxButton.YesNo, W.MessageBoxImage.Question) == W.MessageBoxResult.Yes) Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true }); }
    public void Restore() { Show(); WindowState = W.WindowState.Normal; Activate(); }
    internal void SelectPage(string id) { foreach (C.ListBoxItem item in Navigation.Items) if ((string)item.Tag == id) { Navigation.SelectedItem = item; break; } }
    async Task CloseToTray()
    {
        if (closingPreview) return;
        closingPreview = true;
        IsEnabled = false;
        try
        {
            // Always enter the session gate, including when Apply is still capturing its baseline.
            if (!await Execute(CancelTemporaryLighting, "lightingPreviewEnded")) return;
            Hide();
        }
        finally { closingPreview = false; IsEnabled = true; }
    }
    Task RevertLighting() => LightingOperation(async () =>
    {
        await softwareLighting.Stop(); activeSoftwareLighting = null; keyboardPreviewChanged = false;
        await lightingSession.Revert();
        foreach (var clear in clearLightingDrafts) clear();
        if (saveLightingButton != null) saveLightingButton.IsEnabled = false;
        ResetLightApplyButtons();
    });
    Task CancelTemporaryLighting() => LightingOperation(async () =>
    {
        if (lightingSession.Pending)
        {
            await softwareLighting.Stop(); activeSoftwareLighting = null; keyboardPreviewChanged = false;
            await lightingSession.Revert();
            if (preferences.SoftwareLighting is { Effect: "screenSync" or "audioSync" or "soft" } saved)
            {
                try { await StartSoftwareLighting(saved); activeSoftwareLighting = saved; }
                catch (Exception ex) { Store.Log("Resume saved lighting after preview: " + ex); }
            }
        }
        foreach (var clear in clearLightingDrafts) clear();
        if (saveLightingButton != null) saveLightingButton.IsEnabled = false;
        ResetLightApplyButtons();
    });
    internal async Task<Dictionary<string, object?>> TestLightingUI()
    {
        var result = new Dictionary<string, object?>();
        await LightingOperation(async () => { await softwareLighting.Stop(); activeSoftwareLighting = null; });
        var before = await lighting.ReadConfig();
        try
        {
            SelectPage("lighting");
            lightEffectChoices[0].SelectedItem = lightEffectChoices[0].Items.Cast<LightEffect>().Single(effect => effect.Code == 2);
            lightApplyButtons[0].RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            for (int i = 0; i < 100 && saveLightingButton?.IsEnabled != true; i++) await Task.Delay(50);
            result["applyDisabled"] = !lightApplyButtons[0].IsEnabled;
            result["saveEnabled"] = saveLightingButton?.IsEnabled == true;
            result["savePrompt"] = StatusText.Text == L.T("lightingPreviewApplied");
            if (result["applyDisabled"] is not true || result["saveEnabled"] is not true || result["savePrompt"] is not true) throw new IOException("Apply/Save UI state failed.");
            if ((await lighting.ReadConfig())[5] != 2) throw new IOException("Preview effect was not applied.");
            lightEffectChoices[0].SelectedIndex = 0;
            result["parameterChangeReenablesApply"] = lightApplyButtons[0].IsEnabled;
            await CloseToTray();
            // A saved software effect correctly resumes on close. Stop it to
            // compare its restored hardware fallback, not the live mode-0 report.
            await LightingOperation(async () => { await softwareLighting.Stop(); activeSoftwareLighting = null; });
            var after = await lighting.ReadConfig();
            result["closeHidesWindow"] = !IsVisible;
            result["closeRestoresSettings"] = after.AsSpan(5, 59).SequenceEqual(before.AsSpan(5, 59));
            result["passed"] = lightApplyButtons[0].IsEnabled && !IsVisible && result["closeRestoresSettings"] is true;
        }
        catch (Exception ex) { result["passed"] = false; result["error"] = ex.ToString(); }
        finally { await RevertLighting(); }
        return result;
    }
    internal async Task<Dictionary<string, object?>> TestSoftwareLightingUI()
    {
        var result = new Dictionary<string, object?>();
        await LightingOperation(async () => { await softwareLighting.Stop(); activeSoftwareLighting = null; });
        var before = await lighting.ReadConfig(); var oldPreference = preferences.SoftwareLighting;
        try
        {
            SelectPage("lighting");
            lightEffectChoices[0].SelectedItem = lightEffectChoices[0].Items.Cast<LightEffect>().Single(effect => effect.Id == "soft");
            if (keyboardBrightness != null) keyboardBrightness.Value = 3;
            lightApplyButtons[0].RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            for (int i = 0; i < 100 && (softwareLighting.FramesWritten == 0 || !softwareLighting.Running || saveLightingButton?.IsEnabled != true); i++) await Task.Delay(50);
            if (!softwareLighting.Running || softwareLighting.FramesWritten == 0) throw new IOException("Soft glow preview did not start.");
            result["softwarePreviewStarted"] = true;
            result["framesWritten"] = softwareLighting.FramesWritten;
            saveLightingButton!.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            for (int i = 0; i < 100 && (StatusText.Text != L.T("softwareLightingSaved") || !softwareLighting.Running); i++) await Task.Delay(50);
            if (preferences.SoftwareLighting?.Effect != "soft" || StatusText.Text != L.T("softwareLightingSaved")) throw new IOException("Software effect settings were not saved.");
            result["softwareParametersSaved"] = true;
            for (int i = 0; i < 20; i++) { await PollKeys(); await Task.Delay(60); }
            result["effectSelectionSurvivesReadback"] = (lightEffectChoices[0].SelectedItem as LightEffect)?.Id == "soft";
            if (result["effectSelectionSurvivesReadback"] is not true) throw new IOException("Readback replaced the active software effect.");
            result["applyReenabledAfterSave"] = lightApplyButtons[0].IsEnabled;
            if (!lightApplyButtons[0].IsEnabled) throw new IOException("Save did not reenable Apply.");
            await CloseToTray();
            var fallback = await lighting.ReadConfig();
            result["savedSoftwareContinuesInTray"] = softwareLighting.Running;
            result["activeControllerMode"] = fallback[5];
            result["passed"] = softwareLighting.Running && fallback[5] == 0 && !IsVisible;
        }
        catch (Exception ex) { result["passed"] = false; result["error"] = ex.ToString(); }
        finally
        {
            await RevertLighting(); await lighting.Exchange(2, Lighting.SettingsPayload(before, true)); await lighting.Save();
            preferences.SoftwareLighting = oldPreference; preferences.Save();
            var after = await lighting.ReadConfig(); result["originalHardwareRestored"] = after.AsSpan(5, 59).SequenceEqual(before.AsSpan(5, 59));
            if (result["originalHardwareRestored"] is not true) result["passed"] = false;
        }
        return result;
    }
    internal async Task<Dictionary<string, object?>> TestLightingReadbackUI()
    {
        var result = new Dictionary<string, object?>();
        await RevertLighting(); var before = await lighting.ReadConfig();
        try
        {
            await lighting.Keyboard(2, 1, 3, Color.Coral, 0);
            for (int i = 0; i < 60; i++)
            {
                await PollKeys();
                if ((lightEffectChoices[0].SelectedItem as LightEffect)?.Code == 2 && keyboardBrightness?.Value == 1) break;
                await Task.Delay(50);
            }
            result["externalEffectReflected"] = (lightEffectChoices[0].SelectedItem as LightEffect)?.Code == 2;
            result["externalBrightnessReflected"] = keyboardBrightness?.Value == 1;
            result["passed"] = result["externalEffectReflected"] is true && result["externalBrightnessReflected"] is true;
        }
        finally { await lighting.Exchange(2, Lighting.SettingsPayload(before, false)); }
        return result;
    }
    internal async Task<Dictionary<string, object?>> TestAudioLightingUI()
    {
        var result = new Dictionary<string, object?>();
        await RevertLighting(); var before = await lighting.ReadConfig(); var oldPreference = preferences.SoftwareLighting; preferences.SoftwareLighting = null;
        try
        {
            SelectPage("lighting");
            lightEffectChoices[0].SelectedItem = lightEffectChoices[0].Items.Cast<LightEffect>().Single(effect => effect.Id == "audioSync");
            if (keyboardBrightness != null) keyboardBrightness.Value = 2;
            if (keyboardAudioSource != null) keyboardAudioSource.SelectedIndex = 0;
            setKeyboardColor?.Invoke(Color.White);
            lightApplyButtons[0].RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            for (int i = 0; i < 100 && (!softwareLighting.Running || saveLightingButton?.IsEnabled != true); i++) await Task.Delay(50);
            using var device = NAudio.Wave.WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice();
            result["outputMuted"] = device.AudioEndpointVolume.Mute;
            using var output = new NAudio.Wave.WasapiOut(device, NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 100);
            var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            var buffer = new NAudio.Wave.BufferedWaveProvider(format);
            var tone = new byte[48000 * 2 * 4 * 4];
            for (int i = 0; i < 48000 * 4; i++) for (int channel = 0; channel < 2; channel++) BitConverter.GetBytes((float)(.04 * Math.Sin(2 * Math.PI * 440 * i / 48000))).CopyTo(tone, (i * 2 + channel) * 4);
            buffer.AddSamples(tone, 0, tone.Length); output.Init(buffer); output.Play();
            for (int i = 0; i < 60 && (softwareLighting.AudioFramesReceived == 0 || softwareLighting.AudioPeak < .0001 || softwareLighting.LitKeys == 0); i++) await Task.Delay(50);
            result["audioFramesReceived"] = softwareLighting.AudioFramesReceived; result["audioPeak"] = softwareLighting.AudioPeak; result["litKeys"] = softwareLighting.LitKeys;
            result["controllerMode"] = (await lighting.ReadConfig())[5];
            result["controllerBrightness"] = (await lighting.ReadConfig())[8];
            var observedModes = new List<byte>();
            for (int i = 0; i < 20; i++) { await PollKeys(); observedModes.Add((await lighting.ReadConfig())[5]); await Task.Delay(60); }
            result["realTimeModeStayedActive"] = observedModes.All(mode => mode == 0);
            result["framesWritten"] = softwareLighting.FramesWritten; result["changedFrames"] = softwareLighting.ChangedFrames;
            result["visualizerSelectionSurvivesReadback"] = (lightEffectChoices[0].SelectedItem as LightEffect)?.Id == "audioSync";
            result["passed"] = result["realTimeModeStayedActive"] is true && softwareLighting.FramesWritten > 10 && softwareLighting.ChangedFrames > 1 && result["controllerMode"] is byte mode && mode == 0 && softwareLighting.AudioFramesReceived > 0 && softwareLighting.AudioPeak >= .0001 && softwareLighting.LitKeys > 0 && result["visualizerSelectionSurvivesReadback"] is true;
            output.Stop(); await CloseToTray(); result["closeStopsAudio"] = !softwareLighting.Running;
        }
        catch (Exception ex) { result["passed"] = false; result["error"] = ex.ToString(); }
        finally { await RevertLighting(); await lighting.Exchange(2, Lighting.SettingsPayload(before, false)); preferences.SoftwareLighting = oldPreference; }
        return result;
    }
    internal Dictionary<string, object?> TestDisabledLightingUI()
    {
        bool previousUpdating = updating; bool passed = true; var results = new List<bool>(); var modes = new List<object>();
        try
        {
            updating = true;
            for (int i = 0; i < lightSwitches.Count; i++)
            {
                updating = true; bool? enabled = lightSwitches[i].IsChecked; object? selected = lightEffectChoices[i].SelectedItem;
                lightSwitches[i].IsChecked = false; ResetLightApplyButtons();
                lightEffectChoices[i].SelectedIndex = (lightEffectChoices[i].SelectedIndex + 1) % lightEffectChoices[i].Items.Count;
                bool disabled = !lightApplyButtons[i].IsEnabled && lightingControlStates[i]().Values.All(value => !value); results.Add(disabled); passed &= disabled;
                lightSwitches[i].IsChecked = true;
                foreach (LightEffect choice in lightEffectChoices[i].Items)
                {
                    lightEffectChoices[i].SelectedItem = choice; var controls = lightingControlStates[i]();
                    modes.Add(new { area = i, effect = choice.Id, controls });
                    if (i == 0 && choice.Id == "screenSync") passed &= !controls["color"] && !controls["speed"] && !controls["direction"] && !controls["audio"];
                    if (i == 0 && choice.Id == "audioSync") passed &= controls["audio"] && !controls["speed"] && !controls["direction"];
                    if (i > 0 && choice.Id is "linked" or "linkedBar" or "gradient") passed &= !controls["color"] && !controls["speed"] && !controls["direction"] && !controls["audio"];
                }
                updating = true; lightSwitches[i].IsChecked = enabled; lightEffectChoices[i].SelectedItem = selected; updating = previousUpdating;
            }
        }
        finally { updating = previousUpdating; foreach (var clear in clearLightingDrafts) clear(); ResetLightApplyButtons(); }
        return new() { ["passed"] = passed, ["disabledApplyForAllThreeAreas"] = results, ["modeControls"] = modes };
    }
    public async Task Exit(bool restoreColor = false) { exiting = true; IsEnabled = false; timer.Stop(); keyTimer.Stop(); warmthTimer.Stop(); try { await RevertLighting(); } catch (Exception ex) { Store.Log(ex.ToString()); } if (restoreColor) foreach (var snapshot in preferences.ColorBackups.Values) snapshot.Restore(); Close(); }
    async Task<bool> Execute(Func<Task> action, string? successMessage = null)
    {
        Status(L.T("working")); try { await action(); Status(L.T(successMessage ?? "done")); return true; } catch (Exception ex) { Status(ex is InvalidOperationException ? ex.Message : L.Error(ex), true); return false; }
    }
    void Status(string text, bool error = false) { StatusText.Text = error ? L.T("error") + " · " + text : text; Store.Log(text); }
    internal void SavePreview(string path)
    {
        var background = (M.SolidColorBrush)Resources["MainText"]; bool light = background.Color.R < 100;
        var drawing = new M.DrawingVisual(); using (var context = drawing.RenderOpen()) { context.DrawRectangle(new M.SolidColorBrush(light ? M.Color.FromRgb(243, 243, 243) : M.Color.FromRgb(32, 32, 32)), null, new W.Rect(0, 0, ActualWidth, ActualHeight)); context.DrawRectangle(new M.VisualBrush(this), null, new W.Rect(0, 0, ActualWidth, ActualHeight)); }
        var bitmap = new M.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, M.PixelFormats.Pbgra32); bitmap.Render(drawing); var encoder = new M.Imaging.PngBitmapEncoder(); encoder.Frames.Add(M.Imaging.BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    sealed record Option(string Id, int Code) { public override string ToString() => L.T(Id); }
    sealed record LightEffect(string Id, int Code) { public override string ToString() => L.T(Id); }
    sealed record Profile(string Path) { public override string ToString() => System.IO.Path.GetFileName(Path); }
    sealed class Trend : W.FrameworkElement
    {
        readonly Queue<double?> values = new(); public M.Brush Stroke { get; set; } = M.Brushes.DodgerBlue;
        public void Add(double? value) { values.Enqueue(value); while (values.Count > 45) values.Dequeue(); InvalidateVisual(); }
        protected override void OnRender(M.DrawingContext dc)
        {
            base.OnRender(dc); var values = this.values.ToArray(); if (values.Length < 2) return; var geometry = new M.StreamGeometry();
            using (var context = geometry.Open()) { bool started = false; for (int i = 0; i < values.Length; i++) { if (values[i] == null) { started = false; continue; } var point = new W.Point(i * ActualWidth / 44, ActualHeight - Math.Clamp(values[i]!.Value, 0, 100) * ActualHeight / 100); if (started) context.LineTo(point, true, false); else { context.BeginFigure(point, false, false); started = true; } } }
            dc.DrawGeometry(null, new M.Pen(Stroke, 1.6), geometry);
        }
    }
}
