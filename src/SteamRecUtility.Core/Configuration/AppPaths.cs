namespace SteamRecUtility.Core.Configuration;

/// <summary>
/// Where the app keeps its own state. Deliberately not beside the executable: a single-file
/// published app installed under Program Files cannot write there without elevation, which is
/// how the previous version silently failed to save settings.
/// </summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamRecUtility");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string YouTubeCredentialsFile => Path.Combine(DataDirectory, "youtube_credentials.json");
    public static string YouTubeTokenStore => Path.Combine(DataDirectory, "youtube_token");

    public static void EnsureCreated() => Directory.CreateDirectory(DataDirectory);
}
