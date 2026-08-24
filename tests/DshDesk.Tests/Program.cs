using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DshDesk.Models;
using DshDesk.Services;

namespace DshDesk.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("解析 DSH 启动地址", TestReadyLineParser),
            ("限制 WebView2 导航来源", TestNavigationPolicy),
            ("读写新版设置", TestSettingsRoundTrip),
            ("配置文件缺失端口时默认 3080", TestSettingsMissingPortDefaultsTo3080),
            ("比较语义化版本与预发布版本", TestSemanticVersionComparison),
            ("解析更新源响应", TestUpdateResponseParsing),
            ("计算 24 小时更新检查周期", TestUpdateCheckSchedule),
            ("校正离屏窗口位置", TestWindowPlacementNormalization),
            ("构造 Windows 登录启动命令", TestStartupRegistrationCommand),
            ("默认目录不再使用 G 盘", TestDefaultPaths),
            ("解析 DSH 页面主题", TestThemeMessageParser),
            ("计算窗口最大化工作区", TestWindowMaximizeBounds),
            ("计算最大化窗口拖动还原位置", TestWindowDragRestorePosition),
            ("验证官方 DSH 包目录", TestPackageValidation),
            ("拒绝损坏或越界的 DSH 包", TestInvalidPackages),
            ("PATH 中的 DSH 按顺序优先", TestPathDiscoveryPrecedence),
            ("回退标准 npm 全局目录", TestStandardNpmDiscovery),
            ("回退 npm root 全局目录", TestNpmRootDiscovery),
            ("区分运行中 DSH 与可用安装", TestInstallationStatusText),
            ("指定模式不回退自动检测", TestSpecifiedModeDoesNotFallback),
            ("缺少安装时返回专用错误", TestMissingInstallationError),
            ("指定模式处理现有服务的三种选择", TestExistingServiceChoices),
            ("构造安全的 DSH 启动参数", TestProcessStartInfo),
            ("识别端口不可用原因", TestPortAvailability),
            ("优雅停止超时后强杀进程树", TestStopGracefulThenForce),
            ("ConPTY 向 Node.js 传递 Ctrl+C", TestConPtyCtrlC),
            ("ConPTY 正确转义启动参数", TestConPtyArgumentQuoting),
            ("优雅窗口内自行退出的进程不被强杀", TestSelfExitingProcessNotForceKilled),
            ("识别健康的 DeepSeek Harness 页面", TestHealthProbe),
            ("连接外部 DSH 时不终止服务", TestAttachDoesNotStopExternalService),
            ("重启自管 DSH 后仍保持自管状态", TestRestartOwnedService)
        };

        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                _passed++;
                Console.WriteLine($"[PASS] {test.Name}");
            }
            catch (Exception exception)
            {
                _failed++;
                Console.WriteLine($"[FAIL] {test.Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static Task TestReadyLineParser()
    {
        Assert(DshUrlParser.TryParseReadyLine("dsh web: http://127.0.0.1:43127", out var uri), "应识别启动行");
        Equal("http://127.0.0.1:43127/", uri!.AbsoluteUri, "启动地址不正确");
        Assert(!DshUrlParser.TryParseReadyLine("listening on 0.0.0.0:3080", out _), "不应接受非 DSH 启动行");
        return Task.CompletedTask;
    }

    private static Task TestNavigationPolicy()
    {
        var origin = new Uri("http://127.0.0.1:3080/");
        Assert(DshUrlParser.IsAllowedNavigation(new Uri("http://127.0.0.1:3080/chat/1"), origin), "同源页面应允许");
        Assert(DshUrlParser.IsAllowedNavigation(new Uri("about:blank"), origin), "about:blank 应允许");
        Assert(!DshUrlParser.IsAllowedNavigation(new Uri("http://127.0.0.1:3081/"), origin), "其他端口应阻止");
        Assert(!DshUrlParser.IsAllowedNavigation(new Uri("https://example.com/"), origin), "外部站点应阻止");
        return Task.CompletedTask;
    }

    private static Task TestSettingsRoundTrip()
    {
        WithTemporaryDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            var settings = new DshSettings
            {
                InstallationMode = DshInstallationMode.SpecifiedPath,
                DshPackageDirectory = Path.Combine(directory, "dsh"),
                WorkspaceDirectory = directory,
                CloseToTray = false,
                AttachPort = 4099,
                StartupTimeoutSeconds = 45,
                LastUpdateCheckUtc = new DateTimeOffset(2026, 8, 21, 8, 30, 0, TimeSpan.Zero),
                WindowPlacement = new WindowPlacementSettings
                {
                    Left = 100,
                    Top = 120,
                    Right = 1380,
                    Bottom = 940,
                    Maximized = true
                }
            };
            store.Save(settings);
            var loaded = store.Load();
            Equal(settings.InstallationMode, loaded.InstallationMode, "安装模式未保存");
            Equal(settings.DshPackageDirectory, loaded.DshPackageDirectory, "DSH 路径未保存");
            Equal(settings.WorkspaceDirectory, loaded.WorkspaceDirectory, "Workspace 未保存");
            Equal(settings.AttachPort, loaded.AttachPort, "端口未保存");
            Equal(settings.CloseToTray, loaded.CloseToTray, "托盘设置未保存");
            Equal(100, loaded.WindowPlacement?.Left, "窗口位置未保存");
            Equal(true, loaded.WindowPlacement?.Maximized, "窗口最大化状态未保存");
            Equal(settings.LastUpdateCheckUtc, loaded.LastUpdateCheckUtc, "更新检查时间未保存");
        });
        return Task.CompletedTask;
    }

    private static Task TestSemanticVersionComparison()
    {
        Assert(SemanticVersion.IsNewer("0.1.0-rc.8", "0.1.0-rc.7"), "rc.8 应高于 rc.7");
        Assert(SemanticVersion.IsNewer("0.1.0", "0.1.0-rc.8"), "正式版应高于预发布版");
        Assert(SemanticVersion.IsNewer("v0.1.5", "0.1.4+local"), "应忽略 v 前缀和构建元数据");
        Assert(!SemanticVersion.IsNewer("0.1.0-rc.7", "0.1.0-rc.8"), "不能提示降级");
        Assert(!SemanticVersion.IsNewer("invalid", "0.1.0"), "无效版本不能触发更新");
        return Task.CompletedTask;
    }

    private static Task TestUpdateResponseParsing()
    {
        var desk = UpdateCheckService.ParseDeskResponse(
            "0.1.4",
            """{"tag_name":"v0.1.5","html_url":"https://github.com/flycoo/dsh-desk/releases/tag/v0.1.5"}""");
        Equal(UpdateAvailability.Available, desk.Availability, "应识别 DSH Desk 新版本");
        Equal("0.1.5", desk.LatestVersion, "Release tag 解析错误");

        var dsh = UpdateCheckService.ParseDshResponse("0.1.0-rc.8", """{"version":"0.1.0-rc.7"}""");
        Equal(UpdateAvailability.Current, dsh.Availability, "latest 较旧时不应提示更新");
        return Task.CompletedTask;
    }

    private static Task TestUpdateCheckSchedule()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        Equal(TimeSpan.Zero, UpdateCheckService.CalculateNextCheckDelay(null, now), "首次启动应立即检查");
        Equal(
            TimeSpan.FromHours(1),
            UpdateCheckService.CalculateNextCheckDelay(now.AddHours(-23), now),
            "重启后应等待剩余周期");
        Equal(
            TimeSpan.Zero,
            UpdateCheckService.CalculateNextCheckDelay(now.AddHours(-25), now),
            "超过 24 小时应立即检查");
        Equal(
            TimeSpan.FromHours(24),
            UpdateCheckService.CalculateNextCheckDelay(now.AddHours(1), now),
            "系统时间回拨时不应形成负延迟");
        return Task.CompletedTask;
    }

    private static Task TestWindowPlacementNormalization()
    {
        var saved = new WindowPlacementSettings
        {
            Left = 5000,
            Top = 5000,
            Right = 6280,
            Bottom = 5820,
            Maximized = true
        };
        var workArea = new System.Drawing.Rectangle(0, 0, 1920, 1040);
        var normalized = WindowPlacementService.Normalize(saved, [workArea], workArea);
        Equal(640, normalized.Left, "离屏窗口应移回主屏右侧边界内");
        Equal(220, normalized.Top, "离屏窗口应移回主屏底部边界内");
        Equal(1920, normalized.Right, "窗口右边界错误");
        Equal(1040, normalized.Bottom, "窗口底边界错误");
        Assert(normalized.Maximized, "应保留最大化状态");
        return Task.CompletedTask;
    }

    private static Task TestStartupRegistrationCommand()
    {
        Equal(
            "\"C:\\Program Files\\DSH Desk\\DshDesk.exe\" --background",
            StartupRegistrationService.BuildCommand(@"C:\Program Files\DSH Desk\DshDesk.exe"),
            "启动项命令应正确引用程序路径");
        return Task.CompletedTask;
    }

    private static Task TestSettingsMissingPortDefaultsTo3080()
    {
        WithTemporaryDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """{"InstallationMode":0,"WorkspaceDirectory":"C:\\temp"}""");
            var store = new SettingsStore(path);
            var loaded = store.Load();
            Equal(3080, loaded.AttachPort, "配置文件缺失端口时应默认 3080");
        });
        return Task.CompletedTask;
    }

    private static Task TestDefaultPaths()
    {
        var settings = new DshSettings();
        Equal(DshInstallationMode.AutoDetect, settings.InstallationMode, "默认应自动检测");
        Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), settings.WorkspaceDirectory, "默认 Workspace 不正确");
        Assert(SettingsStore.DefaultSettingsPath.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            StringComparison.OrdinalIgnoreCase), "设置应位于 LocalAppData");
        Assert(!SettingsStore.DefaultSettingsPath.Contains(@"G:\", StringComparison.OrdinalIgnoreCase), "默认设置不能包含 G 盘");
        Assert(!AppPaths.DataDirectory.Contains(@"G:\", StringComparison.OrdinalIgnoreCase), "应用数据不能包含 G 盘");
        return Task.CompletedTask;
    }

    private static Task TestThemeMessageParser()
    {
        Assert(DshThemeMessageParser.TryParse("{\"source\":\"dsh-desk-theme\",\"theme\":\"light\"}", out var isLight) && isLight, "应识别浅色主题");
        Assert(DshThemeMessageParser.TryParse("{\"source\":\"dsh-desk-theme\",\"theme\":\"dark\"}", out isLight) && !isLight, "应识别深色主题");
        Assert(!DshThemeMessageParser.TryParse("{\"source\":\"untrusted\",\"theme\":\"light\"}", out _), "不应接受其他页面消息");
        return Task.CompletedTask;
    }

    private static Task TestPackageValidation()
    {
        WithTemporaryDirectory(directory =>
        {
            var objectBin = Path.Combine(directory, "object-bin");
            CreatePackage(objectBin, "0.1.0-rc.6");
            Assert(DshPackageLocator.TryValidatePackageDirectory(
                objectBin,
                DshInstallationSource.System,
                out var systemInstallation,
                out var systemError), systemError);
            Equal("0.1.0-rc.6", systemInstallation!.Version, "版本解析错误");
            Equal(DshInstallationSource.System, systemInstallation.Source, "安装来源错误");

            var stringBin = Path.Combine(directory, "string-bin");
            CreatePackage(stringBin, "0.2.0", binAsString: true);
            Assert(DshPackageLocator.TryValidatePackageDirectory(
                stringBin,
                DshInstallationSource.Specified,
                out var specifiedInstallation,
                out var specifiedError), specifiedError);
            Equal(DshInstallationSource.Specified, specifiedInstallation!.Source, "指定来源错误");
            Assert(File.Exists(specifiedInstallation.EntryPoint), "入口文件应存在");
        });
        return Task.CompletedTask;
    }

    private static Task TestInvalidPackages()
    {
        WithTemporaryDirectory(directory =>
        {
            var wrongName = Path.Combine(directory, "wrong-name");
            CreatePackage(wrongName, "1.0.0", name: "not-dsh");
            AssertInvalidPackage(wrongName, "错误包名应被拒绝");

            var missingVersion = Path.Combine(directory, "missing-version");
            CreatePackage(missingVersion, null);
            AssertInvalidPackage(missingVersion, "缺失版本应被拒绝");

            var missingEntry = Path.Combine(directory, "missing-entry");
            CreatePackage(missingEntry, "1.0.0", createEntry: false);
            AssertInvalidPackage(missingEntry, "缺失入口应被拒绝");

            var traversal = Path.Combine(directory, "traversal");
            CreatePackage(traversal, "1.0.0", entry: @"..\outside.js", createEntry: false);
            File.WriteAllText(Path.Combine(directory, "outside.js"), "// outside");
            AssertInvalidPackage(traversal, "越界入口应被拒绝");

            AssertInvalidPackage(Path.Combine(directory, "deleted"), "不存在目录应被拒绝");
        });
        return Task.CompletedTask;
    }

    private static Task TestPathDiscoveryPrecedence()
    {
        WithTemporaryDirectory(directory =>
        {
            var first = Path.Combine(directory, "first");
            var second = Path.Combine(directory, "second");
            CreateShimInstallation(first, "0.1.0");
            CreateShimInstallation(second, "9.0.0");
            var installation = DshPackageLocator.FindSystemInstallation(
                [first, second],
                Path.Combine(directory, "empty-appdata"),
                () => null);
            Assert(installation is not null, "应找到 PATH 安装");
            Equal("0.1.0", installation!.Version, "应遵循 PATH 顺序而非选择最高版本");
            Assert(installation.PackageDirectory.StartsWith(first, StringComparison.OrdinalIgnoreCase), "应使用第一个 PATH 候选");
        });
        return Task.CompletedTask;
    }

    private static Task TestStandardNpmDiscovery()
    {
        WithTemporaryDirectory(directory =>
        {
            var appData = Path.Combine(directory, "appdata");
            var package = Path.Combine(appData, "npm", "node_modules", "@deepseek-ai", "dsh");
            CreatePackage(package, "1.2.3");
            var installation = DshPackageLocator.FindSystemInstallation([], appData, () => null);
            Equal("1.2.3", installation?.Version, "应回退标准 npm 全局目录");
        });
        return Task.CompletedTask;
    }

    private static Task TestNpmRootDiscovery()
    {
        WithTemporaryDirectory(directory =>
        {
            var npmRoot = Path.Combine(directory, "global-root");
            var package = Path.Combine(npmRoot, "@deepseek-ai", "dsh");
            CreatePackage(package, "2.0.0");
            var installation = DshPackageLocator.FindSystemInstallation(
                [],
                Path.Combine(directory, "empty-appdata"),
                () => npmRoot);
            Equal("2.0.0", installation?.Version, "应使用 npm root 回退");

            var missing = DshPackageLocator.FindSystemInstallation(
                [],
                Path.Combine(directory, "empty-appdata"),
                () => null);
            Assert(missing is null, "npm root 失败时应稳定返回未找到");
        });
        return Task.CompletedTask;
    }

    private static Task TestWindowMaximizeBounds()
    {
        var bottomTaskbar = WindowWorkAreaMaximizer.CalculateBounds(
            0, 0,
            0, 0, 2560, 1380);
        Equal(new WindowMaximizeBounds(0, 0, 2560, 1380), bottomTaskbar,
            "底部任务栏应从最大化高度中排除");

        var topTaskbar = WindowWorkAreaMaximizer.CalculateBounds(
            0, 0,
            0, 48, 1920, 1080);
        Equal(new WindowMaximizeBounds(0, 48, 1920, 1032), topTaskbar,
            "顶部任务栏应偏移最大化窗口");

        var leftTaskbar = WindowWorkAreaMaximizer.CalculateBounds(
            0, 0,
            56, 0, 1920, 1080);
        Equal(new WindowMaximizeBounds(56, 0, 1864, 1080), leftTaskbar,
            "左侧任务栏应偏移并收窄最大化窗口");

        var rightTaskbar = WindowWorkAreaMaximizer.CalculateBounds(
            0, 0,
            0, 0, 1864, 1080);
        Equal(new WindowMaximizeBounds(0, 0, 1864, 1080), rightTaskbar,
            "右侧任务栏应收窄最大化窗口");

        var secondaryMonitor = WindowWorkAreaMaximizer.CalculateBounds(
            -1920, -120,
            -1864, -72, 0, 960);
        Equal(new WindowMaximizeBounds(56, 48, 1864, 1032), secondaryMonitor,
            "负坐标副显示器应使用相对显示器的最大化位置");

        Assert(WindowWorkAreaMaximizer.TryGetBoundsForWindow(IntPtr.Zero, out var actualWorkArea),
            "应能通过 Windows API 获取当前工作区");
        Assert(actualWorkArea.Width > 0 && actualWorkArea.Height > 0,
            "Windows API 返回的工作区尺寸应有效");
        return Task.CompletedTask;
    }

    private static Task TestWindowDragRestorePosition()
    {
        // 100% DPI：光标在屏幕 (1000, 200)，标题栏宽 1280，抓取点 (100, 12)，
        // 正常窗口宽 1200。还原后应水平按抓取比例、垂直按抓取偏移锚定光标。
        var at100 = WindowDragRestore.CalculateRestorePosition(
            cursorScreenX: 1000,
            cursorScreenY: 200,
            pointerOffsetX: 100,
            pointerOffsetY: 12,
            titleBarWidth: 1280,
            normalWindowWidth: 1200,
            dpiScaleX: 1.0,
            dpiScaleY: 1.0);
        Equal(1000 - (100.0 / 1280.0) * 1200.0, at100.Left, "还原后 Left 未按水平抓取比例锚定");
        Equal(188.0, at100.Top, "还原后 Top 未按垂直抓取偏移锚定");

        // 150% DPI：像素坐标需折算回 DIP，垂直偏移也要乘以缩放。
        var at150 = WindowDragRestore.CalculateRestorePosition(
            cursorScreenX: 1500,
            cursorScreenY: 300,
            pointerOffsetX: 100,
            pointerOffsetY: 12,
            titleBarWidth: 1280,
            normalWindowWidth: 1200,
            dpiScaleX: 1.5,
            dpiScaleY: 1.5);
        Equal((1500 - (100.0 / 1280.0) * 1200.0) / 1.5, at150.Left, "高 DPI 下 Left 换算错误");
        Equal((300 - 12.0 * 1.5) / 1.5, at150.Top, "高 DPI 下 Top 换算错误");
        return Task.CompletedTask;
    }

    private static Task TestInstallationStatusText()
    {
        var running = new DshInstallation(
            DshInstallationSource.System,
            "0.1.0-rc.7",
            @"C:\npm\node_modules\@deepseek-ai\dsh",
            @"C:\npm\node_modules\@deepseek-ai\dsh\lib\bin.js");

        Equal("DSH 0.1.0-rc.7 · 运行中",
            DshInstallationStatusText.FormatRunningStatus(running),
            "顶部应显示运行状态，而不是声明安装仍可用");

        var unavailable = DshInstallationStatusText.FormatSystemDetection(
            null,
            running,
            "npm install --global @deepseek-ai/dsh");
        Assert(unavailable.Contains("当前仍在运行 DSH 0.1.0-rc.7", StringComparison.Ordinal),
            "应保留当前运行版本");
        Assert(unavailable.Contains("系统安装已不可用", StringComparison.Ordinal),
            "应明确说明磁盘安装已不可用");

        var detected = running with { Version = "0.1.0-rc.8" };
        var available = DshInstallationStatusText.FormatSystemDetection(
            detected,
            running,
            "npm install --global @deepseek-ai/dsh");
        Assert(available.Contains("版本：0.1.0-rc.8", StringComparison.Ordinal),
            "应优先显示当前检测到的安装");
        Assert(!available.Contains("0.1.0-rc.7", StringComparison.Ordinal),
            "不应用运行中的旧版本覆盖当前安装版本");
        return Task.CompletedTask;
    }

    private static Task TestSpecifiedModeDoesNotFallback()
    {
        var providerCalled = false;
        var settings = new DshSettings
        {
            InstallationMode = DshInstallationMode.SpecifiedPath,
            DshPackageDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        };
        Throws<DshInstallationNotFoundException>(() => DshProcessManager.ResolveInstallation(
            settings,
            () =>
            {
                providerCalled = true;
                return null;
            }));
        Assert(!providerCalled, "指定模式不应调用自动检测");
        return Task.CompletedTask;
    }

    private static Task TestMissingInstallationError()
    {
        var settings = new DshSettings { InstallationMode = DshInstallationMode.AutoDetect };
        Throws<DshInstallationNotFoundException>(() =>
            DshProcessManager.ResolveInstallation(settings, () => null));
        return Task.CompletedTask;
    }

    private static Task TestExistingServiceChoices()
    {
        var connect = DshLaunchPolicy.Decide(
            DshInstallationMode.SpecifiedPath,
            existingServiceHealthy: true,
            ExistingDshChoice.ConnectExisting);
        Assert(connect.ApplyChanges && connect.AttachExisting, "连接现有服务选择错误");

        var launch = DshLaunchPolicy.Decide(
            DshInstallationMode.SpecifiedPath,
            existingServiceHealthy: true,
            ExistingDshChoice.LaunchSpecified);
        Assert(launch.ApplyChanges && !launch.AttachExisting, "启动指定版本选择错误");

        var cancel = DshLaunchPolicy.Decide(
            DshInstallationMode.SpecifiedPath,
            existingServiceHealthy: true,
            ExistingDshChoice.Cancel);
        Assert(!cancel.ApplyChanges, "取消不能应用设置或切换进程");

        var noConflict = DshLaunchPolicy.Decide(
            DshInstallationMode.SpecifiedPath,
            existingServiceHealthy: false);
        Assert(noConflict.ApplyChanges && !noConflict.AttachExisting, "无冲突时应启动指定版本");
        return Task.CompletedTask;
    }

    private static Task TestProcessStartInfo()
    {
        WithTemporaryDirectory(directory =>
        {
            var packageDirectory = Path.Combine(directory, "dsh");
            CreatePackage(packageDirectory, "1.0.0");
            DshPackageLocator.TryValidatePackageDirectory(
                packageDirectory,
                DshInstallationSource.System,
                out var installation,
                out _);

            var originalDshHome = Environment.GetEnvironmentVariable("DSH_HOME");
            var originalNpmCache = Environment.GetEnvironmentVariable("npm_config_cache");
            var originalSshConnection = Environment.GetEnvironmentVariable("SSH_CONNECTION");
            var originalSshTty = Environment.GetEnvironmentVariable("SSH_TTY");
            try
            {
                Environment.SetEnvironmentVariable("DSH_HOME", "inherited-home");
                Environment.SetEnvironmentVariable("npm_config_cache", "inherited-cache");
                Environment.SetEnvironmentVariable("SSH_CONNECTION", "secret-connection");
                Environment.SetEnvironmentVariable("SSH_TTY", "secret-tty");

                var startInfo = DshProcessManager.CreateStartInfo("node.exe", installation!, directory, 3080);
                Equal(Path.GetFullPath(directory), startInfo.WorkingDirectory, "Workspace 未应用");
                Assert(startInfo.ArgumentList.SequenceEqual([
                    installation!.EntryPoint,
                    "web",
                    "--no-open",
                    "--host",
                    "127.0.0.1",
                    "--port",
                    "3080"
                ]), "启动参数错误");

                var randomStartInfo = DshProcessManager.CreateStartInfo("node.exe", installation!, directory, 0);
                Equal("0", randomStartInfo.ArgumentList[^1], "传 0 时应让系统分配端口");
                Equal("inherited-home", startInfo.Environment["DSH_HOME"], "DSH_HOME 应原样继承");
                Equal("inherited-cache", startInfo.Environment["npm_config_cache"], "npm 缓存变量应原样继承");
                Assert(!startInfo.Environment.ContainsKey("SSH_CONNECTION"), "应移除 SSH_CONNECTION");
                Assert(!startInfo.Environment.ContainsKey("SSH_TTY"), "应移除 SSH_TTY");
            }
            finally
            {
                Environment.SetEnvironmentVariable("DSH_HOME", originalDshHome);
                Environment.SetEnvironmentVariable("npm_config_cache", originalNpmCache);
                Environment.SetEnvironmentVariable("SSH_CONNECTION", originalSshConnection);
                Environment.SetEnvironmentVariable("SSH_TTY", originalSshTty);
            }
        });
        return Task.CompletedTask;
    }

    private static async Task TestHealthProbe()
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = ServeHealthyPageOnceAsync(listener);
            var settings = new DshSettings { WorkspaceDirectory = directory };
            var log = new LogService(directory);
            using var manager = new DshProcessManager(settings, log);
            var healthy = await manager.IsHealthyDshAsync(new Uri($"http://127.0.0.1:{port}/"));
            Assert(healthy, "应识别 DSH 页面");
            await server;
        });
    }

    private static async Task TestAttachDoesNotStopExternalService()
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = ServeHealthyPageOnceAsync(listener);
            var settings = new DshSettings
            {
                AttachPort = port,
                WorkspaceDirectory = directory
            };
            var log = new LogService(directory);
            using var manager = new DshProcessManager(settings, log);
            var url = await manager.StartOrAttachAsync();
            await server;
            Equal(DshRuntimeState.Attached, manager.State, "应连接外部服务");
            Assert(!manager.OwnsCurrentProcess, "不能把外部服务标记为自管进程");
            Equal(port, url.Port, "连接端口错误");
            await manager.StopOwnedAsync();
            Assert(listener.Server.IsBound, "停止自管进程不能关闭外部监听器");
        });
    }

    private static async Task TestRestartOwnedService()
    {
        await WithTemporaryDirectoryAsync(async directory =>
        {
            var packageDirectory = Path.Combine(directory, "dsh");
            CreatePackage(packageDirectory, "1.0.0");
            File.WriteAllText(
                Path.Combine(packageDirectory, "lib", "bin.js"),
                """
                const http = require('http');
                const portIndex = process.argv.indexOf('--port');
                const requestedPort = Number(process.argv[portIndex + 1]);
                const server = http.createServer((_, response) => {
                  const body = '<html><title>DeepSeek Harness</title></html>';
                  response.writeHead(200, { 'Content-Type': 'text/html', 'Content-Length': Buffer.byteLength(body) });
                  response.end(body);
                });
                server.listen(requestedPort, '127.0.0.1', () => {
                  console.log(`dsh web: http://127.0.0.1:${server.address().port}`);
                });
                process.on('SIGINT', () => server.close(() => process.exit(0)));
                """);

            using var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
            portReservation.Stop();

            var settings = new DshSettings
            {
                InstallationMode = DshInstallationMode.SpecifiedPath,
                DshPackageDirectory = packageDirectory,
                WorkspaceDirectory = directory,
                AttachPort = port,
                StartupTimeoutSeconds = 10
            };
            var log = new LogService(directory);
            using var manager = new DshProcessManager(settings, log);
            await manager.StartOrAttachAsync(attachExisting: false);
            Assert(manager.OwnsCurrentProcess, "首次启动后应持有 DSH 进程");

            var restartedUrl = await manager.RestartAsync();
            Equal(DshRuntimeState.Ready, manager.State, "重启后不应误判为外部服务");
            Assert(manager.OwnsCurrentProcess, "重启后应持有新 DSH 进程");
            Equal(port, restartedUrl.Port, "重启后应继续使用配置端口");

            await manager.StopOwnedAsync();
        });
    }

    private static Task TestPortAvailability()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var occupied = DshProcessManager.ProbePort(port);
        Assert(!occupied.IsAvailable, "占用中的端口应被识别为不可用");
        Equal(SocketError.AddressAlreadyInUse, occupied.SocketErrorCode, "应保留端口占用错误码");
        Assert(DshProcessManager.ResolveStartPort(port) == 0, "端口占用时应回退到随机端口");
        Assert(
            DshProcessManager.FormatPortFailureForStatus(port, occupied).Contains("已被其他程序监听"),
            "占用提示应说明存在监听器");
        Assert(
            DshProcessManager.FormatPortFailureForLog(port, occupied).Contains("AddressAlreadyInUse"),
            "占用日志应包含 Winsock 错误名");

        listener.Stop();
        Assert(DshProcessManager.ProbePort(port).IsAvailable, "释放后的端口应视为可用");
        Assert(DshProcessManager.IsPortAvailable(port), "公开 API 应报告端口可用");
        Equal(port, DshProcessManager.ResolveStartPort(port), "端口空闲时应使用配置端口");
        Assert(DshProcessManager.ProbePort(0).IsAvailable, "端口 0 应视为可用（系统分配）");

        var accessDenied = new DshProcessManager.PortProbeResult(
            false,
            SocketError.AccessDenied,
            10013,
            "Access denied");
        Assert(
            DshProcessManager.FormatPortFailureForStatus(3080, accessDenied)
                .Contains("系统排除/保留范围"),
            "权限拒绝提示应说明 Windows 排除或保留端口");
        var accessDeniedLog = DshProcessManager.FormatPortFailureForLog(3080, accessDenied);
        Assert(accessDeniedLog.Contains("AccessDenied/10013"), "权限拒绝日志应包含准确错误码");
        Assert(accessDeniedLog.Contains("excluded/reserved"), "权限拒绝日志应说明排除或保留范围");

        var unexpected = new DshProcessManager.PortProbeResult(
            false,
            SocketError.NetworkDown,
            10050,
            "Network is\r\ndown");
        Assert(
            DshProcessManager.FormatPortFailureForStatus(3080, unexpected)
                .Contains("NetworkDown/10050"),
            "其他错误提示应保留 Winsock 错误码");
        var unexpectedLog = DshProcessManager.FormatPortFailureForLog(3080, unexpected);
        Assert(unexpectedLog.Contains("Network is down"), "其他错误日志应保留系统消息");
        Assert(!unexpectedLog.Contains('\r') && !unexpectedLog.Contains('\n'), "端口错误日志应保持单行");

        WithTemporaryDirectory(directory =>
        {
            var log = new LogService(directory);
            log.Warning("Port fallback test");
            Assert(
                File.ReadAllText(log.CurrentLogPath).Contains("[WARN] Port fallback test"),
                "端口回退应使用 WARN 级别日志");
        });
        return Task.CompletedTask;
    }

    private static async Task TestStopGracefulThenForce()
    {
        // 一个永远不会自行退出的 node 进程:优雅信号(尽力而为)后必须在
        // 宽限期内被兜底强杀。
        var startInfo = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("setInterval(() => {}, 1000)");
        using var process = Process.Start(startInfo)!;
        try
        {
            await DshProcessManager.StopGracefullyAsync(process, TimeSpan.FromSeconds(1));
            Assert(process.HasExited, "进程应在优雅→强杀流程结束后退出");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 测试清理 */ }
            }
        }
    }

    private static async Task TestConPtyCtrlC()
    {
        var nodeExecutable = DshPackageLocator.FindNodeExecutable()
            ?? throw new InvalidOperationException("测试需要 Node.js");
        var startInfo = new ProcessStartInfo(nodeExecutable)
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(
            "process.on('SIGINT',()=>{console.log('graceful-sigint');process.exit(0)});" +
            "console.log('conpty-ready');setInterval(()=>{},1000)");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var graceful = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = ConPtyProcess.Start(startInfo, line =>
        {
            if (line.Contains("conpty-ready", StringComparison.Ordinal)) ready.TrySetResult();
            if (line.Contains("graceful-sigint", StringComparison.Ordinal)) graceful.TrySetResult();
        });
        try
        {
            var startupResult = await Task.WhenAny(
                ready.Task,
                session.OutputCompleted,
                Task.Delay(TimeSpan.FromSeconds(5)));
            if (startupResult == session.OutputCompleted)
            {
                await session.OutputCompleted;
                throw new InvalidOperationException("ConPTY 输出在 ready 行之前结束");
            }

            Assert(startupResult == ready.Task, "ConPTY 没有捕获 Node.js ready 输出");
            await session.SendCtrlCAsync();
            await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await graceful.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(0, session.Process.ExitCode, "Ctrl+C 应进入 Node.js SIGINT 处理器并正常退出");
        }
        finally
        {
            if (!session.Process.HasExited)
            {
                try { session.Process.Kill(entireProcessTree: true); } catch { /* 测试清理 */ }
            }
        }
    }

    private static Task TestConPtyArgumentQuoting()
    {
        Equal("plain", ConPtyProcess.QuoteArgument("plain"), "普通参数不应增加引号");
        Equal("\"two words\"", ConPtyProcess.QuoteArgument("two words"), "空格参数应加引号");
        Equal("\"\"", ConPtyProcess.QuoteArgument(string.Empty), "空参数应保留");
        Equal("\"a\\\\\\\"b\"", ConPtyProcess.QuoteArgument("a\\\"b"), "反斜杠和引号应按 Windows 规则转义");
        Equal("\"path with space\\\\\"", ConPtyProcess.QuoteArgument("path with space\\"), "结尾反斜杠应在引号内加倍");
        return Task.CompletedTask;
    }

    private static async Task TestSelfExitingProcessNotForceKilled()
    {
        // 在宽限期内自行退出的进程不应被强杀(退出码保持 0)。
        var startInfo = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("setTimeout(() => process.exit(0), 300)");
        using var process = Process.Start(startInfo)!;
        await DshProcessManager.StopGracefullyAsync(process, TimeSpan.FromSeconds(3));
        Assert(process.HasExited, "进程应自行退出");
        Equal(0, process.ExitCode, "自行退出不应被强杀");
    }

    private static async Task ServeHealthyPageOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var buffer = new byte[2048];
        await stream.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false);
        var body = "<html><title>DeepSeek Harness</title></html>";
        var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
    }

    private static void CreateShimInstallation(string shimDirectory, string version)
    {
        Directory.CreateDirectory(shimDirectory);
        File.WriteAllText(Path.Combine(shimDirectory, "dsh.cmd"), "@echo off");
        CreatePackage(
            Path.Combine(shimDirectory, "node_modules", "@deepseek-ai", "dsh"),
            version);
    }

    private static void CreatePackage(
        string packageDirectory,
        string? version,
        bool binAsString = false,
        string name = "@deepseek-ai/dsh",
        string entry = "lib/bin.js",
        bool createEntry = true)
    {
        Directory.CreateDirectory(packageDirectory);
        var package = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["bin"] = binAsString ? entry : new Dictionary<string, string> { ["dsh"] = entry }
        };
        if (version is not null)
        {
            package["version"] = version;
        }

        File.WriteAllText(
            Path.Combine(packageDirectory, "package.json"),
            JsonSerializer.Serialize(package));
        if (!createEntry)
        {
            return;
        }

        var entryPath = Path.GetFullPath(Path.Combine(packageDirectory, entry));
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        File.WriteAllText(entryPath, "// test entry");
    }

    private static void AssertInvalidPackage(string directory, string message)
    {
        Assert(!DshPackageLocator.TryValidatePackageDirectory(
            directory,
            DshInstallationSource.Specified,
            out _,
            out _), message);
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DshDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            action(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task WithTemporaryDirectoryAsync(Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DshDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await action(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static TException Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"期望抛出 {typeof(TException).Name}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}。期望 {expected}，实际 {actual}");
        }
    }
}
