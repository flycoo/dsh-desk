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

    public async Task<Uri> StartOrAttachAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(DshRuntimeState.Checking, "正在检查本机 DeepSeek Harness…");
            var existingUrl = new Uri($"http://127.0.0.1:{_settings.AttachPort}/");
            if (await IsHealthyDshAsync(existingUrl, cancellationToken).ConfigureAwait(false))
            {
                CurrentUrl = existingUrl;
                CurrentVersion = null;
                SetState(DshRuntimeState.Attached, $"已连接现有 DSH · {existingUrl.Authority}", existingUrl, false);
                _log.Info($"Attached to existing DSH at {existingUrl}");
                return existingUrl;
            }

            await StopOwnedCoreAsync().ConfigureAwait(false);

            var nodeExecutable = DshPackageLocator.FindNodeExecutable()
                ?? throw new InvalidOperationException("未找到 Node.js。请先安装 Node.js 并确认 node.exe 已加入 PATH。");
            var installation = DshPackageLocator.FindDshInstallation(_settings.NpmCache)
                ?? throw new InvalidOperationException(
                    $"未在 {_settings.NpmCache} 找到官方 @deepseek-ai/dsh。请在设置中运行“更新/修复官方 DSH”。");
            if (!Directory.Exists(_settings.DshHome))
            {
                throw new DirectoryNotFoundException($"DSH Home 不存在：{_settings.DshHome}");
            }

            CurrentVersion = installation.Version;
            SetState(DshRuntimeState.Starting, $"正在启动 DSH {installation.Version}…");
            _log.Info($"Starting DSH {installation.Version} from {installation.EntryPoint}");

            var startInfo = new ProcessStartInfo(nodeExecutable)
            {
                WorkingDirectory = Directory.GetParent(_settings.DshHome)?.FullName ?? _settings.DshHome,
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
            startInfo.Environment["DSH_HOME"] = _settings.DshHome;
            startInfo.Environment["npm_config_cache"] = _settings.NpmCache;
            startInfo.Environment.Remove("SSH_CONNECTION");
            startInfo.Environment.Remove("SSH_TTY");

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
            SetState(DshRuntimeState.Ready, $"DSH {installation.Version} 已连接 · {url.Authority}", url, true);
            _log.Info($"DSH ready at {url}");
            return url;
        }
        catch (Exception exception)
        {
            CurrentUrl = null;
            SetState(DshRuntimeState.Faulted, exception.Message);
            _log.Error(exception, "DSH startup failed");
            await StopOwnedCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Uri> RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (OwnsCurrentProcess)
            {
                await StopOwnedCoreAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        return await StartOrAttachAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task StopOwnedCoreAsync()
    {
        var process = _ownedProcess;
        _ownedProcess = null;
        _readyUrl = null;
        CurrentUrl = null;
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
