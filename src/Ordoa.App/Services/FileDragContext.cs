using Avalonia.Input;
using Ordoa.Core.Services;

namespace Ordoa.App.Services;

public sealed record FileDragPayload(IReadOnlyList<OperationSource> Sources);

public static class FileDragContext
{
    public static readonly DataFormat<FileDragPayload> Format =
        DataFormat.CreateInProcessFormat<FileDragPayload>("ordoa-drag");

    public static FileDragPayload? Current { get; set; }

    /// <summary>
    /// Tooltips can briefly appear while the pointer is captured by a drag
    /// operation, causing the platform drag cursor to flicker.
    /// </summary>
    public static bool IsDragging { get; private set; }

    public static event Action? DragEnded;

    public static void BeginDrag()
    {
        IsDragging = true;
    }

    public static void EndDrag()
    {
        if (!IsDragging)
            return;

        IsDragging = false;
        DragEnded?.Invoke();
    }
}
