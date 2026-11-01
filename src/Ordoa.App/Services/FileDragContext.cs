using Avalonia.Input;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

public sealed record FileDragPayload(IReadOnlyList<OperationSource> Sources);

public static class FileDragContext
{
    public static readonly DataFormat<FileDragPayload> Format =
        DataFormat.CreateInProcessFormat<FileDragPayload>("ordoa-drag");

    public static FileDragPayload? Current { get; set; }
}
