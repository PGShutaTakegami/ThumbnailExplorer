using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;

namespace ThumbnailExplorer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Use the Windows display language, independently of regional number and date settings.
        CultureInfo language;
        try { language = CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()); }
        catch (CultureNotFoundException) { language = CultureInfo.CurrentUICulture; }

        CultureInfo.DefaultThreadCurrentUICulture = language;
        CultureInfo.CurrentUICulture = language;
        base.OnStartup(e);
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetUserDefaultUILanguage();
}
