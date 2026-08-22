using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;

namespace DshDesk.Services;

internal enum UpdateAvailability
{
    Current,
    Available,
    Unavailable,
    Failed
}

internal sealed record ProductUpdateStatus(
    string CurrentVersion,
    string? LatestVersion,
    UpdateAvailability Availability,
    Uri? MoreInfoUrl,
    string? Error = null);

internal sealed record UpdateCheckResult(
    ProductUpdateStatus DshDesk,
    ProductUpdateStatus SystemDsh)
{
    internal bool HasUpdate =>
        DshDesk.Availability == UpdateAvailability.Available ||
        SystemDsh.Availability == UpdateAvailability.Available;
}

internal sealed class UpdateCheckService : IDisposable
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    internal static readonly Uri DeskReleasesUrl = new("https://github.com/flycoo/dsh-desk/releases/latest");
    internal static readonly Uri DshPackageUrl = new("https://www.npmjs.com/package/@deepseek-ai/dsh");
    private static readonly Uri DeskApiUrl = new("https://api.github.com/repos/flycoo/dsh-desk/releases/latest");
    private static readonly Uri DshLatestUrl = new("https://registry.npmjs.org/@deepseek-ai%2Fdsh/latest");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    internal UpdateCheckService(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"DSH-Desk/{CurrentDeskVersion}");
        }
    }

    internal static string CurrentDeskVersion
    {
        get
        {
            var informational = typeof(UpdateCheckService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            return string.IsNullOrWhiteSpace(informational)
                ? typeof(UpdateCheckService).Assembly.GetName().Version?.ToString(3) ?? "未知"
                : informational.Split('+')[0];
        }
    }

    internal static TimeSpan CalculateNextCheckDelay(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc)
    {
        if (lastCheckUtc is null)
        {
            return TimeSpan.Zero;
        }

        var elapsed = nowUtc - lastCheckUtc.Value;
        if (elapsed < TimeSpan.Zero)
        {
            return CheckInterval;
        }

        return elapsed >= CheckInterval ? TimeSpan.Zero : CheckInterval - elapsed;
    }

    internal async Task<UpdateCheckResult> CheckAsync(
        string? currentSystemDshVersion,
        CancellationToken cancellationToken = default)
    {
        var deskTask = CheckDeskAsync(cancellationToken);
        var dshTask = CheckDshAsync(currentSystemDshVersion, cancellationToken);
        await Task.WhenAll(deskTask, dshTask).ConfigureAwait(false);
        return new UpdateCheckResult(await deskTask, await dshTask);
    }

    internal static ProductUpdateStatus ParseDeskResponse(string currentVersion, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var latestVersion = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        var urlText = root.TryGetProperty("html_url", out var urlProperty) ? urlProperty.GetString() : null;
        var url = Uri.TryCreate(urlText, UriKind.Absolute, out var parsedUrl) ? parsedUrl : DeskReleasesUrl;
        return BuildStatus(currentVersion, latestVersion, url);
    }

    internal static ProductUpdateStatus ParseDshResponse(string currentVersion, string json)
    {
        using var document = JsonDocument.Parse(json);
        var latestVersion = document.RootElement.GetProperty("version").GetString();
        return BuildStatus(currentVersion, latestVersion, DshPackageUrl);
    }

    private async Task<ProductUpdateStatus> CheckDeskAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await _httpClient.GetStringAsync(DeskApiUrl, cancellationToken).ConfigureAwait(false);
            return ParseDeskResponse(CurrentDeskVersion, json);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            var fallback = await TryReadPrivateReleaseWithGitHubCliAsync(cancellationToken).ConfigureAwait(false);
            if (fallback.Json is not null)
            {
                return ParseDeskResponse(CurrentDeskVersion, fallback.Json);
            }

            return new ProductUpdateStatus(
                CurrentDeskVersion,
                null,
                UpdateAvailability.Failed,
                DeskReleasesUrl,
                fallback.Error ?? "Release 不可匿名访问，且 GitHub CLI 不可用或未登录。");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return new ProductUpdateStatus(
                CurrentDeskVersion,
                null,
                UpdateAvailability.Failed,
                DeskReleasesUrl,
                exception.Message);
        }
    }

    private static async Task<(string? Json, string? Error)> TryReadPrivateReleaseWithGitHubCliAsync(
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo("gh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("api");
            startInfo.ArgumentList.Add("repos/flycoo/dsh-desk/releases/latest");
            process = Process.Start(startInfo);
            if (process is null)
            {
                return (null, "无法启动 GitHub CLI。");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)
                ? (output, null)
                : (null, string.IsNullOrWhiteSpace(error) ? "GitHub CLI 未返回 Release。" : error.Trim());
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort cleanup */ }
            }
            return (null, exception.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task<ProductUpdateStatus> CheckDshAsync(string? currentVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return new ProductUpdateStatus(
                "未检测到",
                null,
                UpdateAvailability.Unavailable,
                DshPackageUrl);
        }

        try
        {
            var json = await _httpClient.GetStringAsync(DshLatestUrl, cancellationToken).ConfigureAwait(false);
            return ParseDshResponse(currentVersion, json);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return new ProductUpdateStatus(
                currentVersion,
                null,
                UpdateAvailability.Failed,
                DshPackageUrl,
                exception.Message);
        }
    }

    private static ProductUpdateStatus BuildStatus(string currentVersion, string? latestVersion, Uri url)
    {
        if (!SemanticVersion.TryParse(latestVersion, out _))
        {
            return new ProductUpdateStatus(
                currentVersion,
                latestVersion,
                UpdateAvailability.Failed,
                url,
                "版本源返回了无法识别的版本号。");
        }

        return new ProductUpdateStatus(
            currentVersion,
            latestVersion,
            SemanticVersion.IsNewer(latestVersion, currentVersion)
                ? UpdateAvailability.Available
                : UpdateAvailability.Current,
            url);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
