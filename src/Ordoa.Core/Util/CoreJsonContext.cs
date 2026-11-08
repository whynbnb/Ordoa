using System.Text.Json.Serialization;
using Ordoa.Core.Models;
using Ordoa.Core.Services;

namespace Ordoa.Core.Util;

/// <summary>
/// Source-generated JSON metadata used by every persisted file. This keeps
/// settings, sessions, bookmarks and credentials working under NativeAOT where
/// reflection-based serialization is disabled.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<ConnectionProfile>))]
[JsonSerializable(typeof(List<RecentEntry>))]
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(List<Bookmark>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, Ordoa.Core.Models.FileTagInfo>))]
[JsonSerializable(typeof(Ordoa.Core.Models.FileTagInfo))]
[JsonSerializable(typeof(List<Ordoa.Core.Models.FileTagInfo>))]
[JsonSerializable(typeof(List<SavedWorkspace>))]
[JsonSerializable(typeof(List<Ordoa.Core.Services.PendingTransfer>))]
internal sealed partial class CoreJsonContext : JsonSerializerContext
{
}
