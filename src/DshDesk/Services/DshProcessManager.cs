using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using DshDesk.Models;

namespace DshDesk.Services;

public sealed class DshProcessManager : IDisposable
{
    private readonly DshSettings _settings;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly ConcurrentQueue<string> _recentOutput = new();
    private Process? _ownedProcess;
    private TaskCompletionSource<Uri>? _readyUrl;
    private bool _stopping;

    public DshProcessManager(DshSettings settings, LogService log)
    {
        _settings = settings;
        _log = log;
        _httpClient = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    public event EventHandler<DshStateChangedEventArgs>? StateChanged;

    public DshRuntimeState State { get; private set; } = DshRuntimeState.Stopped;

    public Uri? CurrentUrl { get; private set; }

    public bool OwnsCurrentProcess => _ownedProcess is { HasExited: false };

    public string? CurrentVersion { get; private set; }

    public DshInstallation? CurrentInstallation { get; private set; }

    public Uri AttachUrl => new($"http://127.0.0.1:{_settings.AttachPort}/");

    public async Task<Uri> StartOrAttachAsync(
        bool attachExisting = true,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var startupTransitionBegan = false;
        try
        {
            SetState(DshRuntimeState.Checking, "正在检查本机 DeepSeek Harness…");
            var existingUrl = AttachUrl;
            if (attachExisting &&
                await IsHealthyDshAsync(existingUrl, cancellationToken).ConfigureAwait(false))
            {
                startupTransitionBegan = true;
                await StopOwnedCoreAsync().ConfigureAwait(false);
                CurrentUrl = existingUrl;
                CurrentVersion = null;
                CurrentInstallation = null;
                SetState(DshRuntimeState.Attached, $"已连接现有 DSH · {existingUrl.Authority}", existingUrl, false);
                _log.Info($"Attached to existing DSH at {existingUrl}");
                return existingUrl;
            }

            var nodeExecutable = DshPackageLocator.FindNodeExecutable()
                ?? throw new InvalidOperationException("未找到 Node.js。请先安装 Node.js 并确认 node.exe 已加入 PATH。");
            var installation = ResolveInstallation(_settings);
            ValidateWorkspaceDirectory(_settings.WorkspaceDirectory);

            startupTransitionBegan = true;
            await StopOwnedCoreAsync().ConfigureAwait(false);

            CurrentVersion = installation.Version;
            CurrentInstallation = installation;
            var sourceText = installation.Source == DshInstallationSource.System ? "系统安装" : "指定路径";
            SetState(DshRuntimeState.Starting, $"正在启动 DSH {installation.Version} · {sourceText}…");
            _log.Info(
                $"Starting DSH {installation.Version} from {installation.EntryPoint} " +
                $"(source: {installation.Source}, package: {installation.PackageDirectory})");

            var startInfo = CreateStartInfo(nodeExecutable, installation, _settings.WorkspaceDirectory);

            _readyUrl = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopping = false;
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += OnOutputDataReceived;
            process.ErrorDataReceived += OnErrorDataReceived;
            process.Exited += OnProcessExited;

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Node.js 进程没有成功启动。");
            }

            _ownedProcess = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings.StartupTimeoutSeconds, 10, 180)));
            Uri url;
            try
            {
                url = await _readyUrl.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"等待 DSH 启动超时。最近输出：{Environment.NewLine}{string.Join(Environment.NewLine, _recentOutput.TakeLast(12))}");
            }

            while (!await IsHealthyDshAsync(url, timeout.Token).ConfigureAwait(false))
            {
                await Task.Delay(250, timeout.Token).ConfigureAwait(false);
            }

            CurrentUrl = url;
            SetState(
                DshRuntimeState.Ready,
                $"DSH {installation.Version} · {sourceText} · {url.Authority}",
                url,
                true);
            _log.Info($"DSH ready at {url}");
            return url;
        }
        catch (Exception exception)
        {
            CurrentUrl = null;
            CurrentInstallation = null;
            SetState(DshRuntimeState.Faulted, exception.Message);
            _log.Error(exception, "DSH startup failed");
            if (startupTransitionBegan)
            {
                await StopOwnedCoreAsync().ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Uri> RestartAsync(
        bool attachExisting = true,
        CancellationToken cancellationToken = default)
    {
        return await StartOrAttachAsync(attachExisting, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopOwnedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopOwnedCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsHealthyDshAsync(Uri url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return content.Contains("DeepSeek Harness", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public static ProcessStartInfo CreateStartInfo(
        string nodeExecutable,
        DshInstallation installation,
        string workspaceDirectory)
    {
        ValidateWorkspaceDirectory(workspaceDirectory);
        var startInfo = new ProcessStartInfo(nodeExecutable)
        {
            WorkingDirectory = Path.GetFullPath(workspaceDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
        startInfo.ArgumentList.Add(installation.EntryPoint);
        startInfo.ArgumentList.Add("web");
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");
        startInfo.Environment.Remove("SSH_CONNECTION");
        startInfo.Environment.Remove("SSH_TTY");
        return startInfo;
    }

    public static DshInstallation ResolveInstallation(
        DshSettings settings,
        Func<DshInstallation?>? systemInstallationProvider = null)
    {
        if (settings.InstallationMode == DshInstallationMode.SpecifiedPath)
        {
            if (DshPackageLocator.TryValidatePackageDirectory(
                    settings.DshPackageDirectory,
                    DshInstallationSource.Specified,
                    out var specified,
                    out var error))
            {
                return specified!;
            }

            throw new DshInstallationNotFoundException(error);
        }

        return (systemInstallationProvider ?? (() => DshPackageLocator.FindSystemInstallation()))()
            ?? throw new DshInstallationNotFoundException(
                "未检测到官方 @deepseek-ai/dsh。请先执行 npm install --global @deepseek-ai/dsh，" +
                "然后重新检测；也可以在设置中选择已有的 DSH 包目录。");
    }

    private static void ValidateWorkspaceDirectory(string workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory) || !Directory.Exists(workspaceDirectory))
        {
            throw new DirectoryNotFoundException($"Workspace 不存在：{workspaceDirectory}");
        }
    }

    private async Task StopOwnedCoreAsync()
    {
        var process = _ownedProcess;
        _ownedProcess = null;
        _readyUrl = null;
        CurrentUrl = null;
        CurrentInstallation = null;
        CurrentVersion = null;
        if (process is null)
        {
            return;
        }

        _stopping = true;
        try
        {
            if (!process.HasExited)
            {
                _log.Info($"Stopping owned DSH process {process.Id}");
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Failed to stop owned DSH process");
        }
        finally
        {
            process.Dispose();
            _stopping = false;
            SetState(DshRuntimeState.Stopped, "DSH 已停止");
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs args) => HandleProcessLine(args.Data, false);

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs args) => HandleProcessLine(args.Data, true);

    private void HandleProcessLine(string? line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _recentOutput.Enqueue(line);
        while (_recentOutput.Count > 200)
        {
            _recentOutput.TryDequeue(out _);
        }

        if (isError) _log.Error($"[dsh] {line}");
        else _log.Info($"[dsh] {line}");

        if (DshUrlParser.TryParseReadyLine(line, out var uri) && uri is not null)
        {
            _readyUrl?.TrySetResult(uri);
        }
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        if (sender is not Process process || _stopping)
        {
            return;
        }

        var exitCode = process.ExitCode;
        var message = $"DSH 进程已退出（代码 {exitCode}）。";
        _readyUrl?.TrySetException(new InvalidOperationException(
            $"{message}{Environment.NewLine}{string.Join(Environment.NewLine, _recentOutput.TakeLast(12))}"));
        CurrentUrl = null;
        CurrentInstallation = null;
        CurrentVersion = null;
        SetState(DshRuntimeState.Faulted, message);
        _log.Error(message);
    }

    private void SetState(DshRuntimeState state, string message, Uri? url = null, bool isOwned = false)
    {
        State = state;
        StateChanged?.Invoke(this, new DshStateChangedEventArgs(state, message, url, isOwned));
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _gate.Dispose();
        _ownedProcess?.Dispose();
    }
}
