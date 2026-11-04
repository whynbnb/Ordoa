using System.Text;
using Avalonia.Threading;
using Tmds.DBus.Protocol;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.Services;

public enum FileManagerAction
{
    ShowFolders,
    ShowItems,
    ShowItemProperties,
}

/// <summary>
/// Exposes the standard org.freedesktop.FileManager1 interface so browsers,
/// IDEs and other apps can ask Ordoa to reveal files or folders.
/// </summary>
public sealed class FileManager1Service : IPathMethodHandler, IAsyncDisposable
{
    private const string Interface = "org.freedesktop.FileManager1";
    private const string ObjectPath = "/org/freedesktop/FileManager1";
    private const string WellKnownName = "org.freedesktop.FileManager1";

    private readonly Func<IReadOnlyList<string>, FileManagerAction, Task> _handler;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private DBusConnection? _connection;

    public FileManager1Service(Func<IReadOnlyList<string>, FileManagerAction, Task> handler)
    {
        _handler = handler;
    }

    public string Path => ObjectPath;
    public bool HandlesChildPaths => false;
    public bool IsRunning => _connection is not null;

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!AppPaths.IsLinux)
            return false;

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
                return true;

            var address = DBusAddress.Session;
            if (string.IsNullOrWhiteSpace(address))
                return false;

            var connection = new DBusConnection(address);
            try
            {
                await connection.ConnectAsync().ConfigureAwait(false);
                connection.AddMethodHandler(this);

                var acquired = await connection.TryRequestNameAsync(
                    WellKnownName, RequestNameOptions.None).ConfigureAwait(false);
                if (!acquired)
                {
                    connection.Dispose();
                    return false;
                }

                _connection = connection;
                return true;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("启动 FileManager1 D-Bus 服务失败", ex);
            return false;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([InterfaceXml]);
            return default;
        }

        if (!string.Equals(context.Request.InterfaceAsString, Interface, StringComparison.Ordinal))
        {
            context.ReplyUnknownMethodError();
            return default;
        }

        FileManagerAction action;
        switch (context.Request.MemberAsString)
        {
            case "ShowFolders":
                action = FileManagerAction.ShowFolders;
                break;
            case "ShowItems":
                action = FileManagerAction.ShowItems;
                break;
            case "ShowItemProperties":
                action = FileManagerAction.ShowItemProperties;
                break;
            default:
                context.ReplyUnknownMethodError();
                return default;
        }

        try
        {
            var reader = context.Request.GetBodyReader();
            var uris = reader.ReadArrayOfString();
            reader.ReadString(); // startup id, unused

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await _handler(uris, action).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Log.Warn("处理 FileManager1 请求失败", ex);
                }
            });
            using var writer = context.CreateReplyWriter(string.Empty);
            context.Reply(writer.CreateMessage());
        }
        catch (Exception ex)
        {
            context.ReplyError(Interface + ".Error", ex.Message);
        }

        return default;
    }

    private static ReadOnlyMemory<byte> InterfaceXml { get; } =
        """
        <interface name="org.freedesktop.FileManager1">
          <method name="ShowFolders">
            <arg direction="in" type="as" name="URIs"/>
            <arg direction="in" type="s" name="StartupId"/>
          </method>
          <method name="ShowItems">
            <arg direction="in" type="as" name="URIs"/>
            <arg direction="in" type="s" name="StartupId"/>
          </method>
          <method name="ShowItemProperties">
            <arg direction="in" type="as" name="URIs"/>
            <arg direction="in" type="s" name="StartupId"/>
          </method>
        </interface>
        """u8.ToArray();

    public async ValueTask DisposeAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
            return;

        try
        {
            await connection.ReleaseNameAsync(WellKnownName).ConfigureAwait(false);
        }
        catch { /* ignore */ }

        connection.Dispose();
    }
}
