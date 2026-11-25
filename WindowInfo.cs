namespace WindowThumbWall;

internal sealed class WindowInfo
{
    public required IntPtr Handle { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }
    public WindowIdentity? Identity { get; init; }
    internal long? ObservedLifetime { get; set; }
    public override string ToString() => Title;
}
