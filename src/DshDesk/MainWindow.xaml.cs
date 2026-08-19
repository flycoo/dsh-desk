using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DshDesk.Models;
using DshDesk.Services;
using Microsoft.Web.WebView2.Core;
using Forms = System.Windows.Forms;

namespace DshDesk;

public partial class MainWindow : Window
{
    private const string DshThemeBridgeScript = """
        (() => {
          if (window.__dshDeskThemeBridgeInstalled) return;
          window.__dshDeskThemeBridgeInstalled = true;

          let lastTheme = '';
          let scheduled = false;

          function parseColor(value) {
            const match = value && value.match(/rgba?\((\d+)[, ]+\s*(\d+)[, ]+\s*(\d+)(?:[, /]+\s*([\d.]+))?\)/i);
            if (!match) return null;
            const alpha = match[4] === undefined ? 1 : Number(match[4]);
            if (alpha < 0.2) return null;
            return [Number(match[1]), Number(match[2]), Number(match[3])];
          }

          function findBackground() {
            const candidates = [];
            if (document.body) {
              candidates.push(document.elementFromPoint(innerWidth / 2, innerHeight / 2));
              candidates.push(document.body);
            }
            candidates.push(document.documentElement);

            for (const candidate of candidates) {
              let element = candidate;
              while (element) {
                const color = parseColor(getComputedStyle(element).backgroundColor);
                if (color) return color;
                element = element.parentElement;
              }
            }
            return null;
          }

          function publishTheme() {
            scheduled = false;
            const color = findBackground();
            const theme = color
              ? ((0.2126 * color[0] + 0.7152 * color[1] + 0.0722 * color[2]) >= 145 ? 'light' : 'dark')
              : (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');
            if (theme === lastTheme) return;
            lastTheme = theme;
            window.chrome.webview.postMessage({ source: 'dsh-desk-theme', theme });
          }

          window.__dshDeskPublishTheme = publishTheme;

          function schedulePublish() {
            if (scheduled) return;
            scheduled = true;
            requestAnimationFrame(publishTheme);
          }

          function installObservers() {
            if (!document.documentElement) {
              setTimeout(installObservers, 0);
              return;
            }

            new MutationObserver(schedulePublish).observe(document.documentElement, {
              attributes: true,
              childList: true,
              subtree: true,
              attributeFilter: ['class', 'style', 'data-theme', 'data-color-mode']
            });
            matchMedia('(prefers-color-scheme: light)').addEventListener('change', schedulePublish);
            setInterval(schedulePublish, 1000);
            schedulePublish();
          }

          if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', installObservers, { once: true });
          } else {
            installObservers();
          }
        })();
        """;

    private readonly DshSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly LogService _log;
    private readonly DshProcessManager _processManager;
    private readonly DshUpdater _updater;
    private readonly Forms.NotifyIcon _trayIcon;
    private Uri? _allowedOrigin;
    private bool _webViewInitialized;
    private bool _isExiting;
    private bool _trayHintShown;
    private bool _settingsInitialized;
    private bool _hasPageTheme;

    public MainWindow(
        DshSettings settings,
        SettingsStore settingsStore,
        LogService log,
        DshProcessManager processManager)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _log = log;
        _processManager = processManager;
        _updater = new DshUpdater(settings, log);

        InitializeComponent();
        CloseToTrayCheckBox.IsChecked = settings.CloseToTray;
        _settingsInitialized = true;
        _processManager.StateChanged += ProcessManager_OnStateChanged;
        _trayIcon = CreateTrayIcon();

