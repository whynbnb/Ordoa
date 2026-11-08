<p align="center">
  <b>English</b> · <a href="README.zh-CN.md">简体中文</a>
</p>

> Vibe-coded with DeepSeek V4.1 Flash / OpenCode

<p align="center">
  <img src="src/Ordoa.App/Assets/ordoa.png" width="96" alt="Ordoa logo" />
</p>

<h1 align="center">Ordoa</h1>

<p align="center">
  <b>Ordoa (安序) = Ordo (order) + A (Avalonia)</b><br/>
  A Linux-first, restrained but complete file manager. Tabs, split panes, local and remote files, a keyboard-first workflow, and a command palette.
</p>

<p align="center">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-512BD4" />
  <img alt="C#" src="https://img.shields.io/badge/C%23-14-239120" />
  <img alt="Avalonia" src="https://img.shields.io/badge/Avalonia-12.1-8B44AC" />
  <img alt="License" src="https://img.shields.io/badge/license-MIT-17937F" />
</p>

<p align="center">
  <img src="docs/screenshots/main-light.png" width="49%" alt="Ordoa light theme" />
  <img src="docs/screenshots/main-dark.png" width="49%" alt="Ordoa dark theme" />
</p>

## Design goals

A file manager should not bury everyday actions under layers of menus. Ordoa keeps **navigation, selection, and opening** front and center, while actions such as copy, cut, archive, checksum, permissions, and remote connections live in context menus, overflow menus, and the command palette. The interface stays quiet, but the features are there when needed.

- **Minimal chrome**: single-row tab strip, breadcrumbs, list/grid views, and a slim status bar; the list view shows permissions and ownership.
- **Power features**: cross-filesystem transfer queue, XDG trash, archive browsing and extraction, SHA-256, chmod, remote sessions, and bookmarks; the grid view virtualizes by viewport, so very large folders stay responsive.
- **Native Linux experience**: XDG paths, XDG Trash, Unix permissions, the system keyring (libsecret), terminal launching, and disk mount points.
- **Keyboard-friendly**: every major action has a shortcut, `Ctrl+Shift+P` opens a searchable command palette, and common shortcuts can be rebound in Settings.
- **Latest Avalonia**: Avalonia 12.1, C# 14, and .NET 10; macOS and Windows builds are produced from the same codebase.

## Features

### Browsing and organization

- Tabs: create, duplicate, close, close others, middle-click close, and switch; drag to reorder, pin tabs, and restore recently closed tabs with `Ctrl+Shift+T`.
- Multiple windows and single-instance mode: `Ctrl+N` opens a new window; launching Ordoa again reuses the running process by default. Settings can choose whether external folder requests open in the current window or a new one, and multi-window mode can be disabled.
- Split panes: two panes per tab with a draggable divider; narrow panes automatically simplify their toolbars.
- List and grid views, with local image, PDF and video thumbnails in the grid; PDF and video frames use optional poppler/ffmpeg tools and are capped per listing.
- Breadcrumb navigation, an editable path bar (`Ctrl+L`), and back/forward/up history.
- Natural sorting (`file2` before `file10`) by name, size, modified time, type, permissions, or owner, with folders first.
- List, grid, tree, and Miller column views: the tree view expands child folders on demand; the Miller view browses multiple directory levels in columns, appends a column when a folder is clicked, opens files on double-click, and keeps Git markers and tag dots.
- Configurable list columns: size, modified time, type, permissions, owner, and an optional tag/color column; show/hide columns, drag column borders to resize, double-click a border to auto-fit, and drag the header grip to reorder. Widths and order are remembered globally and per folder.
- Three grid thumbnail sizes (small, medium, large) and three list row densities (compact, standard, spacious).
- Optional grouping in the list view by type, modified time, or size, with collapsible-style group headings.
- Folder sizes are not computed while a listing loads; right-click **Calculate Folder Size** measures selected folders recursively on demand.
- Hidden-file toggle, live name filtering, and recursive search with configurable depth and result limits.
- Search syntax: glob, `-exclude`, `ext:`, `type:`, `size:`, `modified:`, `content:`, `tag:`, and `color:`; recent searches, named saved searches, current-folder/full-disk scope switching, and Git status on search results.
- Optional local search index (off by default): whitelist mode indexes exactly the listed roots, blacklist mode indexes those roots while applying exclusion rules; watcher-driven incremental updates, periodic/manual rebuilds, and automatic fallback to the scan-based search for content, tag, color, or remote queries.
- Bookmarks, recent locations, and session restore (tabs and split panes); the trash entry is pinned to the bottom of the sidebar and always visible.
- Settings can show or hide each common location in the sidebar's **Places** section, with one-click reset to defaults.
- On macOS the disk list shows the system disk and user volumes under `/Volumes` by default; Settings can disable disks, show hidden system volumes, or add arbitrary custom paths.
- Every disk in the sidebar can be shown or hidden individually in Settings; saving refreshes the sidebar immediately, and the disk section is omitted when everything is hidden.
- On macOS, `.app` directories can be treated as applications: they show an application icon and type, launch on double-click, and offer **Show Package Contents** in the context menu. This can be disabled in Settings and is off by default on Linux and Windows.
- Linux disks can be mounted, unmounted, and ejected (preferring `udisksctl`); unmounted removable devices also appear in the sidebar, and GVfs mounts (MTP cameras/phones, SMB, SFTP, and others) appear as disks automatically.
- Filesystem watching: local folders refresh automatically after external changes (debounced).
- Internal and cross-application drag and drop; drag a multi-selection as one unit, drop onto a folder to move items into it, or drop onto the sidebar's **Trash** entry or the trash panel to move items to the trash; hold `Shift` while dropping on the trash to delete permanently.
- Drag modifiers: `Ctrl` copies and `Shift` moves; the default is to move within the same filesystem and copy across filesystems.
- Selection modifiers: `Ctrl+click` toggles selection, `Shift+click` selects a range, and dragging on empty space draws a selection rectangle; hold `Ctrl` or `Shift` while rubber-band selecting to add to the selection.
- System clipboard: copying or cutting local files writes both the file list and text paths, so other file managers can interoperate.
- Open With: on Linux, choose from installed applications and remember a default per extension; other platforms fall back to the system default application.
- System file manager integration: register as the default file manager and expose the `org.freedesktop.FileManager1` D-Bus interface for browser/IDE "show in folder" requests on Linux; Finder and Explorer reveal actions are available on macOS and Windows.
- Optional system-menu integration, off by default: Windows Explorer **Open in Ordoa** / **Open Ordoa here** verbs and a macOS **Open in Ordoa** Finder Quick Action. Both only write to the current user's registry or `~/Library/Services`.
- Seven neutral palettes (Graphite, Midnight, Slate, Ocean, Forest, Warm, Black) each have a deep dark variant and a pale light variant, applied consistently to windows, sidebars, lists, menus and dialogs.

