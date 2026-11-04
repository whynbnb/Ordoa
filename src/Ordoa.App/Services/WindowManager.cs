using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Ordoa.App.ViewModels;
using Ordoa.App.Views;
using Ordoa.Core.FileSystems;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.Services;

/// <summary>Owns the app's top-level windows and routes external open requests.</summary>
public sealed class WindowManager
{
    private readonly AppServices _services;
    private readonly List<MainWindow> _windows = [];
    private readonly List<SessionState> _closedSessions = [];
    private IClassicDesktopStyleApplicationLifetime? _lifetime;

    public WindowManager(AppServices services)
    {
        _services = services;
    }

    public IReadOnlyList<MainWindow> Windows => _windows;
    public MainWindow? ActiveWindow { get; private set; }

    public void AttachLifetime(IClassicDesktopStyleApplicationLifetime lifetime)
        => _lifetime = lifetime;

    public bool IsLastWindow(MainWindow window)
        => _windows.Count == 1 && ReferenceEquals(_windows[0], window);

    public MainWindow CreateWindow(string? location = null, bool restoreSession = false,
        IReadOnlyList<string>? selection = null)
    {
        var viewModel = new MainViewModel(_services, restoreSession, location, selection);
        var window = new MainWindow { DataContext = viewModel };
        ThemeService.ApplyDensity(window, _services.Settings.Current.CompactMode);
        ThemeService.ApplyWindowFrame(window, _services.Settings.Current.UseSystemWindowFrame);

        _windows.Add(window);
        ActiveWindow = window;

        window.Activated += (_, _) => ActiveWindow = window;
        window.Closed += (_, _) => Detach(window);

        if (_lifetime is { MainWindow: null })
            _lifetime.MainWindow = window;

        window.Show();
        window.Activate();
        return window;
    }

    public async Task<MainWindow> CreateWindowAsync(string? location = null, bool restoreSession = false,
        IReadOnlyList<string>? selection = null)
    {
        var window = CreateWindow(location, restoreSession, selection);
        if (window.DataContext is MainViewModel viewModel)
            await viewModel.WaitForReadyAsync();

        return window;
    }

    public void Activate(MainWindow? window)
    {
        window ??= ActiveWindow ?? _windows.FirstOrDefault();
        if (window is null)
            return;

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }

