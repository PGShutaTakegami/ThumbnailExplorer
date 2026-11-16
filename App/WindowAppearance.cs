using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ThumbnailExplorer;

public partial class MainWindow
{
    private const int UseImmersiveDarkMode = 20;
    private const int CaptionColor = 35;
    private const int CaptionTextColor = 36;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme(this);
    }

    private void ApplyTitleBarTheme(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        uint dark = !SystemParameters.HighContrast && _settings.Theme == "Dark" ? 1u : 0u;
        uint background = GetCaptionColor("Panel");
        uint text = GetCaptionColor("InkText");
        DwmSetWindowAttribute(handle, UseImmersiveDarkMode, in dark, sizeof(uint));
        DwmSetWindowAttribute(handle, CaptionColor, in background, sizeof(uint));
        DwmSetWindowAttribute(handle, CaptionTextColor, in text, sizeof(uint));
    }

    private uint GetCaptionColor(string resource)
    {
        if (SystemParameters.HighContrast)
            return uint.MaxValue; // DWMWA_COLOR_DEFAULT restores the Windows colors.

        Color color = ((SolidColorBrush)Resources[resource]).Color;
        return (uint)(color.R | (color.G << 8) | (color.B << 16));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, in uint value, uint size);
}
