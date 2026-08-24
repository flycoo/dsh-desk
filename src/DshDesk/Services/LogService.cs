namespace DshDesk.Services;

public sealed class LogService
{
    private readonly object _gate = new();
    private string _logDirectory;

    public LogService(string? appDataDirectory = null)
    {
        _logDirectory = ResolveLogDirectory(appDataDirectory);
        Directory.CreateDirectory(_logDirectory);
        PruneOldLogs();
    }

    public string LogDirectory => _logDirectory;

    public string CurrentLogPath => Path.Combine(LogDirectory, $"dshdesk-{DateTime.Now:yyyyMMdd}.log");

    public void Info(string message) => Write("INFO", message);

    public void Warning(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(Exception exception, string context) =>
        Write("ERROR", $"{context}: {exception.Message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                File.AppendAllText(CurrentLogPath, line);
            }
            catch
            {
                try
                {
                    _logDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DSHDesk",
                        "logs");
                    Directory.CreateDirectory(_logDirectory);
                    File.AppendAllText(CurrentLogPath, line);
                }
                catch
                {
                    // Logging is best-effort and must never terminate the desktop shell.
                }
            }
        }
    }

    private static string ResolveLogDirectory(string? appDataDirectory)
    {
        var preferred = string.IsNullOrWhiteSpace(appDataDirectory)
            ? AppPaths.LogDirectory
            : Path.Combine(appDataDirectory, "logs");
        try
        {
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch
        {
            var fallback = AppPaths.LogDirectory;
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-14);
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "dshdesk-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Logging must never prevent the application from starting.
        }
    }
}
