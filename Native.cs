using System.Runtime.InteropServices;

namespace McServerTray;

static class Native
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// Windows 11: small rounded corners (and the system shadow), like other flyouts. No-op elsewhere.
    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 3; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUNDSMALL
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }
}
