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
