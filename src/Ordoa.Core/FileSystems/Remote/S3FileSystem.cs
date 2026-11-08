using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using Ordoa.Core.Abstractions;
using Ordoa.Core.Localization;
using Ordoa.Core.Models;
using Ordoa.Core.Util;

namespace Ordoa.Core.FileSystems.Remote;

/// <summary>
/// Lightweight S3-compatible object storage provider. It signs requests with
/// AWS Signature V4 and works with AWS S3, MinIO, Cloudflare R2 and B2's
/// S3-compatible endpoint. No AWS SDK or native dependency is required.
/// </summary>
public sealed class S3FileSystem : FileSystemBase
{
    private readonly HttpClient _http;
    private readonly string _bucket;
    private readonly string _region;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string _sessionToken;
    private readonly string _endpoint;
    private readonly bool _pathStyle;
    private readonly string _rootPrefix;

    private S3FileSystem(ConnectionProfile profile, string password)
        : base(profile.Id, profile.Scheme, profile.Name)
    {
        _bucket = profile.S3Bucket.Trim();
        _region = string.IsNullOrWhiteSpace(profile.S3Region) ? "us-east-1" : profile.S3Region.Trim();
        _accessKey = profile.Username.Trim();
        _secretKey = password;
        _sessionToken = profile.S3SessionToken.Trim();
        _endpoint = NormalizeEndpoint(profile.Host, profile.Secure);
        _pathStyle = profile.S3UsePathStyle;
        _rootPrefix = NormalizePrefix(profile.NormalizeInitialPath(profile.InitialPath));
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public override FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.List | FileSystemCapabilities.Read | FileSystemCapabilities.Write |
        FileSystemCapabilities.CreateDirectory | FileSystemCapabilities.Delete |
        FileSystemCapabilities.Rename | FileSystemCapabilities.Move | FileSystemCapabilities.Copy;

    public static Task<S3FileSystem> ConnectAsync(ConnectionProfile profile, string password,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(profile.S3Bucket))
                throw new ArgumentException(L10n.T("S3 Bucket 不能为空"));
            if (string.IsNullOrWhiteSpace(profile.Host))
                throw new ArgumentException(L10n.T("S3 端点不能为空"));
            if (string.IsNullOrWhiteSpace(profile.Username))
                throw new ArgumentException(L10n.T("Access Key 不能为空"));

            return new S3FileSystem(profile, password);
        }, cancellationToken);
    }

    public override string GetDisplayPath(string path)
        => $"s3://{_bucket}{NormalizePath(path)}";

    public override async Task<IReadOnlyList<FileEntry>> ListAsync(string path,
        CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        var result = new List<FileEntry>();
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);
        var prefix = path == "/" ? _rootPrefix : CombineKey(KeyForPath(path), string.Empty);
        if (path != "/")
            prefix = prefix.TrimEnd('/') + "/";

        string? continuation = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = BuildListQuery(prefix, delimiter: "/", continuation);
            using var response = await SendAsync(HttpMethod.Get, string.Empty, query, null,
                AwsSignatureV4.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
            var xml = await ReadXmlAsync(response, cancellationToken).ConfigureAwait(false);
            var root = xml.Root ?? throw new IOException(L10n.T("S3 返回了无效的列表响应"));

            foreach (var common in root.Elements().Where(element => element.Name.LocalName == "CommonPrefixes"))
            {
                var value = Value(common, "Prefix");
                var relative = RelativeKey(value);
                if (relative.Length == 0 || !seenPrefixes.Add(relative))
                    continue;
                var name = relative.TrimEnd('/').Split('/').Last();
                result.Add(new FileEntry
                {
                    Name = name,
                    Path = Combine(path, name),
                    Kind = EntryKind.Directory,
                    IsHidden = name.StartsWith('.'),
                });
            }

            foreach (var content in root.Elements().Where(element => element.Name.LocalName == "Contents"))
            {
                var key = Value(content, "Key");
                if (key.Length == 0 || key.EndsWith("/", StringComparison.Ordinal) ||
                    string.Equals(key, prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = RelativeKey(key);
                if (relative.Length == 0 || relative.Contains('/'))
                    continue;
                var name = relative;
                long.TryParse(Value(content, "Size"), out var size);
                var modified = DateTimeOffset.TryParse(Value(content, "LastModified"),
                    out var parsedModified)
                    ? parsedModified
                    : (DateTimeOffset?)null;
                result.Add(new FileEntry
                {
                    Name = name,
                    Path = Combine(path, name),
                    Kind = EntryKind.File,
                    Size = size,
                    Modified = modified,
                    IsHidden = name.StartsWith('.'),
                });
            }

            continuation = Value(root, "NextContinuationToken");
            var truncated = string.Equals(Value(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase);
            if (!truncated)
                continuation = null;
        } while (!string.IsNullOrEmpty(continuation));

        return result
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, NaturalStringComparer.Instance)
            .ToList();
    }

    public override async Task<FileEntry?> GetEntryAsync(string path,
        CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        if (IsRoot(path))
        {
            return new FileEntry
            {
                Name = _bucket,
                Path = "/",
                Kind = EntryKind.Directory,
            };
        }

        var key = KeyForPath(path);
        var head = await SendAsync(HttpMethod.Head, key, null, null,
            AwsSignatureV4.EmptyPayloadHash, cancellationToken, allowMissing: true).ConfigureAwait(false);
        if (head.IsSuccessStatusCode)
        {
            var size = head.Content.Headers.ContentLength ?? 0;
            var modified = head.Content.Headers.LastModified;
            head.Dispose();
            return new FileEntry
            {
                Name = GetName(path),
                Path = path,
                Kind = EntryKind.File,
                Size = size,
                Modified = modified,
                IsHidden = GetName(path).StartsWith('.'),
            };
        }

        head.Dispose();
        if (await IsDirectoryAsync(key, cancellationToken).ConfigureAwait(false))
        {
            return new FileEntry
            {
                Name = GetName(path),
                Path = path,
                Kind = EntryKind.Directory,
                IsHidden = GetName(path).StartsWith('.'),
            };
        }

        return null;
    }

    public override async Task<bool> ExistsAsync(string path,
        CancellationToken cancellationToken = default)
        => await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) is not null;

    public override async Task<Stream> OpenReadAsync(string path,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, KeyForPath(path), null, null,
            AwsSignatureV4.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new ResponseStream(stream, response);
    }

    public override async Task<Stream> OpenWriteAsync(string path, bool overwrite,
        long? expectedLength = null, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        if (!overwrite && await ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            throw new IOException(L10n.T("目标文件已存在"));

        var directory = Path.Combine(AppPaths.CacheDirectory, "s3-upload");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        return new S3UploadStream(this, KeyForPath(path), temp, cancellationToken);
    }

    public override async Task CreateDirectoryAsync(string path,
        CancellationToken cancellationToken = default)
    {
        var key = KeyForPath(path).TrimEnd('/') + "/";
        using var content = new ByteArrayContent([]);
        using var response = await SendAsync(HttpMethod.Put, key, null, content,
            AwsSignatureV4.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
    }

    public override async Task DeleteAsync(string path, bool recursive,
        CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        if (IsRoot(path))
            throw new InvalidOperationException(L10n.T("不能删除 Bucket 根目录"));
        if (!recursive)
        {
            await DeleteObjectAsync(KeyForPath(path), cancellationToken).ConfigureAwait(false);
            return;
        }

        var prefix = KeyForPath(path).TrimEnd('/') + "/";
        var keys = await ListKeysAsync(prefix, cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
            await DeleteObjectAsync(key, cancellationToken).ConfigureAwait(false);
        await DeleteObjectAsync(KeyForPath(path), cancellationToken).ConfigureAwait(false);
    }

    public override async Task MoveAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        await CopyFileAsync(sourcePath, destinationPath, overwrite: false, null, cancellationToken)
            .ConfigureAwait(false);
        await DeleteObjectAsync(KeyForPath(sourcePath), cancellationToken).ConfigureAwait(false);
    }

    public override async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!overwrite && await ExistsAsync(destinationPath, cancellationToken).ConfigureAwait(false))
            throw new IOException(L10n.T("目标文件已存在"));

        var sourceKey = KeyForPath(sourcePath);
        var destinationKey = KeyForPath(destinationPath);
        using var request = new HttpRequestMessage(HttpMethod.Put, BuildUri(destinationKey));
        request.Headers.TryAddWithoutValidation("x-amz-copy-source",
            "/" + _bucket + "/" + EscapeKey(sourceKey));
        AwsSignatureV4.Sign(request, _region, _accessKey, _secretKey, _sessionToken,
            AwsSignatureV4.EmptyPayloadHash);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public override Task SetModifiedAsync(string path, DateTimeOffset modified,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async Task UploadFileAsync(string key, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(HttpMethod.Put, key, null, content,
            "UNSIGNED-PAYLOAD", cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteObjectAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, key, null, null,
            AwsSignatureV4.EmptyPayloadHash, cancellationToken, allowMissing: true).ConfigureAwait(false);
    }

    private async Task<bool> IsDirectoryAsync(string key, CancellationToken cancellationToken)
    {
        var prefix = key.TrimEnd('/') + "/";
        var query = BuildListQuery(prefix, delimiter: "/", continuation: null);
        using var response = await SendAsync(HttpMethod.Get, string.Empty, query, null,
            AwsSignatureV4.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
        var xml = await ReadXmlAsync(response, cancellationToken).ConfigureAwait(false);
        var root = xml.Root;
        return root is not null && root.Elements().Any(element =>
            element.Name.LocalName is "Contents" or "CommonPrefixes");
    }

    private async Task<List<string>> ListKeysAsync(string prefix, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        string? continuation = null;
        do
        {
            var query = BuildListQuery(prefix, delimiter: null, continuation);
            using var response = await SendAsync(HttpMethod.Get, string.Empty, query, null,
                AwsSignatureV4.EmptyPayloadHash, cancellationToken).ConfigureAwait(false);
            var xml = await ReadXmlAsync(response, cancellationToken).ConfigureAwait(false);
            var root = xml.Root;
            if (root is null)
                break;
            foreach (var content in root.Elements().Where(element => element.Name.LocalName == "Contents"))
            {
                var key = Value(content, "Key");
                if (key.Length > 0)
                    keys.Add(key);
            }

            continuation = string.Equals(Value(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase)
                ? Value(root, "NextContinuationToken")
                : null;
        } while (!string.IsNullOrEmpty(continuation));

        return keys;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string key, string? query,
        HttpContent? content, string payloadHash, CancellationToken cancellationToken,
        bool allowMissing = false)
    {
        using var request = new HttpRequestMessage(method, BuildUri(key, query)) { Content = content };
        AwsSignatureV4.Sign(request, _region, _accessKey, _secretKey, _sessionToken, payloadHash);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (allowMissing && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            return new HttpResponseMessage(response.StatusCode);
        }
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = body;
        try
        {
            var xml = XDocument.Parse(body);
            var code = Value(xml.Root, "Code");
            var detail = Value(xml.Root, "Message");
            if (code.Length > 0 || detail.Length > 0)
                message = $"{code} {detail}".Trim();
        }
        catch
        {
            // Keep the raw response body for non-XML gateways.
        }
        response.Dispose();
        throw new IOException($"S3 {(int)response.StatusCode} {response.ReasonPhrase}: {message}".Trim());
    }

    private static async Task<XDocument> ReadXmlAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private string KeyForPath(string path)
    {
        var relative = NormalizePath(path).Trim('/');
        return CombineKey(_rootPrefix, relative);
    }

    private string RelativeKey(string key)
    {
        if (_rootPrefix.Length == 0)
            return key.TrimStart('/');
        return key.StartsWith(_rootPrefix, StringComparison.Ordinal)
            ? key[_rootPrefix.Length..].TrimStart('/')
            : key.TrimStart('/');
    }

    private Uri BuildUri(string key, string? query = null)
    {
        var baseUri = new Uri(_endpoint, UriKind.Absolute);
        var builder = new UriBuilder(baseUri);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        if (_pathStyle)
        {
            builder.Path = basePath + "/" + _bucket + (key.Length > 0 ? "/" + EscapeKey(key) : string.Empty);
        }
        else
        {
            builder.Host = _bucket + "." + baseUri.Host;
            builder.Path = basePath + (key.Length > 0 ? "/" + EscapeKey(key) : string.Empty);
        }
        builder.Query = query ?? string.Empty;
        return builder.Uri;
    }

    private string BuildListQuery(string prefix, string? delimiter, string? continuation)
    {
        var parts = new List<string>
        {
            "list-type=2",
            "max-keys=1000",
            "prefix=" + Uri.EscapeDataString(prefix),
        };
        if (delimiter is not null)
            parts.Add("delimiter=" + Uri.EscapeDataString(delimiter));
        if (!string.IsNullOrEmpty(continuation))
            parts.Add("continuation-token=" + Uri.EscapeDataString(continuation));
        return string.Join('&', parts);
    }

    private static string NormalizeEndpoint(string host, bool secure)
    {
        host = host.Trim();
        if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            host = (secure ? "https://" : "http://") + host;
        }
        return host.TrimEnd('/');
    }

    private static string NormalizePrefix(string path)
    {
        var prefix = path.Trim('/');
        return prefix.Length == 0 ? string.Empty : prefix + "/";
    }

    private static string CombineKey(string prefix, string relative)
        => (prefix + relative.Trim('/')).TrimStart('/');

    private static string EscapeKey(string key)
        => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    private static string Value(XElement? element, string name)
        => element?.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value ?? string.Empty;

    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class S3UploadStream : Stream
    {
        private readonly S3FileSystem _owner;
        private readonly string _key;
        private readonly string _path;
        private readonly CancellationToken _cancellationToken;
        private readonly FileStream _inner;
        private bool _uploaded;

        public S3UploadStream(S3FileSystem owner, string key, string path, CancellationToken cancellationToken)
        {
            _owner = owner;
            _key = key;
            _path = path;
            _cancellationToken = cancellationToken;
            _inner = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _inner.Dispose();
                    if (!_uploaded)
                        _owner.UploadFileAsync(_key, _path, _cancellationToken).GetAwaiter().GetResult();
                }
                finally
                {
                    TryDelete();
                }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
                if (!_uploaded)
                {
                    _uploaded = true;
                    await _owner.UploadFileAsync(_key, _path, _cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                TryDelete();
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private void TryDelete()
        {
            try { File.Delete(_path); } catch { /* best effort */ }
        }
    }
}
