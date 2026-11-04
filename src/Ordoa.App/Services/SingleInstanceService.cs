using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ordoa.Core.Util;

namespace Ordoa.App.Services;

public sealed class ActivationRequest
{
    public List<string> Paths { get; set; } = [];
    public bool NewWindow { get; set; }
    public bool NewTab { get; set; }
    public bool NoSingleInstance { get; set; }
    public bool FileManagerService { get; set; }
    public bool RegisterFileManager { get; set; }
    public bool Undo { get; set; }
    public bool Redo { get; set; }

    public static ActivationRequest Parse(string[] args)
    {
        var request = new ActivationRequest();
        var flags = true;
        foreach (var argument in args)
        {
            if (flags && argument == "--")
            {
                flags = false;
                continue;
            }

            if (flags && argument.StartsWith('-'))
            {
                switch (argument)
                {
                    case "--new-window":
                    case "-n":
                        request.NewWindow = true;
                        break;
                    case "--new-tab":
                    case "-t":
                        request.NewTab = true;
                        break;
                    case "--no-single-instance":
                        request.NoSingleInstance = true;
                        break;
                    case "--file-manager-service":
                        request.FileManagerService = true;
                        break;
                    case "--register-file-manager":
                        request.RegisterFileManager = true;
                        break;
                    case "--undo":
                        request.Undo = true;
                        break;
                    case "--redo":
                        request.Redo = true;
                        break;
                }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(argument))
            {
                var path = argument;
                if (!path.Contains("://", StringComparison.Ordinal) && !Path.IsPathRooted(path))
                {
                    try { path = Path.GetFullPath(path); }
                    catch { /* keep the original value */ }
                }

                request.Paths.Add(path);
            }
        }

        return request;
    }
}

[JsonSerializable(typeof(ActivationRequest))]
internal sealed partial class ActivationJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Cross-platform single-instance forwarding using named pipes. The first
/// process owns the pipe; later launches send their paths and exit.
/// </summary>
public static class SingleInstanceService
{
    private static readonly object Sync = new();
    private static NamedPipeServerStream? _server;
    private static CancellationTokenSource? _serverCts;
    private static FileStream? _lockStream;
    private static bool _serverLoopStarted;

    private static string LockPath => Path.Combine(AppPaths.StateDirectory, "instance-" + InstanceKey + ".lock");

    private static string InstanceKey
    {
        get
        {
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.ConfigDirectory)));
            return hash[..16];
        }
    }

    private static string PipeName => "ordoa-" + InstanceKey;

    /// <summary>
    /// Claims the named pipe for this process. Returns false when another
    /// process already owns it.
    /// </summary>
    public static bool TryAcquireServer()
    {
        lock (Sync)
        {
            if (_server is not null)
                return true;

            if (_lockStream is null)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.StateDirectory);
                    _lockStream = new FileStream(
                        LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch
                {
                    return false;
                }
            }

            try
            {
                _server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                return true;
            }
            catch
            {
                _lockStream.Dispose();
                _lockStream = null;
                return false;
            }
        }
    }

    public static bool TryForward(ActivationRequest request, TimeSpan timeout)
    {
        NamedPipeClientStream? client = null;
        StreamWriter? writer = null;
        try
        {
            client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            client.Connect((int)Math.Clamp(timeout.TotalMilliseconds, 100, 10000));
            writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            writer.WriteLine(JsonSerializer.Serialize(request, ActivationJsonContext.Default.ActivationRequest));
            writer.Flush();

            // Closing a Unix named-pipe client can wait for the peer. The
            // forwarding process is about to exit, so let the OS close it and
            // clean up asynchronously for the rare fallback case.
            var completedWriter = writer;
            var completedClient = client;
            _ = Task.Run(async () =>
            {
                await Task.Delay(100).ConfigureAwait(false);
                try { completedWriter.Dispose(); } catch { /* ignore */ }
                try { completedClient.Dispose(); } catch { /* ignore */ }
            });
            return true;
        }
        catch
        {
            try { writer?.Dispose(); } catch { /* ignore */ }
            try { client?.Dispose(); } catch { /* ignore */ }
            return false;
        }
    }

    public static void StartServer(Func<ActivationRequest, Task> handler)
    {
        lock (Sync)
        {
            if (_server is null || _serverLoopStarted)
                return;

            _serverLoopStarted = true;
            var server = _server;
            _serverCts = new CancellationTokenSource();
            var token = _serverCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        break;
                    }

                    try
                    {
                        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                        var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            var request = JsonSerializer.Deserialize(
                                line, ActivationJsonContext.Default.ActivationRequest);
                            if (request is not null)
                                await handler(request).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Core.Services.Log.Warn("读取单实例激活请求失败", ex);
                    }
                    finally
                    {
                        try { server.Disconnect(); } catch { /* ignore */ }
                    }
                }
            }, token);
        }
    }

    public static async Task StopAsync()
    {
        NamedPipeServerStream? server;
        CancellationTokenSource? cts;
        FileStream? lockStream;

        lock (Sync)
        {
            server = _server;
            cts = _serverCts;
            lockStream = _lockStream;
            _server = null;
            _serverCts = null;
            _lockStream = null;
            _serverLoopStarted = false;
        }

        try { cts?.Cancel(); } catch { /* ignore */ }
        try { server?.Dispose(); } catch { /* ignore */ }
        try { lockStream?.Dispose(); } catch { /* ignore */ }
        cts?.Dispose();

        await Task.CompletedTask;
    }
}
