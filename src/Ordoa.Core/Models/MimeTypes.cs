namespace Ordoa.Core.Models;

/// <summary>
/// Lightweight extension based MIME mapping. Avoids a native libmagic dependency while
/// still giving useful type names, icons and "open with" hints.
/// </summary>
public static class MimeTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["txt"] = "text/plain", ["md"] = "text/markdown", ["log"] = "text/plain",
        ["csv"] = "text/csv", ["tsv"] = "text/tab-separated-values",
        ["html"] = "text/html", ["htm"] = "text/html", ["xml"] = "application/xml",
        ["css"] = "text/css", ["js"] = "text/javascript", ["mjs"] = "text/javascript",
        ["ts"] = "text/typescript", ["json"] = "application/json", ["yaml"] = "application/yaml",
        ["yml"] = "application/yaml", ["toml"] = "application/toml", ["ini"] = "text/plain",
        ["conf"] = "text/plain", ["cfg"] = "text/plain", ["sh"] = "text/x-shellscript",
        ["bash"] = "text/x-shellscript", ["zsh"] = "text/x-shellscript", ["fish"] = "text/x-shellscript",
        ["ps1"] = "text/x-powershell", ["bat"] = "text/x-msdos-batch", ["cmd"] = "text/x-msdos-batch",
        ["py"] = "text/x-python", ["rb"] = "text/x-ruby", ["go"] = "text/x-go",
        ["rs"] = "text/x-rust", ["java"] = "text/x-java", ["kt"] = "text/x-kotlin",
        ["c"] = "text/x-c", ["h"] = "text/x-c", ["cc"] = "text/x-c++", ["cpp"] = "text/x-c++",
        ["hpp"] = "text/x-c++", ["cs"] = "text/x-csharp", ["fs"] = "text/x-fsharp",
        ["vb"] = "text/x-vb", ["php"] = "text/x-php", ["sql"] = "application/sql",
        ["pdf"] = "application/pdf", ["epub"] = "application/epub+zip",
        ["doc"] = "application/msword", ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xls"] = "application/vnd.ms-excel", ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["ppt"] = "application/vnd.ms-powerpoint", ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ["odt"] = "application/vnd.oasis.opendocument.text", ["ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        ["odp"] = "application/vnd.oasis.opendocument.presentation",
        ["rtf"] = "application/rtf",
        ["png"] = "image/png", ["jpg"] = "image/jpeg", ["jpeg"] = "image/jpeg", ["gif"] = "image/gif",
        ["webp"] = "image/webp", ["avif"] = "image/avif", ["bmp"] = "image/bmp", ["ico"] = "image/x-icon",
        ["svg"] = "image/svg+xml", ["tif"] = "image/tiff", ["tiff"] = "image/tiff", ["heic"] = "image/heic",
        ["raw"] = "image/x-raw", ["psd"] = "image/vnd.adobe.photoshop",
        ["mp3"] = "audio/mpeg", ["flac"] = "audio/flac", ["ogg"] = "audio/ogg", ["oga"] = "audio/ogg",
        ["opus"] = "audio/opus", ["wav"] = "audio/wav", ["m4a"] = "audio/mp4", ["aac"] = "audio/aac",
        ["wma"] = "audio/x-ms-wma", ["mid"] = "audio/midi", ["midi"] = "audio/midi",
        ["mp4"] = "video/mp4", ["mkv"] = "video/x-matroska", ["webm"] = "video/webm",
        ["avi"] = "video/x-msvideo", ["mov"] = "video/quicktime", ["wmv"] = "video/x-ms-wmv",
        ["flv"] = "video/x-flv", ["m4v"] = "video/mp4", ["mpg"] = "video/mpeg", ["mpeg"] = "video/mpeg",
        ["zip"] = "application/zip", ["tar"] = "application/x-tar", ["gz"] = "application/gzip",
        ["tgz"] = "application/gzip", ["bz2"] = "application/x-bzip2", ["xz"] = "application/x-xz",
        ["zst"] = "application/zstd", ["7z"] = "application/x-7z-compressed", ["rar"] = "application/vnd.rar",
        ["deb"] = "application/vnd.debian.binary-package", ["rpm"] = "application/x-rpm",
        ["apk"] = "application/vnd.android.package-archive", ["jar"] = "application/java-archive",
        ["iso"] = "application/x-iso9660-image", ["img"] = "application/octet-stream",
        ["ttf"] = "font/ttf", ["otf"] = "font/otf", ["woff"] = "font/woff", ["woff2"] = "font/woff2",
        ["desktop"] = "application/x-desktop", ["service"] = "text/plain",
        ["torrent"] = "application/x-bittorrent", ["sqlite"] = "application/vnd.sqlite3",
        ["db"] = "application/vnd.sqlite3", ["pem"] = "application/x-pem-file",
        ["asc"] = "application/pgp-signature", ["gpg"] = "application/pgp-encrypted",
    };

    public static string FromExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return "application/octet-stream";
        return Map.TryGetValue(extension.TrimStart('.'), out var mime) ? mime : "application/octet-stream";
    }

    /// <summary>Human readable Chinese description for an extension.</summary>
    public static string Describe(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return "文件";

        return extension.ToLowerInvariant() switch
        {
            "txt" or "md" or "log" or "rtf" => "文本文档",
            "pdf" => "PDF 文档",
            "doc" or "docx" or "odt" => "文字文档",
            "xls" or "xlsx" or "ods" or "csv" => "表格",
            "ppt" or "pptx" or "odp" => "演示文稿",
            "png" or "jpg" or "jpeg" or "gif" or "webp" or "avif" or "bmp" or "tif" or "tiff" or "svg" or "heic" or "ico" => "图片",
            "mp3" or "flac" or "ogg" or "opus" or "wav" or "m4a" or "aac" or "wma" => "音频",
            "mp4" or "mkv" or "webm" or "avi" or "mov" or "wmv" or "m4v" or "mpg" or "mpeg" => "视频",
            "zip" or "tar" or "gz" or "tgz" or "bz2" or "xz" or "zst" or "7z" or "rar" => "压缩包",
            "js" or "mjs" or "ts" or "py" or "rb" or "go" or "rs" or "java" or "kt" or "c" or "h" or "cc" or "cpp" or "hpp" or "cs" or "fs" or "php" or "sh" or "bash" or "zsh" or "fish" or "lua" or "swift" => "源代码",
            "json" or "yaml" or "yml" or "toml" or "xml" or "ini" or "conf" or "cfg" => "配置文件",
            "deb" or "rpm" or "apk" => "安装包",
            "iso" or "img" => "磁盘映像",
            "ttf" or "otf" or "woff" or "woff2" => "字体",
            "desktop" => "启动器",
            "html" or "htm" => "网页",
            "css" => "样式表",
            "sql" => "数据库脚本",
            "torrent" => "种子文件",
            _ => extension.ToUpperInvariant() + " 文件",
        };
    }

    public static bool IsImage(string extension) => extension.ToLowerInvariant() is
        "png" or "jpg" or "jpeg" or "gif" or "webp" or "avif" or "bmp" or "ico";

    public static bool IsText(string extension) => extension.ToLowerInvariant() is
        "txt" or "md" or "log" or "json" or "yaml" or "yml" or "toml" or "xml" or "ini" or "conf" or "cfg" or
        "csv" or "tsv" or "html" or "htm" or "css" or "js" or "mjs" or "ts" or "py" or "rb" or "go" or "rs" or
        "java" or "kt" or "c" or "h" or "cc" or "cpp" or "hpp" or "cs" or "fs" or "php" or "sh" or "bash" or
        "zsh" or "fish" or "lua" or "swift" or "sql" or "service" or "desktop" or "svg";

    public static bool IsArchive(string extension) => extension.ToLowerInvariant() is
        "zip" or "tar" or "gz" or "tgz" or "bz2" or "xz" or "zst" or "7z" or "rar" or "jar";
}
