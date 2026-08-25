using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DshDesk.Models;
using DshDesk.Services;
using Microsoft.Web.WebView2.Core;
using Forms = System.Windows.Forms;

namespace DshDesk;

public partial class MainWindow : Window
{
    private const string InstallCommand = "npm install --global @deepseek-ai/dsh";
    private const string DshUpdateCommand = "npm install --global @deepseek-ai/dsh@latest";

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
    private readonly UpdateCheckService _updateCheckService;
    private readonly DispatcherTimer _updateCheckTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly WindowWorkAreaMaximizer _windowWorkAreaMaximizer;
    private Uri? _allowedOrigin;
    private bool _webViewInitialized;
    private bool _isExiting;
    private bool _trayHintShown;
    private bool _settingsInitialized;
    private bool _hasPageTheme;
    private bool _dshOperationInProgress;
    private bool _updateCheckInProgress;
    private bool _updateNotificationShown;
    private bool _updatingStartupRegistration;
    private Forms.ToolStripMenuItem? _trayCopyAddressItem;
    private Forms.ToolStripMenuItem? _trayOpenInBrowserItem;
    private UpdateCheckResult? _lastUpdateCheck;
    private string _draftDshPackageDirectory = string.Empty;
    private string _draftWorkspaceDirectory = string.Empty;
    private bool _pendingRestoreDrag;
    private System.Windows.Point _restoreDragOffset;

