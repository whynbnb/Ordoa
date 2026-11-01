using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.FileSystems.Remote;

/// <summary>
/// WebDAV provider implemented on top of HttpClient (PROPFIND / GET / PUT / MKCOL /
/// DELETE / MOVE / COPY). Works with Apache mod_dav, Nextcloud, ownCloud, Synology,
/// box.com and most other WebDAV servers.
/// </summary>
public sealed class WebDavFileSystem : FileSystemBase
{
    private static readonly XNamespace D = "DAV:";
    private const string PropFindBody =
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:displayname/><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getcontenttype/></d:prop></d:propfind>""";

    private readonly HttpClient _http;
    private readonly ConnectionProfile _profile;
    private readonly string _root;
    private readonly string _baseUrl;

    private WebDavFileSystem(ConnectionProfile profile, HttpClient http)
        : base(profile.Id, profile.Scheme, profile.Name)
    {
        _profile = profile;
        _http = http;
        _root = PosixPath.Normalize(profile.InitialPath);
        _baseUrl = $"{(profile.Secure ? "https" : "http")}://{profile.Host}:{profile.EffectivePort}";
    }

    public override string RootPath => _root;
    public override bool IsCaseSensitive => true;

    public override FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.List | FileSystemCapabilities.Read | FileSystemCapabilities.Write |
        FileSystemCapabilities.CreateDirectory | FileSystemCapabilities.Delete | FileSystemCapabilities.Rename |
        FileSystemCapabilities.Move | FileSystemCapabilities.Copy | FileSystemCapabilities.ServerSideCopy;

    public static Task<WebDavFileSystem> ConnectAsync(ConnectionProfile profile, string password,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                PreAuthenticate = false,
            };
            if (profile.AllowInvalidCertificate)
                handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;

            var http = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan,
                DefaultRequestVersion = HttpVersion.Version11,
            };

            if (!profile.Anonymous && (!string.IsNullOrEmpty(profile.Username) || !string.IsNullOrEmpty(password)))
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{profile.Username}:{password}"));
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }

            http.DefaultRequestHeaders.UserAgent.ParseAdd("Ordoa/0.1 (WebDAV)");

            var fs = new WebDavFileSystem(profile, http);
            try
            {
                // Validate credentials and the root path before handing the filesystem to the UI.
                _ = await fs.PropFindAsync(fs._root, 0, cancellationToken).ConfigureAwait(false);
                return fs;
            }
            catch
            {
                http.Dispose();
                throw;
            }
        }, cancellationToken);
    }

    public override string NormalizePath(string path)
    {
        var normalized = PosixPath.Normalize(path);
        if (normalized != _root && !normalized.StartsWith(_root.TrimEnd('/') + "/", StringComparison.Ordinal))
        {
            normalized = PosixPath.Combine(_root, normalized.TrimStart('/'));
        }
        return normalized;
    }

    public override bool IsRoot(string path) => string.Equals(NormalizePath(path), _root, StringComparison.Ordinal);

    public override string GetParent(string path)
    {
        path = NormalizePath(path);
        return path == _root ? _root : PosixPath.Parent(path);
    }

    public override string GetDisplayPath(string path)
        => $"{_profile.DisplayTarget}{NormalizePath(path)}";

    public override async Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        var responses = await PropFindAsync(path, 1, cancellationToken).ConfigureAwait(false);
        var result = new List<FileEntry>(responses.Count);

        foreach (var response in responses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = response.ToEntry();
            if (entry is null)
                continue;
            if (string.Equals(entry.Path, path, StringComparison.Ordinal))
                continue;
            if (!string.Equals(PosixPath.Parent(entry.Path), path, StringComparison.Ordinal))
                continue;
            result.Add(entry);
        }

        return result;
    }

    public override async Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        try
        {
            var responses = await PropFindAsync(path, 0, cancellationToken).ConfigureAwait(false);
            return responses.Select(r => r.ToEntry()).FirstOrDefault(e => e is not null && string.Equals(e.Path, path, StringComparison.Ordinal))
                   ?? responses.Select(r => r.ToEntry()).FirstOrDefault(e => e is not null);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public override async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var responses = await PropFindAsync(NormalizePath(path), 0, cancellationToken).ConfigureAwait(false);
            return responses.Count > 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public override async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(NormalizePath(path)));
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new FileNotFoundException("远程文件不存在", path);
        }
        EnsureSuccess(response);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new HttpResponseStream(stream, response);
    }

    public override async Task<Stream> OpenWriteAsync(string path, bool overwrite, long? expectedLength = null,
        CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        if (!overwrite && await ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            throw new IOException("目标文件已存在");

        Directory.CreateDirectory(AppPaths.CacheDirectory);
        return new WebDavUploadStream(_http, BuildUri(path), cancellationToken);
    }

    public override async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), BuildUri(path));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            if (await ExistsAsync(path, cancellationToken).ConfigureAwait(false))
                return;
        }
        EnsureSuccess(response);
    }

    public override async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        using var request = new HttpRequestMessage(HttpMethod.Delete, BuildUri(path));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        EnsureSuccess(response);
    }

    public override async Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        sourcePath = NormalizePath(sourcePath);
        destinationPath = NormalizePath(destinationPath);
        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), BuildUri(sourcePath));
        request.Headers.TryAddWithoutValidation("Destination", BuildUri(destinationPath));
        request.Headers.TryAddWithoutValidation("Overwrite", "T");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public override async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        sourcePath = NormalizePath(sourcePath);
        destinationPath = NormalizePath(destinationPath);

        if (!overwrite && await ExistsAsync(destinationPath, cancellationToken).ConfigureAwait(false))
            throw new IOException("目标文件已存在");

        var source = await GetEntryAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(new HttpMethod("COPY"), BuildUri(sourcePath));
        request.Headers.TryAddWithoutValidation("Destination", BuildUri(destinationPath));
        request.Headers.TryAddWithoutValidation("Overwrite", overwrite ? "T" : "F");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            // Fall back to a streamed copy for servers without COPY support.
            await using var src = await OpenReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            await using var dst = await OpenWriteAsync(destinationPath, overwrite,
                source?.Size, cancellationToken).ConfigureAwait(false);
            await CopyStreamAsync(src, dst, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        EnsureSuccess(response);
        if (source is not null)
            progress?.Report(source.Size);
    }

    public override async Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken = default)
    {
        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:propertyupdate xmlns:d="DAV:"><d:set><d:prop><d:getlastmodified>{modified.ToUniversalTime():R}</d:getlastmodified></d:prop></d:set></d:propertyupdate>
            """;
        using var request = new HttpRequestMessage(new HttpMethod("PROPPATCH"), BuildUri(NormalizePath(path)))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        // Many WebDAV servers refuse to set modification times; treat that as a soft failure.
    }

    public override async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await Task.CompletedTask;
    }

    private string BuildUri(string path) => _baseUrl + PosixPath.EscapePath(NormalizePath(path));

    private async Task<List<DavResponse>> PropFindAsync(string path, int depth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), BuildUri(path))
        {
            Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml"),
        };
        request.Headers.TryAddWithoutValidation("Depth", depth.ToString());

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound)
            throw new FileNotFoundException("远程路径不存在", path);
        if (response.StatusCode is HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("WebDAV 认证失败，请检查用户名和密码");
        EnsureSuccess(response);

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(text);
        var results = new List<DavResponse>();

        foreach (var element in document.Descendants(D + "response"))
        {
            var href = (string?)element.Element(D + "href");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            XElement? prop = null;
            foreach (var propstat in element.Elements(D + "propstat"))
            {
                var status = (string?)propstat.Element(D + "status") ?? string.Empty;
                if (status.Contains("200", StringComparison.Ordinal) || string.IsNullOrEmpty(status))
                {
                    prop = propstat.Element(D + "prop");
                    if (prop is not null)
                        break;
                }
            }

            prop ??= element.Element(D + "prop");
            if (prop is null)
                continue;

            var pathValue = HrefToPath(href);
            if (pathValue is null)
                continue;

            results.Add(new DavResponse(pathValue, prop));
        }

        return results;
    }

    private string? HrefToPath(string href)
    {
        try
        {
            Uri uri;
            if (href.Contains("://", StringComparison.Ordinal) &&
                Uri.TryCreate(href, UriKind.Absolute, out var absolute))
                uri = absolute;
            else
                uri = new Uri(new Uri(_baseUrl), href);
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            return NormalizePath(path);
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException($"WebDAV 拒绝访问 ({(int)response.StatusCode} {response.ReasonPhrase})");
        throw new HttpRequestException($"WebDAV 请求失败 ({(int)response.StatusCode} {response.ReasonPhrase})");
    }

    private sealed record DavResponse(string Path, XElement Prop)
    {
        public FileEntry? ToEntry()
        {
            var name = (string?)Prop.Element(D + "displayname");
            if (string.IsNullOrWhiteSpace(name))
                name = PosixPath.Name(Path);
            if (string.IsNullOrWhiteSpace(name))
                name = Path;

            var resourceType = Prop.Element(D + "resourcetype");
            var isDirectory = resourceType?.Elements(D + "collection").Any() == true;

            long size = 0;
            if (!isDirectory && long.TryParse((string?)Prop.Element(D + "getcontentlength"), out var parsed))
                size = parsed;

            DateTimeOffset? modified = null;
            var modifiedText = (string?)Prop.Element(D + "getlastmodified");
            if (!string.IsNullOrWhiteSpace(modifiedText) && DateTimeOffset.TryParse(modifiedText, out var modifiedValue))
                modified = modifiedValue;

            var contentType = (string?)Prop.Element(D + "getcontenttype");
            var extension = GetExtension(name);

            return new FileEntry
            {
                Name = name,
                Path = Path,
                Kind = isDirectory ? EntryKind.Directory : EntryKind.File,
                Size = size,
                Modified = modified,
                IsHidden = name.StartsWith('.'),
                MimeType = contentType ?? (isDirectory ? null : MimeTypes.FromExtension(extension)),
            };
        }

        private static string GetExtension(string name)
        {
            var dot = name.LastIndexOf('.');
            return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..];
        }
    }
}