        Loaded += async (_, _) => await StartDshAsync();
        Closing += MainWindow_OnClosing;
        Closed += (_, _) =>
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _processManager.StateChanged -= ProcessManager_OnStateChanged;
        };
    }

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private async Task StartDshAsync(bool restart = false)
    {
        ShowStarting("正在启动 DeepSeek Harness", restart ? "正在重新连接本机服务…" : "正在检查本机环境…");
        try
        {
            EnsureWebViewRuntimeAvailable();
            var url = restart
                ? await _processManager.RestartAsync()
                : await _processManager.StartOrAttachAsync();
            await NavigateToDshAsync(url);
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Unable to start DSH Desk");
            ShowFailure(exception.Message);
        }
    }

    private static void EnsureWebViewRuntimeAvailable()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new InvalidOperationException();
            }
        }
        catch
        {
            throw new InvalidOperationException(
                "未检测到 Microsoft Edge WebView2 Runtime。请安装 Evergreen WebView2 Runtime 后重试。");
        }
    }

    private async Task NavigateToDshAsync(Uri url)
    {
        if (!_webViewInitialized)
        {
            var userDataDirectory = ResolveWebViewDataDirectory();
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataDirectory);
            await HarnessWebView.EnsureCoreWebView2Async(environment);
            HarnessWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            HarnessWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            HarnessWebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
            HarnessWebView.CoreWebView2.WebMessageReceived += CoreWebView2_OnWebMessageReceived;
            await HarnessWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(DshThemeBridgeScript);
            HarnessWebView.CoreWebView2.NavigationStarting += CoreWebView2_OnNavigationStarting;
            HarnessWebView.CoreWebView2.NavigationCompleted += CoreWebView2_OnNavigationCompleted;
            HarnessWebView.CoreWebView2.NewWindowRequested += CoreWebView2_OnNewWindowRequested;
            _webViewInitialized = true;
        }

        _allowedOrigin = url;
        HarnessWebView.Visibility = Visibility.Visible;
        HarnessWebView.Source = url;
    }

    public void ApplySystemThemeFallback()
    {
        if (!_hasPageTheme)
        {
            ThemeService.ApplyCurrentTheme(System.Windows.Application.Current.Resources);
        }
    }

    private void CoreWebView2_OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!DshThemeMessageParser.TryParse(e.WebMessageAsJson, out var isLight))
        {
            return;
        }

        _hasPageTheme = true;
        ThemeService.ApplyTheme(System.Windows.Application.Current.Resources, isLight);
    }

    private string ResolveWebViewDataDirectory()
    {
        var preferred = Path.Combine(_settings.AppDataDirectory, "webview2");
        try
        {
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DSHDesk",
                "webview2");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private void CoreWebView2_OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_allowedOrigin is null ||
            !Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) ||
            DshUrlParser.IsAllowedNavigation(target, _allowedOrigin))
        {
            return;
        }

        e.Cancel = true;
        OpenExternalUrl(target);
    }

    private async void CoreWebView2_OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            try
            {
                await HarnessWebView.CoreWebView2.ExecuteScriptAsync(
                    "window.__dshDeskPublishTheme && window.__dshDeskPublishTheme();");
            }
            catch (Exception exception)
            {
                _log.Error(exception, "Unable to synchronize DSH page theme");
            }

            LaunchOverlay.Visibility = Visibility.Collapsed;
            HarnessWebView.Visibility = Visibility.Visible;
            return;
        }

        ShowFailure($"DSH 页面加载失败：{e.WebErrorStatus}");
    }

    private void CoreWebView2_OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target))
        {
            OpenExternalUrl(target);
        }
    }

    private static void OpenExternalUrl(Uri target)
    {
        if (target.Scheme is not ("http" or "https"))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
    }

    private void ProcessManager_OnStateChanged(object? sender, DshStateChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = e.State switch
            {
                DshRuntimeState.Ready => "DSH 已连接",
                DshRuntimeState.Attached => "已连接现有 DSH",
                DshRuntimeState.Faulted => "DSH 启动失败",
                DshRuntimeState.Stopped => "DSH 已停止",
                _ => "正在启动"
            };
            StatusDot.Fill = e.State switch
            {
                DshRuntimeState.Ready => new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 180, 126)),
                DshRuntimeState.Attached => new SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 139, 219)),
                DshRuntimeState.Faulted => new SolidColorBrush(System.Windows.Media.Color.FromRgb(196, 43, 28)),
                _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(214, 153, 61))
            };
            PopupStatusText.Text = e.Message;
            PopupAddressText.Text = e.Url is null ? string.Empty : $"地址：{e.Url.Authority}";
            PopupVersionText.Text = string.IsNullOrWhiteSpace(_processManager.CurrentVersion)
                ? (e.State == DshRuntimeState.Attached ? "来源：外部进程" : string.Empty)
                : $"版本：{_processManager.CurrentVersion} · {(e.IsOwned ? "DSH Desk 管理" : "外部进程")}";
            _trayIcon.Text = StatusText.Text.Length <= 63 ? $"DSH Desk - {StatusText.Text}" : "DSH Desk";

            if (e.State is DshRuntimeState.Checking or DshRuntimeState.Starting)
            {
                StartupDetail.Text = e.Message;
            }
        });
    }

    private void ShowStarting(string title, string detail)
    {
        HarnessWebView.Visibility = Visibility.Hidden;
        LaunchOverlay.Visibility = Visibility.Visible;
        StartupTitle.Text = title;
        StartupDetail.Text = detail;
        StartupProgress.Visibility = Visibility.Visible;
        ErrorActions.Visibility = Visibility.Collapsed;
    }

    private void ShowFailure(string message)
    {
        HarnessWebView.Visibility = Visibility.Hidden;
        LaunchOverlay.Visibility = Visibility.Visible;
        StartupTitle.Text = "DSH Desk 无法启动";
        StartupDetail.Text = message;
        StartupProgress.Visibility = Visibility.Collapsed;
        ErrorActions.Visibility = Visibility.Visible;
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 DSH Desk", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("重新连接 / 重启 DSH", null, (_, _) => Dispatcher.InvokeAsync(async () => await StartDshAsync(true)));
        menu.Items.Add("查看日志", null, (_, _) => Dispatcher.Invoke(OpenLogDirectory));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("彻底退出", null, (_, _) => Dispatcher.InvokeAsync(ExitApplicationAsync));

        var executableIcon = Environment.ProcessPath is { } executablePath
            ? System.Drawing.Icon.ExtractAssociatedIcon(executablePath)
            : null;
        var tray = new Forms.NotifyIcon
        {
            Icon = executableIcon ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone(),
            Text = "DSH Desk",
            Visible = true,
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        return tray;
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        if (_settings.CloseToTray)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayIcon.ShowBalloonTip(
                    2500,
                    "DSH Desk 仍在运行",
                    "双击托盘图标可重新打开；彻底退出会停止由 DSH Desk 启动的服务。",
                    Forms.ToolTipIcon.Info);
                _trayHintShown = true;
            }
            return;
        }

        _ = ExitApplicationAsync();
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        SettingsPopup.IsOpen = false;
        StatusPopup.IsOpen = false;
        _trayIcon.Visible = false;
        await _processManager.StopOwnedAsync();
        System.Windows.Application.Current.Shutdown();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void StatusButton_OnClick(object sender, RoutedEventArgs e) => StatusPopup.IsOpen = !StatusPopup.IsOpen;

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private async void RetryButton_OnClick(object sender, RoutedEventArgs e) => await StartDshAsync();

    private async void RestartButton_OnClick(object sender, RoutedEventArgs e)
    {
        StatusPopup.IsOpen = false;
        await StartDshAsync(true);
    }

    private void OpenLogsButton_OnClick(object sender, RoutedEventArgs e) => OpenLogDirectory();

    private void OpenLogDirectory()
    {
        Directory.CreateDirectory(_log.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _log.LogDirectory) { UseShellExecute = true });
    }

    private void OpenDataButton_OnClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.AppDataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _settings.AppDataDirectory) { UseShellExecute = true });
    }

    private async void UpdateDshButton_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        var result = System.Windows.MessageBox.Show(
            "将通过官方 npm 包 @deepseek-ai/dsh@latest 更新本机缓存。继续吗？",
            "更新官方 DSH",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        ShowStarting("正在更新官方 DSH", "正在访问 npm 官方包源，请稍候…");
        try
        {
            var version = await _updater.UpdateOfficialPackageAsync();
            _log.Info($"Update completed: {version}");
            await StartDshAsync(true);
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Official DSH update failed");
            ShowFailure(exception.Message);
        }
    }

    private void CloseToTrayCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsInitialized)
        {
            return;
        }

        _settings.CloseToTray = CloseToTrayCheckBox.IsChecked == true;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Unable to save settings");
        }
    }

    private async void ExitButton_OnClick(object sender, RoutedEventArgs e) => await ExitApplicationAsync();
}