    public MainWindow(
        DshSettings settings,
        SettingsStore settingsStore,
        LogService log,
        DshProcessManager processManager,
        bool startInBackground = false)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _log = log;
        _processManager = processManager;
        _updateCheckService = new UpdateCheckService();
        _updateCheckTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = UpdateCheckService.CheckInterval
        };
        _updateCheckTimer.Tick += UpdateCheckTimer_OnTick;

        InitializeComponent();
        _windowWorkAreaMaximizer = new WindowWorkAreaMaximizer(this);
        SourceInitialized += (_, _) => WindowPlacementService.Restore(this, settings.WindowPlacement);
        SizeChanged += (_, _) => UpdateSettingsDrawerSize();
        PreviewKeyDown += MainWindow_OnPreviewKeyDown;
        CloseToTrayCheckBox.IsChecked = settings.CloseToTray;
        LaunchAtLoginCheckBox.IsChecked = StartupRegistrationService.IsEnabledForCurrentExecutable();
        _settingsInitialized = true;
        _processManager.StateChanged += ProcessManager_OnStateChanged;
        _trayIcon = CreateTrayIcon();

        Loaded += async (_, _) =>
        {
            StartUpdateCheckSchedule();
            await StartDshAsync(
                attachExistingOverride: startInBackground ? true : null,
                navigate: !startInBackground);
        };
        Closing += MainWindow_OnClosing;
        Closed += (_, _) =>
        {
            _windowWorkAreaMaximizer.Dispose();
            _updateCheckTimer.Stop();
            _updateCheckTimer.Tick -= UpdateCheckTimer_OnTick;
            _updateCheckService.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _processManager.StateChanged -= ProcessManager_OnStateChanged;
        };
    }

    public void ShowInBackground()
    {
        ShowActivated = false;
        ShowInTaskbar = false;
        Opacity = 0;
        Show();
        Hide();
        Opacity = 1;
        ShowActivated = true;
        ShowInTaskbar = true;
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
        _ = EnsureVisibleContentAsync();
    }

    private async Task StartDshAsync(
        bool restart = false,
        bool? attachExistingOverride = null,
        bool navigate = true)
    {
        if (_dshOperationInProgress)
        {
            return;
        }

        _dshOperationInProgress = true;
        try
        {
            var attachExisting = attachExistingOverride ?? true;
            if (attachExistingOverride is null && _settings.InstallationMode == DshInstallationMode.SpecifiedPath)
            {
                var existingHealthy = await _processManager.IsHealthyDshAsync(_processManager.AttachUrl);
                var choice = existingHealthy ? ShowExistingDshDialog() : ExistingDshChoice.LaunchSpecified;
                var decision = DshLaunchPolicy.Decide(_settings.InstallationMode, existingHealthy, choice);
                if (!decision.ApplyChanges)
                {
                    return;
                }

                attachExisting = decision.AttachExisting;
            }

            var keepCurrentContent = navigate && restart &&
                                     _webViewInitialized &&
                                     HarnessWebView.Visibility == Visibility.Visible &&
                                     LaunchOverlay.Visibility == Visibility.Collapsed;
            var wasOwnedProcess = _processManager.OwnsCurrentProcess;
            if (!keepCurrentContent)
            {
                ShowStarting("正在启动 DeepSeek Harness", restart ? "正在重新连接本机服务…" : "正在检查本机环境…");
            }

            try
            {
                var url = restart
                    ? await _processManager.RestartAsync(attachExisting)
                    : await _processManager.StartOrAttachAsync(attachExisting);
                if (navigate || IsVisible)
                {
                    EnsureWebViewRuntimeAvailable();
                    await NavigateToDshAsync(
                        url,
                        keepCurrentPageIfSame: keepCurrentContent && !wasOwnedProcess);
                }
            }
            catch (Exception exception)
            {
                _log.Error(exception, "Unable to start DSH Desk");
                ShowFailure(exception);
            }
        }
        finally
        {
            _dshOperationInProgress = false;
        }
    }

    private async Task EnsureVisibleContentAsync()
    {
        if (_webViewInitialized && HarnessWebView.Visibility == Visibility.Visible)
        {
            return;
        }

        if (_processManager.CurrentUrl is { } currentUrl &&
            _processManager.State is DshRuntimeState.Ready or DshRuntimeState.Attached)
        {
            try
            {
                ShowStarting("正在打开 DeepSeek Harness", "正在加载本机服务页面…");
                EnsureWebViewRuntimeAvailable();
                await NavigateToDshAsync(currentUrl);
            }
            catch (Exception exception)
            {
                _log.Error(exception, "Unable to initialize DSH page");
                ShowFailure(exception);
            }

            return;
        }

        await StartDshAsync();
    }

    private ExistingDshChoice ShowExistingDshDialog()
    {
        var dialog = new ExistingDshDialog { Owner = this };
        dialog.ShowDialog();
        return dialog.Choice;
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

    private async Task NavigateToDshAsync(Uri url, bool keepCurrentPageIfSame = false)
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
        if (keepCurrentPageIfSame && HarnessWebView.Source == url)
        {
            return;
        }

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
        Directory.CreateDirectory(AppPaths.WebViewDataDirectory);
        return AppPaths.WebViewDataDirectory;
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
                DshRuntimeState.Ready when _processManager.CurrentInstallation is { } installation =>
                    DshInstallationStatusText.FormatRunningStatus(installation),
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
            var currentInstallation = _processManager.CurrentInstallation;
            PopupVersionText.Text = currentInstallation is not null
                ? DshInstallationStatusText.FormatRunningDetails(currentInstallation)
                : e.State == DshRuntimeState.Attached
                    ? "来源：外部服务"
                    : string.Empty;
            UpdateRuntimeEnvironmentSummary();
            _trayIcon.Text = StatusText.Text.Length <= 63 ? $"DSH Desk - {StatusText.Text}" : "DSH Desk";
            UpdateAddressActions();

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
        ShowFailureCore(message, false);
    }

    private void ShowFailure(Exception exception)
    {
        ShowFailureCore(exception.Message, exception is DshInstallationNotFoundException);
    }

    private void ShowFailureCore(string message, bool installationMissing)
    {
        HarnessWebView.Visibility = Visibility.Hidden;
        LaunchOverlay.Visibility = Visibility.Visible;
        StartupTitle.Text = installationMissing ? "未找到可用的 DSH" : "DSH Desk 无法启动";
        StartupDetail.Text = installationMissing
            ? $"{message}{Environment.NewLine}{Environment.NewLine}安装命令：{InstallCommand}"
            : message;
        StartupProgress.Visibility = Visibility.Collapsed;
        MissingInstallationActions.Visibility = installationMissing ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = installationMissing ? Visibility.Collapsed : Visibility.Visible;
        DetectAgainButton.Visibility = installationMissing ? Visibility.Visible : Visibility.Collapsed;
        ErrorActions.Visibility = Visibility.Visible;
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 DSH Desk", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("重新连接 / 重启 DSH", null, (_, _) => Dispatcher.InvokeAsync(async () => await StartDshAsync(true)));
        _trayCopyAddressItem = new Forms.ToolStripMenuItem(
            "复制本地地址",
            null,
            (_, _) => Dispatcher.Invoke(CopyCurrentAddress))
        {
            Enabled = false
        };
        _trayOpenInBrowserItem = new Forms.ToolStripMenuItem(
            "在浏览器中打开",
            null,
            (_, _) => Dispatcher.Invoke(OpenCurrentAddressInBrowser))
        {
            Enabled = false
        };
        menu.Items.Add(_trayCopyAddressItem);
        menu.Items.Add(_trayOpenInBrowserItem);
        menu.Items.Add("查看日志", null, (_, _) => Dispatcher.Invoke(OpenLogDirectory));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("彻底退出", null, (_, _) => Dispatcher.InvokeAsync(ExitApplicationAsync));

        var executableIcon = Environment.ProcessPath is { } executablePath
            ? LoadTrayIcon(executablePath)
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

    private static System.Drawing.Icon? LoadTrayIcon(string executablePath)
    {
        if (ExtractIconEx(executablePath, 0, out _, out var smallHandle, 1) == 0 || smallHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(smallHandle).Clone();
        }
        finally
        {
            DestroyIcon(smallHandle);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file,
        int iconIndex,
        out IntPtr largeIcon,
        out IntPtr smallIcon,
        uint icons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        SaveWindowPlacement();
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

        SaveWindowPlacement();
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
            CancelRestoreDrag();
            ToggleMaximize();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            // Delay the restore until the pointer actually moves, so a plain
            // click on the maximized caption does not restore the window.
            _pendingRestoreDrag = true;
            _restoreDragOffset = e.GetPosition(TitleBar);
            TitleBar.CaptureMouse();
            return;
        }

        DragMove();
    }

    private void TitleBar_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_pendingRestoreDrag)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelRestoreDrag();
            return;
        }

        var current = e.GetPosition(TitleBar);
        var movedHorizontally = Math.Abs(current.X - _restoreDragOffset.X) >= SystemParameters.MinimumHorizontalDragDistance;
        var movedVertically = Math.Abs(current.Y - _restoreDragOffset.Y) >= SystemParameters.MinimumVerticalDragDistance;
        if (!movedHorizontally && !movedVertically)
        {
            return;
        }

        _pendingRestoreDrag = false;
        TitleBar.ReleaseMouseCapture();
        RestoreAndDrag(_restoreDragOffset);
    }

    private void TitleBar_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CancelRestoreDrag();

    private void CancelRestoreDrag()
    {
        if (!_pendingRestoreDrag)
        {
            return;
        }

        _pendingRestoreDrag = false;
        TitleBar.ReleaseMouseCapture();
    }

    private void RestoreAndDrag(System.Windows.Point pointerOffset)
    {
        if (!WindowDragRestore.TryGetRestorePosition(this, TitleBar, pointerOffset, out var position))
        {
            return;
        }

        WindowState = WindowState.Normal;
        Left = position.Left;
        Top = position.Top;
        DragMove();
    }

    private void StatusButton_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        var open = !StatusPopup.IsOpen;
        if (open)
        {
            StatusPopup.HorizontalOffset = (StatusButton.ActualWidth - StatusPopupCard.Width) / 2;
        }

        StatusPopup.IsOpen = open;
    }

    private void StatusPopup_OnOpened(object sender, EventArgs e) =>
        StatusButton.Background = (System.Windows.Media.Brush)FindResource("SurfaceBackground");

    private void StatusPopup_OnClosed(object sender, EventArgs e) =>
        StatusButton.Background = System.Windows.Media.Brushes.Transparent;

    private async void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        StatusPopup.IsOpen = false;
        if (SettingsPopup.IsOpen)
        {
            SettingsPopup.IsOpen = false;
            return;
        }

        _updatingStartupRegistration = true;
        LaunchAtLoginCheckBox.IsChecked = StartupRegistrationService.IsEnabledForCurrentExecutable();
        _updatingStartupRegistration = false;
        SetEnvironmentConfigurationExpanded(false);
        LoadSettingsDraft();
        SettingsPopup.IsOpen = true;
        await RefreshDetectedInstallationAsync();
    }

    private void SettingsPopup_OnOpened(object sender, EventArgs e)
    {
        UpdateSettingsDrawerSize();
        SettingsButton.Background = (System.Windows.Media.Brush)FindResource("SurfaceBackground");
    }

    private void SettingsPopup_OnClosed(object sender, EventArgs e) =>
        SettingsButton.Background = System.Windows.Media.Brushes.Transparent;

    private void CloseSettingsButton_OnClick(object sender, RoutedEventArgs e) =>
        SettingsPopup.IsOpen = false;

    private void ChangeRuntimeEnvironmentButton_OnClick(object sender, RoutedEventArgs e) =>
        SetEnvironmentConfigurationExpanded(
            EnvironmentConfigurationPanel.Visibility != Visibility.Visible);

    private void SetEnvironmentConfigurationExpanded(bool expanded)
    {
        EnvironmentConfigurationPanel.Visibility = expanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        ChangeRuntimeEnvironmentButton.Content = expanded
            ? "收起运行环境设置"
            : "更改运行环境";
    }

    private void UpdateRuntimeEnvironmentSummary()
    {
        var state = _processManager.State;
        var installation = _processManager.CurrentInstallation;
        EnvironmentStatusDot.Fill = StatusDot.Fill;
        EnvironmentConnectionText.Text = state switch
        {
            DshRuntimeState.Ready or DshRuntimeState.Attached => "已连接",
            DshRuntimeState.Checking => "正在检查",
            DshRuntimeState.Starting => "正在启动",
            DshRuntimeState.Faulted => "连接失败",
            _ => "已停止"
        };

        if (installation is not null)
        {
            EnvironmentVersionText.Text = $"DSH {installation.Version}";
            EnvironmentSourceText.Text = installation.Source == DshInstallationSource.System
                ? "系统安装"
                : "指定路径";
            EnvironmentPathText.Text = installation.PackageDirectory;
            return;
        }

        EnvironmentVersionText.Text = _processManager.CurrentVersion is { Length: > 0 } version
            ? $"DSH {version}"
            : state == DshRuntimeState.Attached
                ? "外部 DSH"
                : "DSH";
        EnvironmentSourceText.Text = state == DshRuntimeState.Attached
            ? "外部服务"
            : _settings.InstallationMode == DshInstallationMode.SpecifiedPath
                ? "指定路径"
                : "自动检测";
        EnvironmentPathText.Text = _processManager.CurrentUrl?.Authority ?? "尚未连接";
    }

    private void UpdateSettingsDrawerSize()
    {
        if (!IsLoaded)
        {
            return;
        }

        SettingsDrawer.Height = Math.Max(320, ActualHeight - TitleBar.ActualHeight - 8);
    }

    private void MainWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (SettingsPopup.IsOpen || StatusPopup.IsOpen)
        {
            SettingsPopup.IsOpen = false;
            StatusPopup.IsOpen = false;
            e.Handled = true;
        }
    }

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

    private void CopyAddressButton_OnClick(object sender, RoutedEventArgs e) => CopyCurrentAddress();

    private void OpenInBrowserButton_OnClick(object sender, RoutedEventArgs e) => OpenCurrentAddressInBrowser();

    private void OpenLogsButton_OnClick(object sender, RoutedEventArgs e) => OpenLogDirectory();

    private void OpenLogDirectory()
    {
        Directory.CreateDirectory(_log.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _log.LogDirectory) { UseShellExecute = true });
    }

    private void OpenDataButton_OnClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory) { UseShellExecute = true });
    }

    private void InstallationModeRadioButton_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsInitialized)
        {
            return;
        }

        SpecifiedInstallationPanel.IsEnabled = SpecifiedPathRadioButton.IsChecked == true;
    }

    private void SelectDshPackageButton_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "请选择包含 package.json 的 @deepseek-ai/dsh 包目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(_draftDshPackageDirectory)
                ? _draftDshPackageDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            SettingsPopup.IsOpen = true;
            return;
        }

        if (!DshPackageLocator.TryValidatePackageDirectory(
                dialog.SelectedPath,
                DshInstallationSource.Specified,
                out var installation,
                out var error))
        {
            System.Windows.MessageBox.Show(
                error,
                "无效的 DSH 安装目录",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SettingsPopup.IsOpen = true;
            return;
        }

        _draftDshPackageDirectory = installation!.PackageDirectory;
        SpecifiedInstallationPathText.Text =
            $"版本：{installation.Version}{Environment.NewLine}{installation.PackageDirectory}";
        SettingsPopup.IsOpen = true;
    }

    private void SelectWorkspaceButton_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "请选择 DSH Web 使用的默认 Workspace",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(_draftWorkspaceDirectory)
                ? _draftWorkspaceDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            SettingsPopup.IsOpen = true;
            return;
        }

        _draftWorkspaceDirectory = Path.GetFullPath(dialog.SelectedPath);
        WorkspacePathText.Text = _draftWorkspaceDirectory;
        SettingsPopup.IsOpen = true;
    }

    private async void SaveAndReconnectButton_OnClick(object sender, RoutedEventArgs e)
    {
        var workspaceDirectory = _draftWorkspaceDirectory.Trim();
        if (string.IsNullOrWhiteSpace(workspaceDirectory) || !Directory.Exists(workspaceDirectory))
        {
            System.Windows.MessageBox.Show(
                $"Workspace 不存在：{workspaceDirectory}",
                "无法保存设置",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SettingsPopup.IsOpen = true;
            return;
        }

        var mode = SpecifiedPathRadioButton.IsChecked == true
            ? DshInstallationMode.SpecifiedPath
            : DshInstallationMode.AutoDetect;
        if (mode == DshInstallationMode.SpecifiedPath &&
            !DshPackageLocator.TryValidatePackageDirectory(
                _draftDshPackageDirectory,
                DshInstallationSource.Specified,
                out _,
                out var packageError))
        {
            System.Windows.MessageBox.Show(
                packageError,
                "无法保存设置",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SettingsPopup.IsOpen = true;
            return;
        }

        bool? attachExistingOverride = null;
        if (mode == DshInstallationMode.SpecifiedPath)
        {
            var existingHealthy = await _processManager.IsHealthyDshAsync(_processManager.AttachUrl);
            SettingsPopup.IsOpen = false;
            var choice = existingHealthy ? ShowExistingDshDialog() : ExistingDshChoice.LaunchSpecified;
            var decision = DshLaunchPolicy.Decide(mode, existingHealthy, choice);
            if (!decision.ApplyChanges)
            {
                SettingsPopup.IsOpen = true;
                return;
            }

            attachExistingOverride = decision.AttachExisting;
        }

        var updatedSettings = new DshSettings
        {
            InstallationMode = mode,
            DshPackageDirectory = _draftDshPackageDirectory,
            WorkspaceDirectory = Path.GetFullPath(workspaceDirectory),
            CloseToTray = _settings.CloseToTray,
            AttachPort = _settings.AttachPort,
            StartupTimeoutSeconds = _settings.StartupTimeoutSeconds,
            WindowPlacement = _settings.WindowPlacement,
            LastUpdateCheckUtc = _settings.LastUpdateCheckUtc
        };
        try
        {
            _settingsStore.Save(updatedSettings);
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Unable to save DSH launch settings");
            System.Windows.MessageBox.Show(
                exception.Message,
                "无法保存设置",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SettingsPopup.IsOpen = true;
            return;
        }

        _settings.InstallationMode = updatedSettings.InstallationMode;
        _settings.DshPackageDirectory = updatedSettings.DshPackageDirectory;
        _settings.WorkspaceDirectory = updatedSettings.WorkspaceDirectory;
        SettingsPopup.IsOpen = false;
        await StartDshAsync(true, attachExistingOverride);
    }

    private async void ChooseInstallationButton_OnClick(object sender, RoutedEventArgs e)
    {
        LoadSettingsDraft(forceSpecifiedPath: true);
        SetEnvironmentConfigurationExpanded(true);
        SettingsPopup.IsOpen = true;
        await RefreshDetectedInstallationAsync();
    }

    private void CopyInstallCommandButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(InstallCommand);
            StartupDetail.Text = $"安装命令已复制：{InstallCommand}";
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Unable to copy DSH install command");
            System.Windows.MessageBox.Show(
                InstallCommand,
                "请手动复制安装命令",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void LoadSettingsDraft(bool forceSpecifiedPath = false)
    {
        _draftDshPackageDirectory = _settings.DshPackageDirectory;
        _draftWorkspaceDirectory = string.IsNullOrWhiteSpace(_settings.WorkspaceDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : _settings.WorkspaceDirectory;

        var specified = forceSpecifiedPath || _settings.InstallationMode == DshInstallationMode.SpecifiedPath;
        AutoDetectRadioButton.IsChecked = !specified;
        SpecifiedPathRadioButton.IsChecked = specified;
        SpecifiedInstallationPanel.IsEnabled = specified;
        SpecifiedInstallationPathText.Text = string.IsNullOrWhiteSpace(_draftDshPackageDirectory)
            ? "尚未选择 @deepseek-ai/dsh 包目录"
            : _draftDshPackageDirectory;
        WorkspacePathText.Text = _draftWorkspaceDirectory;
        UpdateRuntimeEnvironmentSummary();
    }

    private async Task RefreshDetectedInstallationAsync()
    {
        DetectedInstallationText.Text = "正在检测系统 DSH…";
        var installation = await Task.Run(() => DshPackageLocator.FindSystemInstallation());
        DetectedInstallationText.Text = DshInstallationStatusText.FormatSystemDetection(
            installation,
            _processManager.CurrentInstallation,
            InstallCommand);
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

    private void LaunchAtLoginCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsInitialized || _updatingStartupRegistration)
        {
            return;
        }

        var enabled = LaunchAtLoginCheckBox.IsChecked == true;
        try
        {
            StartupRegistrationService.SetEnabled(enabled);
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Unable to update Windows startup registration");
            _updatingStartupRegistration = true;
            LaunchAtLoginCheckBox.IsChecked = !enabled;
            _updatingStartupRegistration = false;
            System.Windows.MessageBox.Show(
                exception.Message,
                "无法修改开机启动",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void CheckUpdatesButton_OnClick(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(showNotification: false);

    private void OpenDeskReleaseButton_OnClick(object sender, RoutedEventArgs e)
    {
        var url = _lastUpdateCheck?.DshDesk.MoreInfoUrl ?? UpdateCheckService.DeskReleasesUrl;
        OpenExternalUrl(url);
    }

    private void CopyDshUpdateCommandButton_OnClick(object sender, RoutedEventArgs e)
    {
        CopyTextWithFallback(DshUpdateCommand, "DSH 更新命令");
    }

    private void SaveWindowPlacement()
    {
        var placement = WindowPlacementService.Capture(this);
        if (placement is null)
        {
            return;
        }

        _settings.WindowPlacement = placement;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Unable to save window placement");
        }
    }

    private void UpdateAddressActions()
    {
        var enabled = TryGetCurrentAddress(out _);
        CopyAddressButton.IsEnabled = enabled;
        OpenInBrowserButton.IsEnabled = enabled;
        if (_trayCopyAddressItem is not null) _trayCopyAddressItem.Enabled = enabled;
        if (_trayOpenInBrowserItem is not null) _trayOpenInBrowserItem.Enabled = enabled;
    }

    private bool TryGetCurrentAddress(out Uri? url)
    {
        url = _processManager.CurrentUrl;
        return _processManager.State is DshRuntimeState.Ready or DshRuntimeState.Attached &&
               url is not null &&
               url.Scheme == Uri.UriSchemeHttp &&
               IPAddress.TryParse(url.Host, out var address) &&
               IPAddress.IsLoopback(address);
    }

    private void CopyCurrentAddress()
    {
        if (!TryGetCurrentAddress(out var url))
        {
            return;
        }

        CopyTextWithFallback(url!.AbsoluteUri, "DSH 本地地址");
    }

    private void OpenCurrentAddressInBrowser()
    {
        if (TryGetCurrentAddress(out var url))
        {
            OpenExternalUrl(url!);
        }
    }

    private void CopyTextWithFallback(string text, string title)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception exception)
        {
            _log.Error(exception, $"Unable to copy {title}");
            System.Windows.MessageBox.Show(
                text,
                $"请手动复制{title}",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void StartUpdateCheckSchedule()
    {
        var delay = UpdateCheckService.CalculateNextCheckDelay(
            _settings.LastUpdateCheckUtc,
            DateTimeOffset.UtcNow);
        if (delay == TimeSpan.Zero)
        {
            _ = CheckForUpdatesAsync(showNotification: true);
            return;
        }

        ScheduleNextUpdateCheck(delay);
        var lastLocal = _settings.LastUpdateCheckUtc!.Value.ToLocalTime();
        DeskUpdateText.Text = $"DSH Desk：上次检查 {lastLocal:MM-dd HH:mm}";
        DshUpdateText.Text = $"系统 DSH：将在约 {FormatDelay(delay)}后自动检查";
    }

    private void ScheduleNextUpdateCheck(TimeSpan delay)
    {
        _updateCheckTimer.Stop();
        _updateCheckTimer.Interval = delay <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : delay;
        _updateCheckTimer.Start();
    }

    private async void UpdateCheckTimer_OnTick(object? sender, EventArgs e)
    {
        _updateCheckTimer.Stop();
        await CheckForUpdatesAsync(showNotification: true);
    }

    private static string FormatDelay(TimeSpan delay)
    {
        if (delay.TotalHours >= 1)
        {
            return $"{Math.Ceiling(delay.TotalHours):0} 小时";
        }

        return $"{Math.Max(1, Math.Ceiling(delay.TotalMinutes)):0} 分钟";
    }

    private async Task CheckForUpdatesAsync(bool showNotification)
    {
        if (_updateCheckInProgress)
        {
            return;
        }

        _updateCheckInProgress = true;
        _updateCheckTimer.Stop();
        CheckUpdatesButton.IsEnabled = false;
        DeskUpdateText.Text = $"DSH Desk：当前 {UpdateCheckService.CurrentDeskVersion}，正在检查…";
        DshUpdateText.Text = "系统 DSH：正在检查…";
        try
        {
            var systemInstallation = await Task.Run(() => DshPackageLocator.FindSystemInstallation());
            var result = await _updateCheckService.CheckAsync(systemInstallation?.Version);
            _lastUpdateCheck = result;

            DeskUpdateText.Text = FormatUpdateStatus("DSH Desk", result.DshDesk);
            DshUpdateText.Text = FormatUpdateStatus("系统 DSH", result.SystemDsh);
            OpenDeskReleaseButton.Visibility = result.DshDesk.Availability == UpdateAvailability.Available
                ? Visibility.Visible
                : Visibility.Collapsed;
            CopyDshUpdateCommandButton.Visibility = result.SystemDsh.Availability == UpdateAvailability.Available
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateBadge.Visibility = result.HasUpdate ? Visibility.Visible : Visibility.Collapsed;

            if (showNotification && result.HasUpdate && !_updateNotificationShown)
            {
                var products = new List<string>();
                if (result.DshDesk.Availability == UpdateAvailability.Available) products.Add("DSH Desk");
                if (result.SystemDsh.Availability == UpdateAvailability.Available) products.Add("系统 DSH");
                _trayIcon.ShowBalloonTip(
                    3500,
                    "发现新版本",
                    $"{string.Join("、", products)} 有可用更新。打开 DSH Desk 设置查看详情。",
                    Forms.ToolTipIcon.Info);
                _updateNotificationShown = true;
            }

            if (result.DshDesk.Availability == UpdateAvailability.Failed)
            {
                _log.Info($"DSH Desk update check failed: {result.DshDesk.Error}");
            }
            if (result.SystemDsh.Availability == UpdateAvailability.Failed)
            {
                _log.Info($"System DSH update check failed: {result.SystemDsh.Error}");
            }
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Unable to check for updates");
            DeskUpdateText.Text = $"DSH Desk：当前 {UpdateCheckService.CurrentDeskVersion}，检查失败";
            DshUpdateText.Text = "系统 DSH：检查失败";
        }
        finally
        {
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            try
            {
                _settingsStore.Save(_settings);
            }
            catch (Exception exception)
            {
                _log.Error(exception, "Unable to save update check time");
            }

            _updateCheckInProgress = false;
            CheckUpdatesButton.IsEnabled = true;
            if (!_isExiting)
            {
                ScheduleNextUpdateCheck(UpdateCheckService.CheckInterval);
            }
        }
    }

    private static string FormatUpdateStatus(string productName, ProductUpdateStatus status) =>
        status.Availability switch
        {
            UpdateAvailability.Available =>
                $"{productName}：{status.CurrentVersion} → {status.LatestVersion}，有新版本",
            UpdateAvailability.Current when string.Equals(status.CurrentVersion, status.LatestVersion, StringComparison.OrdinalIgnoreCase) =>
                $"{productName}：{status.CurrentVersion}，已是最新版本",
            UpdateAvailability.Current =>
                $"{productName}：当前 {status.CurrentVersion}，latest 为 {status.LatestVersion}，无需更新",
            UpdateAvailability.Unavailable =>
                $"{productName}：未检测到系统安装",
            _ =>
                $"{productName}：当前 {status.CurrentVersion}，检查失败"
        };

    private async void ExitButton_OnClick(object sender, RoutedEventArgs e) => await ExitApplicationAsync();
}
