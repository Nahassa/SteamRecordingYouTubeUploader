using System.Text.Json;
using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Youtube;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>
/// Dropping the time from a title while keeping the date.
///
/// The checkbox this replaces could only take the whole timestamp, because RemoveDates matches
/// the combined "2026-10-20 19-22-36" form first and by design. The literal "Also remove text"
/// box could do it for exactly one clip, the time being different in every other one. So titles
/// read "CS2 - 2026-10-20 19-22-36 - Dust2 multikills" with no way to reach
/// "CS2 - 2026-10-20 - Dust2 multikills" short of dropping the date as well.
/// </summary>
public class TitleTimestampTests
{
    [Theory]
    [InlineData("CS2 - 2026-10-20 19-22-36 - Dust2 multikills", "CS2 - 2026-10-20 - Dust2 multikills")]
    [InlineData("CS2 - 2026-10-20 18-43-47 - Anubis multikills", "CS2 - 2026-10-20 - Anubis multikills")]
    [InlineData("CS2 - 2026-10-18 20-21-23 - Cache 4K on A", "CS2 - 2026-10-18 - Cache 4K on A")]
    public void The_time_goes_and_the_date_stays(string title, string expected) =>
        Assert.Equal(expected, TitleTemplate.RemoveTimes(title));

    [Fact]
    public void A_colon_separated_time_goes_too() =>
        Assert.Equal("CS2 - 2026-10-20 - Ace", TitleTemplate.RemoveTimes("CS2 - 2026-10-20 19:22:36 - Ace"));

    [Fact]
    public void A_title_with_no_time_in_it_is_left_alone()
    {
        Assert.Equal("CS2 - Dust II - Multikills", TitleTemplate.RemoveTimes("CS2 - Dust II - Multikills"));
        Assert.Equal("CS2 - 2026-10-20 - Ace", TitleTemplate.RemoveTimes("CS2 - 2026-10-20 - Ace"));
    }

    [Fact]
    public void A_scoreline_is_not_a_time()
    {
        // Two groups rather than three, so the pattern cannot match it. This is why the rule is
        // anchored rather than just looking for digits and dashes.
        Assert.Equal("Ace 13-9 comeback", TitleTemplate.RemoveTimes("Ace 13-9 comeback"));
        Assert.Equal("Score 13 : 9", TitleTemplate.RemoveTimes("Score 13 : 9"));
    }

    [Fact]
    public void The_date_itself_is_never_mistaken_for_a_time() =>
        // "26-10-20" sits inside "2026-10-20" and is preceded by a digit, which rules it out.
        Assert.Equal("2026-10-20", TitleTemplate.RemoveTimes("2026-10-20"));

    [Fact]
    public void Steams_own_compact_stamp_is_left_whole() =>
        // No separators to recognise, and no date left to keep in it either - that is a case for
        // removing the lot, not for this.
        Assert.Equal(
            "CS2 - 20261020_192236 - Ace",
            TitleTemplate.RemoveTimes("CS2 - 20261020_192236 - Ace"));

    [Theory]
    [InlineData(TitleTimestamp.Keep, "CS2 - 2026-10-20 19-22-36 - Ace")]
    [InlineData(TitleTimestamp.DateOnly, "CS2 - 2026-10-20 - Ace")]
    [InlineData(TitleTimestamp.None, "CS2 - Ace")]
    public void Each_setting_reaches_the_title(TitleTimestamp setting, string expected) =>
        Assert.Equal(expected, TitleTemplate.Expand(
            "{filename}", "/out/CS2 - 2026-10-20 19-22-36 - Ace.mp4", setting));

    [Fact]
    public void Keeping_the_timestamp_is_the_default() =>
        Assert.Equal(TitleTimestamp.Keep, new AppSettings().YouTubeTitleTimestamp);

    // The setting changed shape from a bool. Load answers a parse failure with defaults, so
    // getting this wrong would not have produced an error - it would have silently reset every
    // other setting the user had ever chosen.

    [Fact]
    public void A_settings_file_from_before_the_change_still_loads_everything_else()
    {
        string path = Write("""
            {
              "OutputFolder": "D:\\clips",
              "YouTubeRemoveDateFromFilename": true,
              "YouTubeTags": "cs2,ace"
            }
            """);

        try
        {
            AppSettings loaded = AppSettings.Load(path);

            Assert.Equal("D:\\clips", loaded.OutputFolder);
            Assert.Equal("cs2,ace", loaded.YouTubeTags);
            Assert.Equal(TitleTimestamp.None, loaded.YouTubeTitleTimestamp);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_old_setting_left_off_means_the_timestamp_stays()
    {
        string path = Write("""{"YouTubeRemoveDateFromFilename": false}""");

        try
        {
            Assert.Equal(TitleTimestamp.Keep, AppSettings.Load(path).YouTubeTitleTimestamp);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_choice_already_made_beats_the_old_setting()
    {
        // Both present: the new one wins, so a translation cannot undo a deliberate choice.
        string path = Write("""
            {
              "YouTubeRemoveDateFromFilename": true,
              "YouTubeTitleTimestamp": "DateOnly"
            }
            """);

        try
        {
            Assert.Equal(TitleTimestamp.DateOnly, AppSettings.Load(path).YouTubeTitleTimestamp);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_translation_survives_a_save_and_a_reload()
    {
        string path = Write("""{"YouTubeRemoveDateFromFilename": true}""");

        try
        {
            AppSettings loaded = AppSettings.Load(path);
            loaded.Save(path);

            AppSettings again = AppSettings.Load(path);
            Assert.Equal(TitleTimestamp.None, again.YouTubeTitleTimestamp);

            // The old flag is cleared once translated, so it cannot re-apply over a later choice.
            Assert.False(JsonDocument.Parse(File.ReadAllText(path))
                .RootElement.GetProperty("YouTubeRemoveDateFromFilename").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Write(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