### Search and filtering

- Typing filters the current folder live and supports name, glob, exclusion, ext/type/size/modified/tag/color syntax (`content` requires `Enter` for recursive search); press `Enter` for a recursive search, and use the button next to the search box for syntax help, history, and saved searches.
- Name matching supports `*` and `?` globs; multiple words must all match, and `-word` excludes matches and skips same-named folders (for example `-node_modules`).
- `ext:cs,json` filters by extension; `type:file|folder|image|video|audio|text|document|archive|code|config` filters by category.
- `size:>10MB` and `size:1KB..2MB` filter by size; `modified:>7d`, `modified:today`, and `modified:2024-01-01..2025-01-01` filter by modification time.
- `content:TODO` or `content:"hello world"` searches file contents (grep-style, skipping binary files and files larger than 8 MB).
- `tag:work`, `tag:work,urgent`, and `color:red` filter search results by file tags and colors.
- Search scope can switch between **Current Folder** and **Entire Disk / Entire Connection**; Git status is filled in for results in the background.
- The 20 most recent searches are saved automatically; named searches are kept indefinitely and can be removed from the search panel.

### Quick preview

- Select a file and press `Space` for Quick Look; press `Space` or `Esc` to close, or `Enter` to open the file. The context menu and command palette also provide preview commands.
- Persistent preview panel: toggle with `F3` or the eye icon in the toolbar (off by default); dock it on the right or bottom in Settings, and it refreshes automatically when the selection changes.
- Text, source code, configuration, CSV, SVG, and similar files are read as text with size limits; UTF-8, UTF-16, UTF-32, and GB18030 are detected automatically, with file name, encoding, line count, and related details.
- Images use Avalonia/Skia native decoding, supplemented by the fully managed [SimdCodecs](https://www.nuget.org/packages/Sdcb.SimdCodecs) package for TIFF, JPEG 2000, QOI, Netpbm, and other formats; EXIF orientation, zooming, and the first frame of animations are supported.
- Video previews show a first-frame thumbnail plus duration, resolution, codec, bitrate, and audio tracks; audio previews show container, duration, codec, sample rate, and channels. Both use optional `ffmpeg`/`ffprobe`, which Ordoa does not bundle.
- ZIP/JAR/tar/tar.gz previews list the first 200 entries; folder previews list folder contents.
- Markdown previews render headings, paragraphs, lists, quotes, fenced code, tables, links and inline emphasis through a native Avalonia renderer; Markdig is used only as a .NET parser.
- Code and configuration files get lightweight syntax highlighting and line numbers; CSV/TSV files render as bounded tables.
- PDF previews are multi-page with previous/next and zoom controls when poppler-utils (`pdftoppm`/`pdfinfo`) is installed; Office/ODF/EPUB previews try to read embedded document thumbnails.
- Other binary files show type, size, timestamps, and a hexadecimal file-header dump; unknown formats fail visibly rather than silently.
- No WebView, browser engine, or native image-processing dependency is introduced.

### Git status

- When browsing a folder inside a Git repository, the status bar shows the current branch, ahead/behind counts, and the number of uncommitted changes.
- List and grid views show Git markers next to files and folders: modified, staged, untracked, conflicted, and others; committed files and folders with no changes show a green check; Git-ignored files use a separate dimmed style and a gray `I`, so they are not confused with dimmed hidden files.
- While browsing a parent folder outside the repository, the repository folder itself shows a `GIT` marker, and submodules show a special `S` marker.
- The **Git Status** window lists every uncommitted item, staging state, branch, and upstream information; double-click an item to jump to it.
- Quick Look and the preview panel show the repository, branch, ahead/behind state, staging counts, the selected item's status and the latest commit touching it. Folder listings inside previews use the same Git badges, clean checks, repository markers and ignored-file dimming as the main file list.
- The **Git** category in Settings can disable Git integration or control the status bar, preview details, file markers, green check, repository markers, submodule markers, and ignored-file dimming independently.
- Ordoa only reads `git status` and `git ls-files`; it never stages, commits, or modifies a repository. A `git` executable must be available on `PATH`.

### Disk analysis

- Disk usage analysis: an interactive treemap shows recursive folder usage, with drill-down, go-up, and rescan actions; the right side lists current-folder items and the largest files on the disk.
- Duplicate finder: group files by size, compute quick head/tail fingerprints for candidates, then verify contents with SHA-256; supports a minimum file size, group selection, select-all duplicates, and move-to-trash.
- Both analyses scan in the background with progress display and cancellation, and can be started from the file area's **Analysis Tools** context menu, the sidebar context menu, or the command palette.
- File comparison: select two files to view a side-by-side text diff (line-level LCS, with a line-by-line fallback for very large files) or a binary diff (size, SHA-256, and the offset of the first difference).
- Folder comparison and sync: recursively compare two folders, show left-only/right-only/newer/different/type-mismatch states, optionally compare file contents by SHA-256, filter to differences, preview with dry run, and queue per-row or bulk copies through the transfer engine.
- Tags and colors: right-click **Tags & Colors** to assign a color and multiple tags to files or folders; list and grid views show color dots and tags on hover. Tag data is stored separately in `tags.json` and works for local, remote, and archive paths.
- Batch image conversion: select multiple images and choose JPEG/PNG/WebP, quality, maximum dimension, output folder, and filename suffix; the default `-converted` suffix avoids overwriting originals.

### File operations

- Copy, cut, paste, move, rename, create folders/empty files, and delete permanently.
- Copy to… / Move to… destination pickers, duplicate in place, and paste clipboard items as symbolic links.
- Archive browsing: double-click a zip, tar, tar.gz, tgz, 7z, or rar to browse it level by level like a folder; files are extracted to a cache on demand and can be copied locally or previewed with `Space`. 7z/rar requires the system `7z` command-line tool; encrypted archives prompt for a password.
- Search inside archives: the search box and `content:` syntax also work inside archives, recursively from the current archive path.
- Extract selection to any folder: inside an archive, right-click **Extract To…** on selected files or folders; in a normal folder, the same command works on selected archives.
- Write inside archives: add files, rename, and delete entries in zip, tar, tar.gz, and 7z; rar is read-only. zip uses the built-in updater when 7z is unavailable; other formats require the system 7z.
- Undo/redo: session-level history covering rename, move, move-to-trash, create, and copy; a batch operation is merged into one entry. Before undoing, Ordoa checks whether targets are occupied or modified and reports conflicts instead of overwriting.
- Links: create symbolic links, relative symbolic links, or hard links from the context menu; hard links apply to files only.
- The Properties window can show extended attributes and POSIX ACLs and change owner/group with `chown`; on Linux, extended attributes and ACL text are editable directly, while macOS attributes are read through `xattr`. The latter usually requires administrator privileges.
- Optional administrator operations: delete, copy and move using pkexec on Linux, osascript on macOS, or Windows UAC. They are off by default and always require a system confirmation prompt.
- Local copies preserve extended attributes where possible; permissions and modification times follow the existing handling.
- Undo scope can be system-following, full (including copies), structural operations only, or disabled; Windows/macOS default to full undo, while Linux defaults to renaming, moving, trash, and creation only.
- Batch rename: find/replace, regex capture groups (`$1`, `${name}`), numbering, case conversion, extension and date variables; live preview checks illegal names, duplicates, and existing-file conflicts, and works on local and remote filesystems.
- Batch permissions/ownership: set the octal mode and owner/group for a multi-selection in one dialog; ownership changes are limited to local files and usually require administrator privileges.
- Before a batch copy/move, if same-named items are detected, Ordoa shows the destination paths and conflict list and can apply replace, skip, or keep-both to all; above 500 items only the first 500 are previewed, and the rest are asked about during execution.
- Operation queue: progress, speed, ETA, cancel, and error summaries; streaming copies across filesystems.
- The transfer panel expands automatically only for lengthy operations, so quick moves/deletes of small files do not interrupt browsing; transfers can be paused, resumed, and cancelled.
- Retries are per-file: failed items stay in the transfer panel, can be expanded, and only the failed files are retried instead of the whole batch.
- After copying, Ordoa verifies the destination file size by default; Settings can switch to SHA-256 or disable verification. Files that fail verification are marked and can be retried.
- Settings can limit transfer speed (MB/s) and the number of concurrent transfer jobs (1–4); the speed limit is shared by all transfers.
- Transfer journal: unfinished copy/move requests are persisted and requeued after a restart. A resumed operation restarts the affected file copy; it does not resume a partially written file at a byte offset.
- The operation history window records this session's copies, moves, deletes, trash operations, and restores, including success/failure counts and error details.
- The undo history panel shows undoable and redoable steps with timestamps and can perform undo/redo directly.
- Conflict handling: replace, skip, keep both (auto-rename), and apply to all.
- Trash: Linux fully implements the freedesktop.org Trash specification (home trash and `.Trash-$uid` / `.Trash/$uid`) with restore, permanent delete, and empty; macOS uses `~/.Trash` and keeps restore information; Windows uses the system Recycle Bin.
- Compression: ZIP, tar, tar.gz, tar.zst, tar.xz, and 7z; 7z supports compression level, split size, and optional password/header encryption; zstd/xz use the optional system command-line tools. Extraction validates paths and rejects zip-slip and archive links.
- Legacy ZIP filename encodings are detected automatically (UTF-8, GBK, Shift-JIS, CP437) and can be overridden in Settings for archives written without the UTF-8 flag.
- SSH config import reads concrete Host aliases, HostName, User, Port, IdentityFile and ProxyJump; Include globs and simple Match blocks are supported. Complex exec/localnetwork conditions are intentionally ignored.
- Passwords prefer the desktop keyring: libsecret on Linux, Keychain on macOS, and Windows Credential Manager on Windows; the 0600 JSON file remains only a fallback.
- Checksums: MD5, SHA-1, SHA-256, and SHA-512 with batch calculation and copy support; verify against GNU/BSD checksum files and switch algorithms without closing the dialog.
- Properties: type, path, size, timestamps, MIME type, Unix permissions (rwx and octal), owner/group (local and remote), symlink target, trash original location, and folder content statistics.
- Local chmod editing (and SFTP `chmod`).
- Copy path/name, open in terminal, and open with the system default application.
- Remote files can be downloaded to the cache and opened with a system application; changes saved externally are synced back automatically (can be disabled in Settings).
- The remote status bar shows the most recent directory listing latency (green ≤300 ms, yellow ≤1 s, red slower) and shows **Offline** on failure.
- Failed remote directory loads automatically rebuild the session and retry once; panes sharing a connection reuse the latest session to avoid unnecessary reconnects.
- Remote grid view can load image thumbnails (remote images up to 24 MB, local up to 200 MB).
- Remote video, audio, PDF, Office, and archive previews are downloaded to the cache on demand and passed to the built-in previewer or ffmpeg/pdftoppm; download limits are 512 MB (media), 128 MB (PDF/Office), and 256 MB (archives).

### Terminal and custom commands

- Terminal discovery: automatically detects common Linux, macOS, and Windows terminals and lets you choose the default terminal and which terminals appear in the context menu. On macOS it checks `PATH`, Homebrew/MacPorts paths, and locations such as `/Applications` and `~/Applications`, supporting Terminal, iTerm2, Ghostty, WezTerm, Kitty, Alacritty, Rio, Warp, Tabby, Hyper, MacTerm, and others. A manual-path mode can specify an executable and arguments directly.
- Open in terminal: available from the file area, sidebar, and command palette; the context menu can also choose among enabled terminals via **Open with Another Terminal**.
- Herdr: when `herdr` is detected, Ordoa offers a separate **Open in Herdr** action (off by default, enabled in Settings); it creates or attaches a Herdr workspace inside the current terminal.
- Custom commands: run scripts with placeholders such as `%f`, `%F`, `%n`, `%N`, `%d`, `%p`, and `%%`; configure the working directory, context-menu visibility, run-in-terminal, selection requirement, confirmation before running, piping selected paths to stdin, extra environment variables, and whether to pin the command to the toolbar. Commands are also added to the command palette.
- Command palette: ordered by usage frequency and recency; execution history is stored in Settings.
- Workspaces: save the current tabs and split layout from the command palette and restore them later as named workspaces; the workspace list is stored in a separate file.
- Cross-window tab dragging: drag a tab out of one window and drop it on another Ordoa window's tab strip; if the source window loses its last tab, it closes automatically.
- Recently closed windows: after closing a window, restore its tabs and split panes from the command palette's **Reopen Closed Window** command.
- Shortcut conflict detection: the shortcut list in Settings reports duplicate combinations live.

### Remote filesystems

| Protocol | Implementation | Notes |
| --- | --- | --- |
| FTP / FTPS | FluentFTP | Active/passive modes and explicit FTPS; optional manual trust for self-signed certificates |
| SFTP | SSH.NET | Password, private key (with passphrase), and SSH Agent authentication; known_hosts validation and TOFU |
| SMB 2/3 | SMBLibrary | List shares, browse, read/write, rename, and delete; fixed TCP port 445 |
| WebDAV | HttpClient | PROPFIND / GET / PUT / MKCOL / DELETE / MOVE / COPY with Basic authentication |
| S3 / S3-compatible | Built-in SigV4 client | AWS S3, MinIO, Cloudflare R2 and B2-compatible endpoints; path-style or virtual-host URLs; list, read, upload, copy, move and delete |

- Connection manager: save connections, initial paths, and password/private-key passphrase storage policies; supports password, private key, and SSH Agent authentication. Failed connections remain editable; saved connections can be hidden, duplicated, or deleted in Settings.
- Remote session reuse; the address bar accepts `sftp://`, `ftp://`, `ftps://`, `smb://`, `webdav://`, `https://`, `s3://`, and similar URLs.
- `~/.ssh/config` import expands `Include` globs, applies host/originalhost/user `Match` blocks, and reads HostName, User, Port, IdentityFile and ProxyJump.
- SFTP ProxyJump supports single and multi-hop SSH port forwarding; jump hosts are checked against known_hosts, and every hop can have its own username, port, password, private key or SSH Agent.
- Local network discovery uses native mDNS queries plus WS-Discovery and can fill in SFTP, SMB, FTP and WebDAV hosts without typing addresses.
- Remote-to-local and remote-to-remote copies/moves use the same streaming engine.
- In-dialog connection tests and friendly error messages.

### Interface and themes

- Light, dark, follow-system and high-contrast themes; five accent colors (jade, indigo, amber, rose, and violet).
- Seven neutral palettes with dark and pale light variants: Graphite, Midnight, Slate, Ocean, Forest, Warm and Black.
- Compact mode; adjustable sidebar width; window size, sidebar state, sorting, and view preferences are persisted.
- Command palette (`Ctrl+Shift+P`) covering all major actions.
- Transfer panel, lightweight toasts, and an immersive custom title bar by default; tiling compositors such as niri, Sway, and Hyprland hide the custom window buttons automatically, and Settings can force them visible or restore the system title bar.
- Chinese, English, Japanese, Korean, German, French and Spanish UI; switch languages in Settings or follow the system. Untranslated entries fall back to English.
- Keyboard focus rings, accessible names for the main file list, search, path, preview and connection fields, and automation metadata for screen readers.
- Explanatory hints collapse into an info icon by default with the full text on hover; Settings → Appearance can disable collapsing and show the full text again.
- The About window shows a two-part version aligned with the git tag, a six-character commit hash, and AOT/runtime information.
- Icons use embedded vector paths and require no icon font.

## More screenshots

| Grid view | Split panes |
| --- | --- |
| ![Grid](docs/screenshots/grid.png) | ![Split](docs/screenshots/split.png) |

| Command palette | Connection settings |
| --- | --- |
| ![Command palette](docs/screenshots/palette.png) | ![Connection](docs/screenshots/connection.png) |

| Remote SFTP |
| --- |
| ![Remote](docs/screenshots/remote-sftp.png) |

## Building

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/) (C# 14)
- Linux: native Wayland (Avalonia 12.1 experimental backend, preferred when available) or X11/XWayland; fontconfig; optional `libsecret`/`secret-tool` for the system keyring
- No system-native file manager components are required to build
- Optional for Quick Look: `ffmpeg`/`ffprobe` for video/audio information and thumbnails (configurable in Settings or via `ORDOA_FFMPEG`/`FFMPEG_PATH`); `pdftoppm` (poppler-utils) for PDF first-page rendering

### Commands

```bash
git clone <your-repository-url> ordoa
cd ordoa

dotnet restore
dotnet build Ordoa.slnx
dotnet test tests/Ordoa.Core.Tests/Ordoa.Core.Tests.csproj

dotnet run --project src/Ordoa.App/Ordoa.App.csproj
```

Open a specific folder or URL:

```bash
dotnet run --project src/Ordoa.App/Ordoa.App.csproj -- /home/user/Downloads
dotnet run --project src/Ordoa.App/Ordoa.App.csproj -- sftp://user@example.com/
```

Linux integration arguments:

```bash
ordoa --new-window /path/to/folder   # Force a new window
ordoa --new-tab /path/to/folder      # Force a new tab in the current window
ordoa --register-file-manager        # Register the desktop entry, D-Bus service, and default file manager
ordoa --no-single-instance           # Do not use single-instance mode for this launch
ordoa --undo                         # Ask the running instance to undo the last step
ordoa --redo                         # Ask the running instance to redo the last step
```

### Publish scripts

On Unix-like systems, use `scripts/publish.sh`:

```bash
# Framework-dependent: multiple files; requires the .NET 10 Desktop Runtime on the target
./scripts/publish.sh framework

# Framework-dependent single file: requires the .NET 10 Desktop Runtime on the target
./scripts/publish.sh single --rid linux-arm64

# NativeAOT: self-contained, no .NET runtime required
./scripts/publish.sh aot

# NativeAOT static single file: SkiaSharp / HarfBuzz / ANGLE statically linked
./scripts/publish.sh aot-static
```

Windows provides both batch and PowerShell entry points (Windows PowerShell 5.1 or PowerShell 7; `publish.bat` picks an available one automatically):

```bat
scripts\publish.bat framework
scripts\publish.bat single -Rid win-x64
scripts\publish.bat aot
scripts\publish.bat aot-static
```

You can also run PowerShell directly:

```powershell
.\scripts\publish.ps1 aot -Rid win-x64 -Configuration Release
```

Four modes:

| Mode | Runtime | Output |
| --- | --- | --- |
| `framework` | Requires .NET 10 Desktop Runtime | Multiple files; detects the current RID by default, and `--portable` produces a portable build |
| `single` | Requires .NET 10 Desktop Runtime | Single file; native libraries self-extract on first launch |
| `aot` | No .NET runtime required | NativeAOT executable; SkiaSharp/HarfBuzz native libraries are still shipped alongside it |
| `aot-static` | No .NET runtime required | Single file; SkiaSharp/HarfBuzz/ANGLE are statically linked via `StaticLink.Avalonia`, but system fontconfig/freetype/zlib and other libraries are still required |

Output defaults to `artifacts/publish/<mode>/<RID|portable>` under the repository root. The directory is ignored by `.gitignore`, with only `artifacts/README.md` kept for documentation. Omitting `RID` detects the current platform; in `framework` mode, `--portable` produces a RID-independent build (larger, with native assets for multiple platforms). Common options:

```bash
./scripts/publish.sh framework --rid linux-x64 --configuration Release
./scripts/publish.sh framework --portable
./scripts/publish.sh single --output /tmp/ordoa-single --no-clean
./scripts/publish.sh aot --no-restore
./scripts/publish.sh aot-static --rid linux-x64
```

NativeAOT requires a local C/C++ toolchain: clang or gcc on Linux, and Visual Studio C++ Build Tools plus the Windows SDK on Windows.

`aot-static` additionally requires:

- `lld` (Linux)
- The system development libraries expected by `StaticLink.Avalonia`: fontconfig, freetype, zlib, libx11, libxext, libxcb, wayland, libffi, and others
- A large static-library package on first restore (about 115 MB download, about 533 MB extracted)

`aot-static` produces a single file; `aot` is not single-file, and `libSkiaSharp.so` / `libHarfBuzzSharp.so` (or the corresponding DLLs on Windows) must sit next to the executable.

`aot-static` uses the third-party MIT package [StaticLink.Avalonia](https://github.com/greepar/StaticLink.Avalonia), aligned with Avalonia 12.1.4 / SkiaSharp 3.119.4 / HarfBuzzSharp 8.3.1.3. It is not a fully static executable and still depends on system libraries such as glibc, fontconfig, and freetype at runtime.

On macOS, `aot-static` also requires `StaticLink.Avalonia.Native` (the scripts/projects reference it conditionally for macOS RIDs) and macOS 13 or later.

### GitHub Actions releases

Pushing a `v*` tag triggers `.github/workflows/release.yml`:

```bash
git tag v0.3
git push origin v0.3
```

The release produces:

- Windows x64 framework-dependent zip
- Windows x64 AOT zip
- Windows x64 AOT static single-file zip
- macOS arm64 framework-dependent zip
- macOS arm64 AOT DMG
- Linux x64 framework-dependent tar.gz
- Linux x64 AOT deb
- Linux x64 AOT AppImage
- Linux x64 AOT static single-file tar.gz

The macOS DMG is **not code-signed and not notarized**: the project does not own an Apple Developer ID certificate, so CI cannot submit it to Apple's notary service. Gatekeeper may block the first launch. After verifying the download, right-click the app in Finder and choose **Open**, or remove the quarantine attribute with `xattr -dr com.apple.quarantine /Applications/Ordoa.app`.

AOT publishing currently produces only the `SMBLibrary` `IL2104` warning. Avalonia compiled bindings are enabled (all Window/UserControl and DataTemplate declarations specify `x:DataType`), and the localization `{loc:Tr}` extension now uses compiled binding paths as well. `TrimmerRootAssembly` keeps the application assemblies, and release output removes `*.pdb` automatically.

Use the appropriate RID for macOS and Windows (`osx-arm64`, `osx-x64`, `win-x64`).

## Installation (Linux)

```bash
chmod +x scripts/install.sh scripts/uninstall.sh
./scripts/install.sh
```

By default this installs into `~/.local`: the program, the `ordoa` command, a desktop menu entry, and multi-size icons. Alternatives:

```bash
PREFIX=/usr/local sudo -E ./scripts/install.sh
./scripts/install.sh --self-contained
./scripts/uninstall.sh [--purge]
```

### Arch Linux / Arch Linux ARM

```bash
chmod +x scripts/install-arch.sh scripts/uninstall-arch.sh
./scripts/install-arch.sh
./scripts/uninstall-arch.sh [--purge] [--yes]
```

`install-arch.sh` publishes with NativeAOT, builds a `.pkg.tar.zst`, and installs it with `pacman -U`. It requires `pacman`, `makepkg`, `fakeroot`, `sudo`, and the .NET 10 SDK; if `clang` is missing it tries to install it with pacman.

Supported options:

```bash
./scripts/install-arch.sh --yes          # Do not ask for confirmation
./scripts/install-arch.sh --build-only   # Only build the pacman package
./scripts/install-arch.sh --rid linux-arm64
```

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+T` / `Ctrl+W` | New tab / close tab (closing the last tab closes the window) |
| `Ctrl+Shift+T` | Restore the most recently closed tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab |
| `Alt+1…9` | Switch to tabs 1–9 |
| `Ctrl+\` | Toggle split panes |
| `Ctrl+L` | Edit the address |
| `Ctrl+F` | Focus search; `Enter` starts a recursive search |
| `Ctrl+H` | Show/hide hidden files |
| `Ctrl+D` | Add a bookmark |
| `Ctrl+1` / `Ctrl+2` | List / grid view |
| `Ctrl+Shift+P` | Command palette |
| `Ctrl+,` | Settings |
| `Space` | Quick Look the selected file / close the preview |
| `F3` | Show / hide the persistent preview panel |
| `Enter` / `Esc` | Open the file in preview / close preview |
| `Enter` / `Backspace` | Open / go to parent |
| `Alt+←` / `Alt+→` / `Alt+↑` | Back / forward / parent |
| `Alt+Home` | Home folder |
| `Alt+Enter` | Properties |
| `F1` | About and all shortcuts |
| `F2` | Rename |
| `Ctrl+Shift+R` | Batch rename |
| `Ctrl+Shift+O` | Open With |
| `Ctrl+N` | New window |
| `F5` | Refresh |
| `F9` | Show/hide the sidebar |
| `F11` | Fullscreen |
| `Delete` / `Shift+Delete` | Move to trash / delete permanently |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` / `Ctrl+A` | Copy / cut / paste / select all |
| `Ctrl+Z` / `Ctrl+Shift+Z` | Undo / redo (`Cmd` on macOS) |
| `Ctrl+Shift+N` | New folder |

Mouse: double-click to open; side buttons navigate back/forward; `Ctrl+click` / `Shift+click` add to or extend the selection; drag a multi-selection as one unit to move or copy; drop on the sidebar's **Trash** to move to trash; `Ctrl`/`Shift` force copy/move. Middle-click a file to open its containing folder in a tab, middle-click a tab to close it, and right-click for the full context menu.

## Configuration and data

XDG paths are used on Linux:

| Content | Path |
| --- | --- |
| Settings | `~/.config/ordoa/settings.json` |
| Connections | `~/.config/ordoa/connections.json` |
| Credential fallback file (0600) | `~/.config/ordoa/credentials.json` |
| Bookmarks / session / recent | `~/.config/ordoa/{bookmarks,session,recent}.json` |
| Pending transfer journal | `~/.local/share/ordoa/pending-transfers.json` |
| Thumbnails / temporary downloads | `~/.cache/ordoa` |
| Search index | `~/.cache/ordoa/search-index` |
| Logs | `~/.local/state/ordoa/ordoa.log` |
| Trash | Linux: `~/.local/share/Trash` (see the XDG Trash specification); macOS: `~/.Trash` |
| Publish/package output | `artifacts/` (gitignored) |

macOS uses `~/Library/Application Support/Ordoa` and `~/Library/Caches/Ordoa`; Windows uses `%APPDATA%\Ordoa` and `%LOCALAPPDATA%\Ordoa`.

When registered as the default file manager on Linux, Ordoa writes the user-level `~/.local/share/applications/ordoa.desktop` and `~/.local/share/dbus-1/services/org.freedesktop.FileManager1.service`, and runs `xdg-mime default ordoa.desktop inode/directory`.

### Credential security

- Linux prefers `secret-tool` (Secret Service / libsecret) for passwords.
- macOS prefers `security` (Keychain).
- When unavailable, Ordoa falls back to a JSON file readable only by the current user (0600); this is a deliberate compatibility path, not encrypted storage.
- SFTP prefers `~/.ssh/known_hosts` for host-key validation (hashed hostnames, wildcards, and non-default ports are supported); first connections to unknown hosts use TOFU and record the SHA-256 fingerprint in the connection profile, rejecting later mismatches. `ORDOA_KNOWN_HOSTS` can point to a custom known_hosts file.
- FTPS/WebDAV validate certificates by default; self-signed or invalid certificates are accepted only when explicitly enabled in the connection settings.
- Plain FTP and HTTP WebDAV provide no transport encryption; prefer SFTP/FTPS/HTTPS.

## Architecture

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for detailed module responsibilities, the threading model, and the steps for adding a new protocol.

```
src/Ordoa.Core
├── Abstractions/      IFileSystem, FileSystemBase
├── FileSystems/       LocalFileSystem, TrashFileSystem
│   └── Remote/        FTP, SFTP, SMB, WebDAV
├── Models/            FileEntry, connection profiles, volumes, capabilities
├── Services/          operation queue, search, archives, trash, credentials, connections, settings, sessions
└── Util/              POSIX paths, natural sorting, formatting, XDG paths

src/Ordoa.App
├── Views/             MainWindow, PaneView, dialogs
├── ViewModels/        MainViewModel (with Palette partial), PaneViewModel
│                      (with Layout partial), tab/pane and dialog view models
├── Services/          themes, clipboard, thumbnails, shell, dialogs, icons
├── Styles/            colors, icons, control styles
└── Assets/            application icons

tests/Ordoa.Core.Tests
├── paths/formatting/sorting
├── local filesystem and operation engine
├── trash (XDG round trips)
├── compression/extraction
├── SHA-256
└── WebDAV (in-process WebDAV server end to end)
```

- **Path contract**: local paths use OS paths; remote filesystems use POSIX paths. `IFileSystem` exposes `Combine / GetParent / NormalizePath / GetDisplayPath`, and the UI never concatenates remote paths itself.
- **Operation engine**: a single background queue with cross-filesystem streaming copies, recursive folders, conflict policies, cancellation, and throttled progress; direct moves are attempted only within the same filesystem instance and fall back to copy-then-delete on failure.
- **UI thread safety**: the operation queue reports state through the Dispatcher; pane loading creates visual-related objects on the UI thread.
- **Linux-specific capabilities** are declared through `FileSystemCapabilities`; the UI enables or disables commands by capability rather than hard-coding protocol names.

## Known limitations

- Trash: Linux fully supports browsing, restoring, and emptying; macOS uses the Finder Trash while keeping Ordoa restore information; Windows uses the system Recycle Bin, whose browsing and restore depend on the Shell and may be unavailable in restricted sessions.
- SMB uses port 445 and does not yet support SMB over QUIC, encryption-required shares, or DFS redirection.
- SFTP known_hosts is read-only and is never written back to `~/.ssh/known_hosts`; unknown hosts have their fingerprints stored in Ordoa's connection profile. SSH `Include`, `Match`, wildcard hosts and ProxyJump execution are not fully supported.
- WebDAV currently supports Basic authentication only; Digest, OAuth, and chunked uploads are not implemented.
- Quick Look is not a full media player; normal opening still uses the system default application unless a default was remembered in Open With.
- Markdown rendering is intentionally a preview subset, not a full CommonMark/GFM implementation; raw HTML is reduced to text and interactive scripts are never executed.
- Code highlighting is a compact lexer, not a language server; uncommon languages fall back to plain monospaced text.
- The search index covers local files and the configured roots only. Content, tag, color, remote, and disabled/stale-index queries fall back to the scan-based search.
- Folder sync copies missing or newer files and creates directories; it does not delete extra items, so it is not a destructive mirror mode.
- Transfer journal recovery requeues unfinished copy/move operations after a restart, but a partially copied file is copied again from the start rather than resumed at a byte offset.
- Default file-manager registration and `org.freedesktop.FileManager1` are currently Linux-only. Windows and macOS provide opt-in Explorer/Finder context actions instead; they do not change the system default file manager.
- S3 uses single-request PUTs and does not yet implement multipart upload, bucket policy, versioning, object ACL or server-side encryption settings.
- Network discovery is best effort: mDNS/WS-Discovery may be blocked by firewalls, container networking or a busy port 5353.
- ProxyJump per-hop credentials are configured in the connection dialog and stored in the system keyring. Advanced `Match exec`/`localnetwork` rules are still ignored.
- Elevated operations are synchronous external commands without transfer progress; they are intended for occasional permission repairs, not bulk work.
- New language files cover the common UI; less common strings fall back to English until translated.
- Undo history is kept only for the current session and is cleared when Ordoa exits. Permanent deletion, emptying the trash, overwriting copies/moves, and operations outside the undo scope cannot be undone and clear the history, preventing `Ctrl+Z` from undoing older steps by mistake.
- System-level mounts are not implemented.
- Linux prefers the native Wayland backend (Avalonia 12.1, experimental) and falls back to X11/XWayland when no compositor is available. Set `ORDOA_NO_WAYLAND=1` to force X11. On Wayland, KDE global menus, window icons, and background blur are not yet available.

## Acknowledgements

Ordoa is built on many excellent open-source projects:

- [Avalonia](https://avaloniaui.net/) — cross-platform UI framework
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM source generators
- [FluentFTP](https://github.com/robinrodricks/FluentFTP) — FTP/FTPS
- [SSH.NET](https://github.com/sshnet/SSH.NET) — SFTP
- [SshNet.Agent](https://github.com/darinkes/SshNet.Agent) — SSH Agent authentication
- [SMBLibrary](https://github.com/TalAloni/SMBLibrary) — SMB 2/3
- [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) — Linux D-Bus / FileManager1 integration
- [Material Design Icons](https://pictogrammers.com/library/mdi/) — icon paths (Apache-2.0)
- [Inter](https://rsms.me/inter/) — Latin typeface

## License

MIT License. See [LICENSE](LICENSE) for details.
