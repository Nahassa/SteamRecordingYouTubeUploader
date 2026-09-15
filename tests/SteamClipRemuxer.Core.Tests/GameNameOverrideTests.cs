using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Steam;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>
/// The name set for an app id has to reach the file name and the YouTube title, through a save
/// and load of the settings. Every hop is somewhere it could be dropped.
/// </summary>
public class GameNameOverrideTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sclip-gamename-" + Guid.NewGuid().ToString("N"));

    public GameNameOverrideTests()
    {
        string clip = Path.Combine(_root, ClipFolder.ClipsFolderName, "clip_730_20260828_221805");
        string video = Path.Combine(clip, "video", "bg_730_20260828_204356");
        Directory.CreateDirectory(video);

        File.Copy(Path.Combine("Fixtures", "clip_cropped_41707ms.pb"), Path.Combine(clip, "clip.pb"));
        File.WriteAllBytes(Path.Combine(video, "init-stream0.m4s"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(video, "chunk-stream0-00001.m4s"), new byte[] { 2 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Without_an_override_the_built_in_name_is_used()
    {
        ClipListing listing = Assert.Single(
            ClipBatchService.FindClips(new AppSettings { InputFolder = _root }));

        Assert.Equal("Counter-Strike 2", listing.GameName);
    }

    [Fact]
    public void A_name_set_for_the_app_id_reaches_the_listing_and_the_file_name()
    {
        var settings = new AppSettings { InputFolder = _root };
        settings.GameNames["730"] = "CS2";

        ClipListing listing = Assert.Single(ClipBatchService.FindClips(settings));

        Assert.Equal("CS2", listing.GameName);
        Assert.StartsWith("CS2 - ", listing.SuggestedName);
    }

    [Fact]
    public void The_name_survives_the_settings_file()
    {
        // Typed into the settings window, written to disk, read back on the next launch.
        string path = Path.Combine(_root, "settings.json");

        var saved = new AppSettings { InputFolder = _root };
        saved.GameNames = SteamApps.ParseOverrides("730 = CS2\r\n440 = Team Fortress 2\r\n");
        saved.Save(path);

        AppSettings loaded = AppSettings.Load(path);

        Assert.Equal("CS2", loaded.GameNames["730"]);
        Assert.Equal("CS2", Assert.Single(ClipBatchService.FindClips(loaded)).GameName);
    }

    [Fact]
    public void Windows_line_endings_from_the_settings_box_are_handled()
    {
        // A multiline TextBox hands back \r\n; splitting on \n alone leaves a stray \r.
        Assert.Equal("CS2", SteamApps.ParseOverrides("730 = CS2\r\n")["730"]);
        Assert.Equal("CS2", SteamApps.NameFor(730, SteamApps.ParseOverrides("730 = CS2\r\n")));
    }
}
