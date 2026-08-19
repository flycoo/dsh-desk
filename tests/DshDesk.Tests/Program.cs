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
            ("默认目录不再使用 G 盘", TestDefaultPaths),
            ("解析 DSH 页面主题", TestThemeMessageParser),
            ("验证官方 DSH 包目录", TestPackageValidation),
            ("拒绝损坏或越界的 DSH 包", TestInvalidPackages),
            ("PATH 中的 DSH 按顺序优先", TestPathDiscoveryPrecedence),
            ("回退标准 npm 全局目录", TestStandardNpmDiscovery),
            ("回退 npm root 全局目录", TestNpmRootDiscovery),
            ("指定模式不回退自动检测", TestSpecifiedModeDoesNotFallback),
            ("缺少安装时返回专用错误", TestMissingInstallationError),
            ("指定模式处理现有服务的三种选择", TestExistingServiceChoices),
            ("构造安全的 DSH 启动参数", TestProcessStartInfo),
            ("识别健康的 DeepSeek Harness 页面", TestHealthProbe),
            ("连接外部 DSH 时不终止服务", TestAttachDoesNotStopExternalService)
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
                StartupTimeoutSeconds = 45
            };
            store.Save(settings);
            var loaded = store.Load();
            Equal(settings.InstallationMode, loaded.InstallationMode, "安装模式未保存");
            Equal(settings.DshPackageDirectory, loaded.DshPackageDirectory, "DSH 路径未保存");
            Equal(settings.WorkspaceDirectory, loaded.WorkspaceDirectory, "Workspace 未保存");
            Equal(settings.AttachPort, loaded.AttachPort, "端口未保存");
            Equal(settings.CloseToTray, loaded.CloseToTray, "托盘设置未保存");
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

                var startInfo = DshProcessManager.CreateStartInfo("node.exe", installation!, directory);
                Equal(Path.GetFullPath(directory), startInfo.WorkingDirectory, "Workspace 未应用");
                Assert(startInfo.ArgumentList.SequenceEqual([
                    installation!.EntryPoint,
                    "web",
                    "--host",
                    "127.0.0.1",
                    "--port",
                    "0"
                ]), "启动参数错误");
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