    /// <summary>Handles CLI paths forwarded by a second process or D-Bus activation.</summary>
    public Task HandleActivationAsync(ActivationRequest request)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await HandleActivationCoreAsync(request).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warn("处理外部打开请求失败", ex);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>Handles org.freedesktop.FileManager1 method calls.</summary>
    public async Task HandleFileManagerRequestAsync(IReadOnlyList<string> uris, FileManagerAction action)
    {
        var paths = uris
            .Select(ToLocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();

        if (paths.Count == 0)
            return;

        if (action == FileManagerAction.ShowItemProperties)
        {
            var entry = await LocalFileSystem.Instance.GetEntryAsync(paths[0]).ConfigureAwait(true);
            if (entry is null)
                return;

            Activate(ActiveWindow ?? CreateWindow());
            await _services.Dialogs.ShowPropertiesAsync(entry, LocalFileSystem.Instance).ConfigureAwait(true);
            return;
        }

        await HandleActivationCoreAsync(new ActivationRequest { Paths = paths }).ConfigureAwait(true);
    }

    private async Task HandleActivationCoreAsync(ActivationRequest request)
    {
        if (request.Undo)
        {
            Activate(ActiveWindow);
            await _services.Undo.UndoAsync().ConfigureAwait(true);
            return;
        }

        if (request.Redo)
        {
            Activate(ActiveWindow);
            await _services.Undo.RedoAsync().ConfigureAwait(true);
            return;
        }

        var paths = request.Paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var settings = _services.Settings.Current;
        var openNewWindow = settings.AllowMultipleWindows &&
                            (request.NewWindow ||
                             (!request.NewTab && settings.FolderOpenMode == FolderOpenMode.NewWindow));

        if (paths.Count == 0)
        {
            if (openNewWindow || ActiveWindow is null)
                CreateWindow();
            else
                Activate(ActiveWindow);
            return;
        }

        if (openNewWindow || ActiveWindow is null)
        {
            var (location, selection, remaining) = PrepareWindowStart(paths);
            var window = CreateWindow(location, restoreSession: false, selection);
            Activate(window);

            if (window.DataContext is MainViewModel viewModel)
            {
                await viewModel.WaitForReadyAsync().ConfigureAwait(true);
                if (remaining.Count > 0)
                    await OpenPathsInWindowAsync(window, remaining).ConfigureAwait(true);
            }

            return;
        }

        var target = ActiveWindow;
        Activate(target);
        await OpenPathsInWindowAsync(target, paths).ConfigureAwait(true);
    }

    private static (string Location, IReadOnlyList<string>? Selection, IReadOnlyList<string> Remaining)
        PrepareWindowStart(IReadOnlyList<string> paths)
    {
        var first = paths[0];
        if (File.Exists(first))
        {
            var parent = Path.GetDirectoryName(first) ?? KnownFolders.Home;
            var siblings = paths
                .Where(path => File.Exists(path) &&
                               string.Equals(Path.GetDirectoryName(path), parent, StringComparison.Ordinal))
                .ToList();
            var remaining = paths
                .Where(path => !siblings.Contains(path, StringComparer.Ordinal))
                .ToList();
            return (parent, siblings, remaining);
        }

        if (Directory.Exists(first))
            return (first, null, paths.Skip(1).ToList());

        return (first, null, paths.Skip(1).ToList());
    }

    private static async Task OpenPathsInWindowAsync(MainWindow window, IReadOnlyList<string> paths)
    {
        if (window.DataContext is not MainViewModel viewModel)
            return;

        await viewModel.WaitForReadyAsync().ConfigureAwait(true);

        var openedParents = new HashSet<string>(StringComparer.Ordinal);
        var openedDirectories = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                if (openedDirectories.Add(path))
                    await viewModel.OpenLocationAsync(path).ConfigureAwait(true);
                continue;
            }

            if (File.Exists(path))
            {
                var parent = Path.GetDirectoryName(path) ?? KnownFolders.Home;
                if (!openedParents.Add(parent))
                    continue;

                var files = paths
                    .Where(candidate => File.Exists(candidate) &&
                                        string.Equals(Path.GetDirectoryName(candidate), parent, StringComparison.Ordinal))
                    .ToList();
                await viewModel.OpenLocationAsync(parent, files).ConfigureAwait(true);
                continue;
            }

            await viewModel.OpenLocationAsync(path).ConfigureAwait(true);
        }
    }

    private static string? ToLocalPath(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
            return parsed.LocalPath;

        return Path.IsPathRooted(uri) ? uri : null;
    }

    public void SaveSession()
    {
        if (!_services.Settings.Current.RestoreSession)
        {
            _services.Session.Clear();
            return;
        }

        var combined = new SessionState();
        var activeWindow = ActiveWindow;
        foreach (var closed in _closedSessions)
            combined.Tabs.AddRange(closed.Tabs);

        foreach (var window in _windows)
        {
            if (window.DataContext is not MainViewModel viewModel)
                continue;

            var part = viewModel.CreateSessionState();
            if (ReferenceEquals(window, activeWindow))
                combined.ActiveTab = combined.Tabs.Count + Math.Max(0, part.ActiveTab);
            combined.Tabs.AddRange(part.Tabs);
        }

        // An empty save can happen a second time while Avalonia is shutting the
        // last window down. Keep the session that was just written.
        if (combined.Tabs.Count == 0)
            return;

        _services.Session.Save(combined);
        _services.Recent.Flush();
    }

    private void Detach(MainWindow window)
    {
        if (window.DataContext is MainViewModel viewModel)
        {
            _closedSessions.Add(viewModel.CreateSessionState());
            viewModel.Dispose();
        }

        _windows.Remove(window);

        if (ReferenceEquals(ActiveWindow, window))
            ActiveWindow = _windows.LastOrDefault();

        if (_lifetime is not null && ReferenceEquals(_lifetime.MainWindow, window))
            _lifetime.MainWindow = _windows.FirstOrDefault();

        if (_windows.Count == 0)
        {
            // OnClosing normally saved the session. This final save also covers
            // a compositor-initiated close where the last window's state was
            // captured after the regular save.
            SaveSession();
            _closedSessions.Clear();
            _lifetime?.Shutdown();
        }
    }
}
