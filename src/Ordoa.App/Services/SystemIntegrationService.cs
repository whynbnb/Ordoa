using System.Diagnostics;
using System.Text;
using Ordoa.Core.Localization;
using Ordoa.Core.Util;

namespace Ordoa.App.Services;

public sealed record IntegrationResult(bool Success, string Message);

/// <summary>
/// Linux desktop integration: a user-level desktop entry, the
/// org.freedesktop.FileManager1 D-Bus activation file, and the inode/directory
/// MIME association.
/// </summary>
public sealed class SystemIntegrationService
{
    private const string DesktopFileName = "ordoa.desktop";
    private const string FileManagerServiceName = "org.freedesktop.FileManager1";

    public bool IsSupported => AppPaths.IsLinux;

    public string DataHome =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
            ? dataHome
            : Path.Combine(AppPaths.Home, ".local", "share");

    public string ApplicationsDirectory => Path.Combine(DataHome, "applications");
    public string DesktopFilePath => Path.Combine(ApplicationsDirectory, DesktopFileName);
    public string DbusServicesDirectory => Path.Combine(DataHome, "dbus-1", "services");
    public string DbusServiceFilePath => Path.Combine(DbusServicesDirectory, FileManagerServiceName + ".service");

    public async Task<IntegrationResult> RegisterAsDefaultFileManagerAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return new(false, L10n.T("仅 Linux 支持注册为默认文件管理器。"));

        try
        {
            Directory.CreateDirectory(ApplicationsDirectory);
            Directory.CreateDirectory(DbusServicesDirectory);

            var (exec, tryExec) = BuildLaunchCommand();
            await File.WriteAllTextAsync(DesktopFilePath, BuildDesktopEntry(exec, tryExec), new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(DbusServiceFilePath, BuildDbusService(exec), new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            var result = await RunAsync("xdg-mime",
                ["default", DesktopFileName, "inode/directory"], cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return new(false, L10n.Format("注册失败：{0}",
                    string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim()));
            }

            await RunAsync("update-desktop-database", [ApplicationsDirectory], cancellationToken)
                .ConfigureAwait(false);

            return new(true, L10n.T("已注册为默认文件管理器。"));
        }
        catch (Exception ex)
        {
            return new(false, L10n.Format("注册失败：{0}", ex.Message));
        }
    }

    public async Task<bool> IsDefaultFileManagerAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return false;

        var result = await RunAsync("xdg-mime", ["query", "default", "inode/directory"], cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode == 0 &&
               string.Equals(result.Output.Trim(), DesktopFileName, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return L10n.T("仅 Linux 支持系统文件管理器集成。");

        if (await IsDefaultFileManagerAsync(cancellationToken).ConfigureAwait(false))
            return L10n.T("Ordoa 当前是默认文件管理器。");

        return File.Exists(DesktopFilePath)
            ? L10n.T("Ordoa 已安装桌面项，但当前不是默认文件管理器。")
            : L10n.T("尚未注册为默认文件管理器。");
    }

    private static (string Exec, string TryExec) BuildLaunchCommand()
    {
        var process = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrWhiteSpace(process) || !File.Exists(process))
            process = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(process) &&
            !string.Equals(Path.GetFileName(process), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return (Quote(process), process);
        }

        var entry = Environment.GetCommandLineArgs().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(entry) &&
            entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(entry))
        {
            var dotnet = string.IsNullOrWhiteSpace(process) ? "dotnet" : process;
            return ($"{Quote(dotnet)} {Quote(entry)}", dotnet);
        }

        var executable = string.IsNullOrWhiteSpace(process) ? "ordoa" : process;
        return (Quote(executable), executable);
    }

    private static string BuildDesktopEntry(string exec, string tryExec)
        => $"""
            [Desktop Entry]
            Type=Application
            Version=1.0
            Name=Ordoa
            Name[zh_CN]=Ordoa 安序
            GenericName=File Manager
            GenericName[zh_CN]=文件管理器
            Comment=A minimal, powerful file manager for Linux
            Comment[zh_CN]=Linux 优先的极简文件管理器
            Exec={exec} %U
            TryExec={tryExec}
            Icon=ordoa
            Terminal=false
            StartupNotify=true
            StartupWMClass=Ordoa
            Categories=System;FileManager;Utility;
            MimeType=inode/directory;x-scheme-handler/sftp;x-scheme-handler/ftp;x-scheme-handler/ftps;x-scheme-handler/smb;x-scheme-handler/dav;x-scheme-handler/webdav;
            """ + Environment.NewLine;

    private static string BuildDbusService(string exec)
        => $"""
            [D-BUS Service]
            Name={FileManagerServiceName}
            Exec={exec} --file-manager-service
            """ + Environment.NewLine;

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static async Task<ProcessResult> RunAsync(string fileName, string[] arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return new(-1, string.Empty, L10n.Format("无法启动 {0}", fileName));

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new(process.ExitCode,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return new(-1, string.Empty, ex.Message);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
