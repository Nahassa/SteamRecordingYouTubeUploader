namespace SteamClipRemuxer.Gui;

/// <summary>
/// The application icon, for every window. Assets/app-icon.svg is the source; app.ico is
/// rendered from it at 16, 24, 32, 48, 64 and 256 px.
/// </summary>
internal static class AppIcon
{
    private static readonly Lazy<Icon?> Shared = new(Load);

    /// <summary>The icon, or null if the resource is missing - a window without one still works.</summary>
    public static Icon? Value => Shared.Value;

    private static Icon? Load()
    {
        using Stream? stream = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico");
        return stream is null ? null : new Icon(stream);
    }
}