internal sealed class HttpResponseStream : Stream
{
    private readonly Stream _inner;
    private readonly HttpResponseMessage _response;

    public HttpResponseStream(Stream inner, HttpResponseMessage response)
    {
        _inner = inner;
        _response = response;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _response.Dispose();
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Buffers writes to a temporary file and uploads it with a single PUT when disposed.
/// This keeps PUT requests compatible with servers that require Content-Length.
/// </summary>
internal sealed class WebDavUploadStream : Stream
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly CancellationToken _cancellationToken;
    private readonly string _tempPath;
    private readonly FileStream _file;
    private bool _disposed;

    public WebDavUploadStream(HttpClient http, string url, CancellationToken cancellationToken)
    {
        _http = http;
        _url = url;
        _cancellationToken = cancellationToken;
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        _tempPath = Path.Combine(AppPaths.CacheDirectory, "upload-" + Guid.NewGuid().ToString("N") + ".tmp");
        _file = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(_tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* best effort */ }
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _file.Length;
    public override long Position
    {
        get => _file.Position;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => _file.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _file.WriteAsync(buffer, cancellationToken);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _file.WriteAsync(buffer, offset, count, cancellationToken);

    public override void Flush() => _file.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _file.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            await _file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await _file.DisposeAsync().ConfigureAwait(false);

            await using var upload = new FileStream(_tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var content = new StreamContent(upload);
            content.Headers.ContentLength = upload.Length;
            using var response = await _http.PutAsync(_url, content, _cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"WebDAV 上传失败 ({(int)response.StatusCode} {response.ReasonPhrase})");
        }
        finally
        {
            try { File.Delete(_tempPath); } catch { /* ignore */ }
        }
    }
}
