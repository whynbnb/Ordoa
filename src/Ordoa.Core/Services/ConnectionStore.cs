using Ordoa.Core.Models;
using Ordoa.Core.Util;
using Ordoa.Core.Localization;

namespace Ordoa.Core.Services;

public sealed class ConnectionStore
{
    private readonly object _sync = new();
    private List<ConnectionProfile> _profiles;

    public ConnectionStore()
    {
        _profiles = JsonFile.Load<List<ConnectionProfile>>(AppPaths.ConnectionsFile) ?? [];
        foreach (var profile in _profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
                profile.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(profile.Name))
                profile.Name = DefaultName(profile);
            profile.InitialPath = profile.NormalizeInitialPath(profile.InitialPath);
            profile.SshJumps ??= [];
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ConnectionProfile> GetAll()
    {
        lock (_sync)
            return _profiles.Select(p => p.Clone()).ToList();
    }

    public ConnectionProfile? Get(string id)
    {
        lock (_sync)
            return _profiles.FirstOrDefault(p => p.Id == id)?.Clone();
    }

    public void Save(ConnectionProfile profile)
    {
        profile.InitialPath = profile.NormalizeInitialPath(profile.InitialPath);
        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = DefaultName(profile);

        lock (_sync)
        {
            var index = _profiles.FindIndex(p => p.Id == profile.Id);
            if (index >= 0)
                _profiles[index] = profile.Clone();
            else
                _profiles.Add(profile.Clone());
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        lock (_sync)
        {
            _profiles.RemoveAll(p => p.Id == id);
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string DefaultName(ConnectionProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Name))
            return profile.Name;
        var host = string.IsNullOrWhiteSpace(profile.Host) ? L10n.T("远程") : profile.Host;
        return $"{profile.ProtocolLabel} · {host}";
    }

    private void Persist()
    {
        try
        {
            JsonFile.Save(AppPaths.ConnectionsFile, _profiles);
        }
        catch (Exception ex)
        {
            Log.Error("保存连接列表失败", ex);
        }
    }
}
