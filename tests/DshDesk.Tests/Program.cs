using System.Net;
using System.Net.Sockets;
using System.Text;
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
            ("读写便携设置", TestSettingsRoundTrip),
            ("解析 DSH 页面主题", TestThemeMessageParser),
            ("选择最新 npm 缓存版本", TestPackageLocator),
            ("识别健康的 DeepSeek Harness 页面", TestHealthProbe)
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

    private static Task TestThemeMessageParser()
    {
        Assert(
            DshThemeMessageParser.TryParse("{\"source\":\"dsh-desk-theme\",\"theme\":\"light\"}", out var isLight) && isLight,
            "应识别浅色主题");
        Assert(
            DshThemeMessageParser.TryParse("{\"source\":\"dsh-desk-theme\",\"theme\":\"dark\"}", out isLight) && !isLight,
            "应识别深色主题");
        Assert(
            !DshThemeMessageParser.TryParse("{\"source\":\"untrusted\",\"theme\":\"light\"}", out _),
            "不应接受其他页面消息");
        return Task.CompletedTask;
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
                DshHome = @"G:\Test\.dsh-home",
                NpmCache = @"G:\Test\.npm-cache",
                AppDataDirectory = @"G:\Test\.dsh-desk",
                CloseToTray = false,
                AttachPort = 4099,
                StartupTimeoutSeconds = 45
            };
            store.Save(settings);
            var loaded = store.Load();
            Equal(settings.DshHome, loaded.DshHome, "DSH Home 未保存");
            Equal(settings.AttachPort, loaded.AttachPort, "端口未保存");
            Equal(settings.CloseToTray, loaded.CloseToTray, "托盘设置未保存");
        });
        return Task.CompletedTask;
    }

    private static Task TestPackageLocator()
    {
        WithTemporaryDirectory(directory =>
        {
            CreateFakePackage(directory, "hash-a", "0.1.0-rc.7");
            CreateFakePackage(directory, "hash-b", "0.1.0-rc.10");
            CreateFakePackage(directory, "hash-c", "0.1.0");
            var installation = DshPackageLocator.FindDshInstallation(directory);
            Assert(installation is not null, "应找到缓存包");
            Equal("0.1.0", installation!.Version, "应优先正式版本");
            Assert(File.Exists(installation.EntryPoint), "入口文件应存在");
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
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var buffer = new byte[2048];
                await stream.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false);
                var body = "<html><title>DeepSeek Harness</title></html>";
                var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
            });

            var settings = new DshSettings { AppDataDirectory = directory };
            var log = new LogService(directory);
            using var manager = new DshProcessManager(settings, log);
            var healthy = await manager.IsHealthyDshAsync(new Uri($"http://127.0.0.1:{port}/"));
            Assert(healthy, "应识别 DSH 页面");
            await server;
        });
    }

    private static void CreateFakePackage(string npmCache, string hash, string version)
    {
        var packageDirectory = Path.Combine(npmCache, "_npx", hash, "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(Path.Combine(packageDirectory, "lib"));
        File.WriteAllText(
            Path.Combine(packageDirectory, "package.json"),
            $$"""
            {
              "name": "@deepseek-ai/dsh",
              "version": "{{version}}",
              "bin": { "dsh": "lib/bin.js" }
            }
            """);
        File.WriteAllText(Path.Combine(packageDirectory, "lib", "bin.js"), "// test entry");
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
