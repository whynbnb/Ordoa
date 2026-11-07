using Ordoa.Core.Util;

namespace Ordoa.Core.Services;

public sealed class SessionState
{
    public List<SessionTab> Tabs { get; set; } = [];
    public int ActiveTab { get; set; }
}

public sealed class SessionTab
{
    public List<SessionPane> Panes { get; set; } = [];
    public int ActivePane { get; set; }
    public bool IsSplit { get; set; }
    public bool IsPinned { get; set; }
}

public sealed class SessionPane
{
    public string FileSystemId { get; set; } = "local";
    public string Path { get; set; } = string.Empty;
    /// <summary>Source archive when FileSystemId is "archive".</summary>
    public string? ArchivePath { get; set; }
}

public sealed class SessionService
{
    public SessionState? Load()
    {
        var session = JsonFile.Load<SessionState>(AppPaths.SessionFile);
        if (session is null)
            return null;
        session.Tabs.RemoveAll(t => t.Panes.Count == 0);
        return session.Tabs.Count == 0 ? null : session;
    }

    public void Save(SessionState state)
    {
        try
        {
            JsonFile.Save(AppPaths.SessionFile, state);
        }
        catch (Exception ex)
        {
            Log.Warn("保存会话失败", ex);
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.SessionFile))
                File.Delete(AppPaths.SessionFile);
        }
        catch { /* ignore */ }
    }
}

/// <summary>A named snapshot of tabs and split panes.</summary>
public sealed class SavedWorkspace
{
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
    public SessionState State { get; set; } = new();
}

/// <summary>Persists named workspaces independently from the last session.</summary>
public sealed class WorkspaceService
{
    private List<SavedWorkspace>? _workspaces;

    public IReadOnlyList<SavedWorkspace> All => Load();

    public SavedWorkspace? Find(string name)
        => Load().FirstOrDefault(workspace =>
            string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Save(string name, SessionState state)
    {
        var workspaces = Load().ToList();
        workspaces.RemoveAll(workspace =>
            string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase));
        workspaces.Insert(0, new SavedWorkspace
        {
            Name = name.Trim(),
            SavedAt = DateTimeOffset.Now,
            State = state,
        });
        while (workspaces.Count > 50)
            workspaces.RemoveAt(workspaces.Count - 1);
        _workspaces = workspaces;
        Persist();
    }

    public bool Delete(string name)
    {
        var workspaces = Load().ToList();
        if (workspaces.RemoveAll(workspace =>
                string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
            return false;
        _workspaces = workspaces;
        Persist();
        return true;
    }

    private List<SavedWorkspace> Load()
    {
        if (_workspaces is not null)
            return _workspaces;
        try
        {
            _workspaces = JsonFile.Load<List<SavedWorkspace>>(WorkspaceFile)?
                .Where(workspace => workspace is not null && !string.IsNullOrWhiteSpace(workspace.Name))
                .ToList() ?? [];
        }
        catch
        {
            _workspaces = [];
        }
        return _workspaces;
    }

    private void Persist()
    {
        try
        {
            JsonFile.Save(WorkspaceFile, _workspaces ?? []);
        }
        catch (Exception ex)
        {
            Log.Warn("保存工作区失败", ex);
        }
    }

    private static string WorkspaceFile => Path.Combine(AppPaths.DataDirectory, "workspaces.json");
}
