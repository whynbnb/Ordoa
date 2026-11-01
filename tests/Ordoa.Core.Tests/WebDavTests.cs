using System.Net;
using System.Text;
using System.Xml.Linq;
using Ordoa.Core.FileSystems.Remote;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.Tests;

/// <summary>End-to-end test of the WebDAV provider against an in-process WebDAV server.</summary>
public sealed class WebDavTests : IAsyncLifetime
{
    private static readonly XNamespace D = "DAV:";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordoa-dav-" + Guid.NewGuid().ToString("N"));
    private HttpListener? _listener;
    private Task? _serverTask;
    private int _port;
    private WebDavFileSystem? _fs;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "hello world.txt"), "hello webdav");
        Directory.CreateDirectory(Path.Combine(_root, "子目录"));
        await File.WriteAllTextAsync(Path.Combine(_root, "子目录", "nested.txt"), "nested");

        _port = GetFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _serverTask = Task.Run(ServerLoopAsync);

        var profile = new ConnectionProfile
        {
            Name = "Test WebDAV",
            Protocol = RemoteProtocol.WebDav,
            Host = "127.0.0.1",
            Port = _port,
            Secure = false,
            InitialPath = "/",
            Username = string.Empty,
        };
        _fs = await WebDavFileSystem.ConnectAsync(profile, string.Empty);
    }

    public async Task DisposeAsync()
    {
        if (_fs is not null)
            await _fs.DisposeAsync();
        if (_listener is not null)
        {
            try { _listener.Stop(); } catch { /* ignore */ }
            _listener.Close();
        }
        if (_serverTask is not null)
        {
            try { await _serverTask; } catch { /* ignore */ }
        }
        try { Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task ListsFilesAndDirectories()
    {
        var entries = await _fs!.ListAsync("/");
        Assert.Contains(entries, e => e.Name == "hello world.txt" && e.Size == 12);
        Assert.Contains(entries, e => e.Name == "子目录" && e.IsDirectory);
    }

    [Fact]
    public async Task MissingEntry_ReturnsNull()
    {
        Assert.Null(await _fs!.GetEntryAsync("/does-not-exist.txt"));
    }

    [Fact]
    public async Task ReadWriteMkdirCopyMoveDelete()
    {
        Assert.Equal("hello webdav", await ReadTextAsync("/hello world.txt"));

        await using (var stream = await _fs!.OpenWriteAsync("/new file.txt", false))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("new content"));
        }
        Assert.Equal("new content", await ReadTextAsync("/new file.txt"));

        await _fs!.CreateDirectoryAsync("/folder");
        Assert.True(await _fs.ExistsAsync("/folder"));

        await _fs.CopyFileAsync("/new file.txt", "/folder/copy.txt", true);
        Assert.Equal("new content", await ReadTextAsync("/folder/copy.txt"));

        await _fs.MoveAsync("/folder/copy.txt", "/folder/moved.txt");
        Assert.False(await _fs.ExistsAsync("/folder/copy.txt"));
        Assert.True(await _fs.ExistsAsync("/folder/moved.txt"));

        await _fs.DeleteAsync("/folder", true);
        Assert.False(await _fs.ExistsAsync("/folder"));
        Assert.True(await _fs.ExistsAsync("/new file.txt"));
    }

    private async Task<string> ReadTextAsync(string path)
    {
        await using var stream = await _fs!.OpenReadAsync(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task ServerLoopAsync()
    {
        while (_listener?.IsListening == true)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { break; }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            var method = request.HttpMethod.ToUpperInvariant();
            var path = Uri.UnescapeDataString(request.Url?.AbsolutePath ?? "/");
            path = path.TrimEnd('/');
            if (path.Length == 0)
                path = "/";
            var local = ToLocal(path);

            switch (method)
            {
                case "PROPFIND":
                {
                    if (local is null || (!Directory.Exists(local) && !File.Exists(local)))
                    {
                        response.StatusCode = 404;
                        break;
                    }

                    var depth = request.Headers["Depth"] ?? "0";
                    var entries = new List<string> { PropEntry(path, local) };
                    if (depth != "0" && Directory.Exists(local))
                    {
                        foreach (var info in new DirectoryInfo(local).EnumerateFileSystemInfos())
                            entries.Add(PropEntry(path == "/" ? "/" + info.Name : path + "/" + info.Name, info.FullName));
                    }

                    var xml = $"""<?xml version="1.0" encoding="utf-8"?><d:multistatus xmlns:d="DAV:">{string.Join("", entries)}</d:multistatus>""";
                    var bytes = Encoding.UTF8.GetBytes(xml);
                    response.StatusCode = 207;
                    response.ContentType = "application/xml; charset=utf-8";
                    response.ContentLength64 = bytes.Length;
                    await response.OutputStream.WriteAsync(bytes);
                    break;
                }
                case "GET":
                {
                    if (local is null || !File.Exists(local))
                    {
                        response.StatusCode = 404;
                        break;
                    }
                    var bytes = await File.ReadAllBytesAsync(local);
                    response.StatusCode = 200;
                    response.ContentLength64 = bytes.Length;
                    await response.OutputStream.WriteAsync(bytes);
                    break;
                }
                case "PUT":
                {
                    if (local is null)
                    {
                        response.StatusCode = 400;
                        break;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    await using var output = File.Create(local);
                    await request.InputStream.CopyToAsync(output);
                    response.StatusCode = 201;
                    break;
                }
                case "MKCOL":
                {
                    if (local is null)
                    {
                        response.StatusCode = 400;
                        break;
                    }
                    if (Directory.Exists(local) || File.Exists(local))
                    {
                        response.StatusCode = 405;
                        break;
                    }
                    Directory.CreateDirectory(local);
                    response.StatusCode = 201;
                    break;
                }
                case "DELETE":
                {
                    if (local is null || (!Directory.Exists(local) && !File.Exists(local)))
                    {
                        response.StatusCode = 404;
                        break;
                    }
                    if (Directory.Exists(local))
                        Directory.Delete(local, true);
                    else
                        File.Delete(local);
                    response.StatusCode = 204;
                    break;
                }
                case "MOVE":
                case "COPY":
                {
                    var destination = request.Headers["Destination"];
                    if (local is null || destination is null)
                    {
                        response.StatusCode = 400;
                        break;
                    }
                    var destinationUri = new Uri(destination);
                    var destinationPath = Uri.UnescapeDataString(destinationUri.AbsolutePath).TrimEnd('/');
                    if (destinationPath.Length == 0)
                        destinationPath = "/";
                    var destinationLocal = ToLocal(destinationPath);
                    if (destinationLocal is null)
                    {
                        response.StatusCode = 400;
                        break;
                    }

                    var overwrite = !string.Equals(request.Headers["Overwrite"], "F", StringComparison.OrdinalIgnoreCase);
                    if (!overwrite && (File.Exists(destinationLocal) || Directory.Exists(destinationLocal)))
                    {
                        response.StatusCode = 412;
                        break;
                    }
                    if (Directory.Exists(destinationLocal))
                        Directory.Delete(destinationLocal, true);
                    else if (File.Exists(destinationLocal))
                        File.Delete(destinationLocal);

                    if (Directory.Exists(local))
                    {
                        CopyDirectory(local, destinationLocal);
                        if (method == "MOVE")
                            Directory.Delete(local, true);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationLocal)!);
                        File.Copy(local, destinationLocal, true);
                        if (method == "MOVE")
                            File.Delete(local);
                    }
                    response.StatusCode = 201;
                    break;
                }
                default:
                    response.StatusCode = 405;
                    break;
            }
        }
        catch
        {
            try { context.Response.StatusCode = 500; } catch { /* ignore */ }
        }
        finally
        {
            try { context.Response.Close(); } catch { /* ignore */ }
        }
    }

    private string PropEntry(string path, string local)
    {
        var isDirectory = Directory.Exists(local);
        var href = path == "/"
            ? "/"
            : string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        if (isDirectory && !href.EndsWith('/'))
            href += "/";

        var name = path == "/" ? "root" : PosixPath.Name(path);
        var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(local) : new FileInfo(local);
        var resourceType = isDirectory ? "<d:resourcetype><d:collection/></d:resourcetype>" : "<d:resourcetype/>";
        var size = isDirectory ? string.Empty : $"<d:getcontentlength>{((FileInfo)info).Length}</d:getcontentlength>";
        var mime = isDirectory ? string.Empty : $"<d:getcontenttype>text/plain</d:getcontenttype>";
        return $"""
            <d:response>
              <d:href>{href}</d:href>
              <d:propstat>
                <d:prop>
                  <d:displayname>{System.Security.SecurityElement.Escape(name)}</d:displayname>
                  {resourceType}{size}
                  <d:getlastmodified>{info.LastWriteTimeUtc:R}</d:getlastmodified>{mime}
                </d:prop>
                <d:status>HTTP/1.1 200 OK</d:status>
              </d:propstat>
            </d:response>
            """;
    }

    private string? ToLocal(string path)
    {
        var relative = Uri.UnescapeDataString(path).TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        var prefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal) || full == _root ? full : null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
