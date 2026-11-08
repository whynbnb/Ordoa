using System.Net;
using System.Text;
using Ordoa.Core.FileSystems.Remote;
using Ordoa.Core.Models;

namespace Ordoa.Core.Tests;

public sealed class S3FileSystemTests
{
    [Fact]
    public async Task ListReadWriteDelete_UsesS3CompatibleRequests()
    {
        using var server = new FakeS3Server();
        var profile = new ConnectionProfile
        {
            Name = "test",
            Protocol = RemoteProtocol.S3,
            Host = server.Endpoint,
            S3Bucket = "bucket",
            S3Region = "us-east-1",
            Username = "access",
            S3UsePathStyle = true,
            Secure = false,
            InitialPath = "/",
        };

        await using var s3 = await S3FileSystem.ConnectAsync(profile, "secret");
        var root = await s3.ListAsync("/");
        Assert.Contains(root, entry => entry is { Name: "folder", IsDirectory: true });
        Assert.Contains(root, entry => entry is { Name: "alpha.txt", IsDirectory: false, Size: 5 });

        await using (var stream = await s3.OpenReadAsync("/alpha.txt"))
        {
            using var reader = new StreamReader(stream);
            Assert.Equal("alpha", await reader.ReadToEndAsync());
        }

        await using (var stream = await s3.OpenWriteAsync("/upload.txt", overwrite: true))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("uploaded"));
        }

        Assert.True(server.LastUploadHasSignature);
        Assert.Equal("uploaded", server.LastUploadBody);

        await s3.CreateDirectoryAsync("/new-folder");
        Assert.Contains("bucket/new-folder/", server.Keys);
    }

    private sealed class FakeS3Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal) { "bucket/alpha.txt" };

        public FakeS3Server()
        {
            var port = FreePort();
            Endpoint = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Endpoint + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string Endpoint { get; }
        public IReadOnlySet<string> Keys => _keys;
        public string LastUploadBody { get; private set; } = string.Empty;
        public bool LastUploadHasSignature { get; private set; }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var rawPath = request.Url?.AbsolutePath ?? string.Empty;
                var path = rawPath.Trim('/');
                LastUploadHasSignature = request.Headers["Authorization"]?.StartsWith(
                    "AWS4-HMAC-SHA256", StringComparison.Ordinal) == true;
                if (request.HttpMethod == "GET" && path == "bucket")
                {
                    await WriteXmlAsync(context.Response,
                        """
                        <?xml version="1.0" encoding="UTF-8"?>
                        <ListBucketResult>
                          <IsTruncated>false</IsTruncated>
                          <Contents><Key>alpha.txt</Key><Size>5</Size><LastModified>2024-01-01T00:00:00.000Z</LastModified></Contents>
                          <CommonPrefixes><Prefix>folder/</Prefix></CommonPrefixes>
                        </ListBucketResult>
                        """);
                    return;
                }

                if (request.HttpMethod == "GET" && path == "bucket/alpha.txt")
                {
                    var bytes = Encoding.UTF8.GetBytes("alpha");
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                    return;
                }

                if (request.HttpMethod == "HEAD" && path == "bucket/alpha.txt")
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentLength64 = 5;
                    context.Response.Close();
                    return;
                }

                if (request.HttpMethod == "PUT" && path == "bucket/upload.txt")
                {
                    using var reader = new StreamReader(request.InputStream);
                    LastUploadBody = await reader.ReadToEndAsync();
                    _keys.Add("bucket/upload.txt");
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                    return;
                }

                if (request.HttpMethod == "PUT" && rawPath.EndsWith('/'))
                {
                    _keys.Add(path + "/");
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                    return;
                }

                if (request.HttpMethod == "DELETE")
                {
                    _keys.Remove(path);
                    context.Response.StatusCode = 204;
                    context.Response.Close();
                    return;
                }

                context.Response.StatusCode = 404;
                context.Response.Close();
            }
            catch
            {
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { /* ignore */ }
            }
        }

        private static async Task WriteXmlAsync(HttpListenerResponse response, string xml)
        {
            var bytes = Encoding.UTF8.GetBytes(xml);
            response.ContentType = "application/xml";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        private static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _loop.GetAwaiter().GetResult(); } catch { /* ignore */ }
            _listener.Close();
            _cts.Dispose();
        }
    }
}
