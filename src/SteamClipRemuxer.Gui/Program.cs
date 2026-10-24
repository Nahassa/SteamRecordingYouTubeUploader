namespace SteamClipRemuxer.Gui;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Follows the Windows setting rather than forcing either way. No longer experimental as
        // of .NET 10 - on .NET 9 this needed a WFO5001 suppression - and it has to be set before
        // Application.Run, because it is read as each window is created.
        //
        // Quietly does nothing on Windows 10, and is ignored while a contrast theme is active,
        // which is correct: a contrast theme is a stronger statement of intent than this is.
        // It also does not follow a theme change made while the app is open; the next launch
        // picks it up.
        try
        {
            Application.SetColorMode(SystemColorMode.System);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
        {
            // An older Windows, or a build without it. A light window is not a reason to refuse
            // to start.
        }

        Application.Run(new MainForm());
    }
}
