# Ordoa 架构说明

## 分层

```
Avalonia App (Views / ViewModels)
        │  IFileSystem, OperationSource, Application services
        ▼
Core (file systems, operations, search, trash, archives, connections)
        │
        ▼
OS APIs / FluentFTP / SSH.NET / SMBLibrary / HttpClient
```

UI 不直接调用 `System.IO` 或远程 SDK。所有文件访问都经过 `IFileSystem`，因此本地、回收站和四种远程协议共享同一套浏览、排序、选择、拖放、复制、搜索和属性逻辑。

## 路径模型

- `LocalFileSystem`：绝对 OS 路径（Linux 为 `/...`，Windows 为 `C:\...`），负责大小写、隐藏属性和 Unix 权限。
- 远程文件系统：POSIX 风格绝对路径（`/`），由 `FileSystemBase` 提供 `Combine / GetParent / NormalizePath`。SMB 在内部把 `/` 转为 `\`，WebDAV 在 URL 编码和展示路径之间转换。
- `GetDisplayPath` 返回人类可读路径（不带密码），用于面包屑、状态栏、复制路径。
- 连接会话用保存的 profile ID 标识；UI 通过 `FileSystemManager` 获取或复用会话。

## 文件系统能力

`FileSystemCapabilities` 声明 `Write / Delete / Rename / UnixPermissions / Trash / SpaceInfo ...`。界面根据能力启用命令，例如：

- 回收站视图没有写入能力，不显示新建/粘贴。
- FTP 不声明 Unix 权限，因此属性中不出现 chmod。
- 远程文件系统不声明 `Trash`，删除走“永久删除 + 确认”。

新增能力时优先扩展 flags 与默认实现，而不是在视图中判断类型。

## 操作队列

`FileOperationService` 是单消费者后台队列：

1. `Enqueue` 创建 `FileOperationItem` 并投递到可观察集合（UI 线程）。
2. 消费器扫描源（本地目录统计总量；远程目录退化为不确定进度）。
3. 逐项复制/移动/删除，冲突通过 `IConflictResolver` 回到 UI 询问。
4. 进度通过注入的 dispatcher 回传，节流约 120 ms。
5. 完成/失败/取消通过 `Finished` 事件交给主视图模型刷新面板并显示 Toast。

跨文件系统复制使用 128 KiB 缓冲流；同文件系统的直接移动失败时自动回退为复制后删除。删除目录到回收站是原子移动，不递归。

## 回收站

`TrashService` 实现 freedesktop.org Trash 规范：

- home trash：`$XDG_DATA_HOME/Trash`；
- 其他挂载点：优先 `.Trash/$uid`，否则创建 `.Trash-$uid`；
- `files/` 存放实体，`info/*.trashinfo` 保存原始路径与删除时间；
- 恢复冲突时自动生成唯一名称；
- `TrashFileSystem` 把回收站包装成只读文件系统，UI 的“恢复/永久删除”通过服务处理。

## WebDAV

- 使用 `HttpClient` 与 PROPFIND XML 解析，按 href 规范化路径；
- 上传先写入 0600 临时文件，再用带 Content-Length 的 PUT 提交（兼容要求长度的服务器）；
- 复制优先使用服务端 COPY，失败回退流式复制；
- 读取响应流通过包装器确保释放 HttpResponseMessage。

## 线程与 UI

- 所有 Avalonia 视觉对象（包括 `Geometry`、`Bitmap` 绑定）必须创建/访问于 UI 线程；`PaneViewModel.LoadAsync` 检测并切回 UI 线程。
- 文件系统提供者内部可线程池执行，但对外异步 API 完成后由调用方决定上下文。
- `FileSystemWatcher` 事件经过防抖后通过 Dispatcher 刷新。
- 后台传输/搜索/压缩通过 `CancellationToken` 取消。

## 本地化

- 翻译采用「中文源串作为 key」的方式：`L10n.T("设置")` / XAML `{loc:Tr 设置}`。
- 英文映射位于 `Ordoa.Core/Localization/en.json`，作为嵌入资源随程序发布；缺失的 key 自动回退中文。
- `TrExtension` 通过绑定 `TrProxy.Values` 槽位，在语言切换时统一刷新所有 XAML 文案；C# 侧在生成菜单/提示时调用 `L10n.T`，主视图模型监听 `L10n.LanguageChanged` 重建侧栏、命令面板与状态栏。
- 语言设置持久化在 `AppSettings.Language`（System / Chinese / English），启动时在创建窗口前应用；系统语言为中文时默认中文，其他系统默认英文。

## 扩展新协议

1. 在 `Models/ConnectionProfile.cs` 增加枚举、默认端口和显示标签。
2. 实现 `IFileSystem`（推荐继承 `FileSystemBase`），至少提供 List / OpenRead / OpenWrite / CreateDirectory / Delete / Move。
3. 在 `FileSystemManager.OpenAsync` 添加工厂分支，声明能力。
4. 在连接对话框中添加协议 chip 与必要字段（主机、端口、域等）。
5. 为路径、编码、删除递归、错误映射写集成测试（可使用进程内测试服务器，参考 `WebDavTests`）。

## 测试

- `Ordoa.Core.Tests` 使用 xUnit，覆盖路径、格式化、本地文件系统、操作队列、回收站、压缩、哈希与 WebDAV 端到端。
- 回收站测试通过 `XDG_DATA_HOME` 指向临时目录并禁用并行执行。
- WebDAV 测试在 `HttpListener` 上实现最小 WebDAV 服务器，验证 PROPFIND、GET/PUT、MKCOL、COPY/MOVE、DELETE。

```bash
dotnet test tests/Ordoa.Core.Tests/Ordoa.Core.Tests.csproj
```
