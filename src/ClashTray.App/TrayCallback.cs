namespace ClashTray.App;

internal enum TrayInteraction { LeftClick, RightClick }

internal static class TrayCallback
{
    // v4: HIWORD is the icon ID. Ignore mouse-up notifications to avoid
    // processing one click twice when the shell also sends NIN_SELECT.
    public static TrayInteraction? Decode(long lParam, bool version4)
    {
        var message = (uint)lParam & 0xFFFF;
        return (version4, message) switch
        {
            (true, 0x400 or 0x401) or (false, 0x0202) => TrayInteraction.LeftClick,
            (true, 0x007B) or (false, 0x0205) => TrayInteraction.RightClick,
            _ => null
        };
    }
}
