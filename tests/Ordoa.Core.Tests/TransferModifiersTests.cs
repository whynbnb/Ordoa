using Ordoa.Core.Services;

namespace Ordoa.Core.Tests;

public sealed class TransferModifiersTests
{
    [Theory]
    [InlineData(false, false, true, TransferMode.Move)]
    [InlineData(false, false, false, TransferMode.Copy)]
    [InlineData(true, false, true, TransferMode.Copy)]
    [InlineData(true, false, false, TransferMode.Copy)]
    [InlineData(false, true, true, TransferMode.Move)]
    [InlineData(false, true, false, TransferMode.Move)]
    // Ctrl wins when both modifiers are held.
    [InlineData(true, true, true, TransferMode.Copy)]
    [InlineData(true, true, false, TransferMode.Copy)]
    public void Resolve_UsesModifiersAndFileSystem(bool control, bool shift, bool sameFileSystem, TransferMode expected)
        => Assert.Equal(expected, TransferModifiers.Resolve(control, shift, sameFileSystem));
}
