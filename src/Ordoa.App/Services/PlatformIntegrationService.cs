using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Ordoa.Core.Localization;
using Ordoa.Core.Services;
using Ordoa.Core.Util;

namespace Ordoa.App.Services;

/// <summary>
/// Optional platform shell integration. Windows registry verbs and the macOS
/// Finder Quick Action are both intrusive, so they are only installed when the
/// user explicitly enables them in Settings.
/// </summary>
public sealed class PlatformIntegrationService
{
    private const string WindowsRoot = @"Software\Classes";
    private const string MacWorkflowName = "Open in Ordoa.workflow";

    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsMacOS => OperatingSystem.IsMacOS();

    public string PlatformLabel => IsWindows ? "Windows Explorer" : IsMacOS ? "Finder" : L10n.T("当前平台不支持");

    public string MacServicesDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Services");

    public string MacWorkflowPath => Path.Combine(MacServicesDirectory, MacWorkflowName);

    public bool IsRegistered()
    {
        if (IsWindows)
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                WindowsRoot + @"\Directory\shell\Ordoa");
            return key is not null;
        }

        if (IsMacOS)
            return System.IO.Directory.Exists(MacWorkflowPath);

        return false;
    }

    public async Task<IntegrationResult> ApplyAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (IsWindows)
            return ApplyWindows(enabled);
        if (IsMacOS)
            return await ApplyMacAsync(enabled, cancellationToken).ConfigureAwait(false);
        return new IntegrationResult(false, L10n.T("当前平台不支持系统集成。"));
    }

    private static IntegrationResult ApplyWindows(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                RemoveWindowsKey(@"Directory\shell\Ordoa");
                RemoveWindowsKey(@"Directory\Background\shell\Ordoa");
                RemoveWindowsKey(@"Drive\shell\Ordoa");
                RemoveWindowsKey(@"*\shell\Ordoa");
                return new IntegrationResult(true, L10n.T("已移除资源管理器集成。"));
            }

            var command = BuildWindowsCommand();
            WriteWindowsVerb(@"Directory\shell\Ordoa", command.Replace("%ARG%", "\"%1\"", StringComparison.Ordinal));
            WriteWindowsVerb(@"Directory\Background\shell\Ordoa",
                command.Replace("%ARG%", "\"%V\"", StringComparison.Ordinal));
            WriteWindowsVerb(@"Drive\shell\Ordoa", command.Replace("%ARG%", "\"%1\"", StringComparison.Ordinal));
            WriteWindowsVerb(@"*\shell\Ordoa", command.Replace("%ARG%", "\"%1\"", StringComparison.Ordinal));
            return new IntegrationResult(true, L10n.T("已添加资源管理器右键菜单。"));
        }
        catch (Exception ex)
        {
            return new IntegrationResult(false, L10n.Format("系统集成失败：{0}", ex.Message));
        }
    }

    private static void WriteWindowsVerb(string relativePath, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(WindowsRoot + "\\" + relativePath);
        key.SetValue(null, "Open in Ordoa");
        key.SetValue("MUIVerb", "Open in Ordoa");
        key.SetValue("Icon", Environment.ProcessPath ?? "ordoa");
        using var commandKey = key.CreateSubKey("command");
        commandKey.SetValue(null, command);
    }

    private static void RemoveWindowsKey(string relativePath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(WindowsRoot + "\\" + relativePath, throwOnMissingSubKey: false);
        }
        catch
        {
            // The key may be locked by Explorer; the next attempt will retry.
        }
    }

    private static string BuildWindowsCommand()
    {
        var process = Environment.ProcessPath ?? "ordoa";
        var args = Environment.GetCommandLineArgs();
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            args.Length > 0 && args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{process}\" \"{args[0]}\" %ARG%";
        }

        return $"\"{process}\" %ARG%";
    }

    private async Task<IntegrationResult> ApplyMacAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            if (!enabled)
            {
                if (System.IO.Directory.Exists(MacWorkflowPath))
                    System.IO.Directory.Delete(MacWorkflowPath, recursive: true);
                await RefreshFinderServicesAsync(cancellationToken).ConfigureAwait(false);
                return new IntegrationResult(true, L10n.T("已移除 Finder 快速操作。"));
            }

            var resources = Path.Combine(MacWorkflowPath, "Contents", "Resources");
            System.IO.Directory.CreateDirectory(resources);
            await File.WriteAllTextAsync(Path.Combine(MacWorkflowPath, "Contents", "Info.plist"),
                BuildMacServiceInfoPlist(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(resources, "document.wflow"),
                BuildMacWorkflowDocument(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await RefreshFinderServicesAsync(cancellationToken).ConfigureAwait(false);
            return new IntegrationResult(true, L10n.T("已添加 Finder 快速操作。"));
        }
        catch (Exception ex)
        {
            return new IntegrationResult(false, L10n.Format("系统集成失败：{0}", ex.Message));
        }
    }

    private static async Task RefreshFinderServicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/System/Library/CoreServices/pbs",
                Arguments = "-update",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is not null)
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Finder refreshes the services cache on its own eventually.
        }
    }

    private static string BuildMacServiceInfoPlist()
        => """
           <?xml version="1.0" encoding="UTF-8"?>
           <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
           <plist version="1.0">
           <dict>
             <key>CFBundleDevelopmentRegion</key><string>en</string>
             <key>CFBundleIdentifier</key><string>me.whynbnb.Ordoa.service</string>
             <key>CFBundleName</key><string>Open in Ordoa</string>
             <key>CFBundleShortVersionString</key><string>1.0</string>
             <key>NSServices</key>
             <array>
               <dict>
                 <key>NSMenuItem</key>
                 <dict>
                   <key>default</key><string>Open in Ordoa</string>
                   <key>zh_CN</key><string>在 Ordoa 中打开</string>
                 </dict>
                 <key>NSMessage</key><string>runWorkflowAsService</string>
                 <key>NSRequiredContext</key>
                 <dict><key>NSApplicationIdentifier</key><string>com.apple.finder</string></dict>
                 <key>NSSendFileTypes</key>
                 <array><string>public.folder</string></array>
               </dict>
             </array>
           </dict>
           </plist>
           """;

    private static string BuildMacWorkflowDocument()
    {
        var script = """
                     while IFS= read -r item; do
                       if [ -n "$item" ]; then
                         /usr/bin/open -b me.whynbnb.Ordoa "$item"
                       fi
                     done
                     """;
        var escaped = SecurityElement.Escape(script) ?? string.Empty;
        var id = Guid.NewGuid().ToString("B").ToUpperInvariant();
        return $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>AMApplicationBuild</key><string>521</string>
                  <key>AMApplicationVersion</key><string>2.10</string>
                  <key>AMDocumentVersion</key><string>2</string>
                  <key>actions</key>
                  <array>
                    <dict>
                      <key>action</key>
                      <dict>
                        <key>AMAccepts</key>
                        <dict>
                          <key>Container</key><string>List</string>
                          <key>Optional</key><false/>
                          <key>Types</key><array><string>com.apple.cocoa.path</string></array>
                        </dict>
                        <key>ActionBundlePath</key><string>/System/Library/Automator/Run Shell Script.action</string>
                        <key>ActionName</key><string>Run Shell Script</string>
                        <key>ActionParameters</key>
                        <dict>
                          <key>COMMAND_STRING</key><string>{escaped}</string>
                          <key>inputMethod</key><integer>0</integer>
                          <key>shell</key><string>/bin/bash</string>
                          <key>source</key><string></string>
                        </dict>
                        <key>AMProvides</key>
                        <dict>
                          <key>Container</key><string>List</string>
                          <key>Types</key><array><string>com.apple.cocoa.path</string></array>
                        </dict>
                        <key>BundleIdentifier</key><string>com.apple.RunShellScript</string>
                        <key>InputUUID</key><string>{id}</string>
                        <key>OutputUUID</key><string>{Guid.NewGuid().ToString("B").ToUpperInvariant()}</string>
                        <key>UUID</key><string>{Guid.NewGuid().ToString("B").ToUpperInvariant()}</string>
                        <key>location</key><string>309.500000:631.000000</string>
                        <key>isViewVisible</key><true/>
                      </dict>
                      <key>isViewVisible</key><true/>
                    </dict>
                  </array>
                  <key>connectors</key><dict/>
                  <key>state</key><dict/>
                </dict>
                </plist>
                """;
    }
}
