using System.Threading;
using System.Windows;
using DshDesk.Services;
using Microsoft.Win32;

namespace DshDesk;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\DshDesk.SingleInstance.1E18A886";
    private const string ActivateEventName = @"Local\DshDesk.Activate.1E18A886";

    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _activationCancellation;
    private DshProcessManager? _processManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(ActivateEventName);
                existingEvent.Set();
            }
            catch
            {
                // The first instance may still be creating its activation event.
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);
        ThemeService.ApplyCurrentTheme(Resources);
        SystemEvents.UserPreferenceChanged += SystemEvents_OnUserPreferenceChanged;

        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();
        var log = new LogService();
        _processManager = new DshProcessManager(settings, log);

        var window = new MainWindow(settings, settingsStore, log, _processManager);
        MainWindow = window;
        window.Show();

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activationCancellation = new CancellationTokenSource();
        _ = ListenForActivationAsync(window, _activationCancellation.Token);
    }

    private void SystemEvents_OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow window)
            {
                window.ApplySystemThemeFallback();
            }
            else
            {
                ThemeService.ApplyCurrentTheme(Resources);
            }
        });
    }

    private async Task ListenForActivationAsync(MainWindow window, CancellationToken cancellationToken)
    {
        if (_activateEvent is null)
        {
            return;
        }

        await Task.Run(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_activateEvent.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    continue;
                }

                Dispatcher.Invoke(window.RestoreFromTray);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_OnUserPreferenceChanged;
        _activationCancellation?.Cancel();
        _activateEvent?.Set();

        try
        {
            _processManager?.StopOwnedAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort cleanup during process shutdown.
        }

        _activateEvent?.Dispose();
        _activationCancellation?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
