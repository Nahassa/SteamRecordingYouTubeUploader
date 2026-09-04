using SteamRecUtility.Core.Files;
using SteamRecUtility.Core.Probing;
using SteamRecUtility.Core.Thumbnails;
using SteamRecUtility.Core.Timelines;
using SteamRecUtility.Core.Youtube;
using Xunit;

namespace SteamRecUtility.Core.Tests;

public class ClipNameTests
{
    // The exact filename Steam produced for the sample recording.
    private const string Real = "CounterStrike_2__20260808_104557_PM__Double_kill";

    [Fact]
    public void Parses_a_real_steam_filename()
    {
        ClipName c = ClipName.Parse(Real);

        Assert.Equal("CounterStrike 2", c.Game);
        Assert.Equal("Double kill", c.Title);
        Assert.NotNull(c.RecordedAt);
        Assert.Equal(new DateTime(2026, 8, 8, 22, 45, 57), c.RecordedAt);
    }

    [Fact]
    public void Reads_pm_as_afternoon()
    {
        Assert.Equal(22, ClipName.Parse(Real).RecordedAt!.Value.Hour);
        Assert.Equal(10, ClipName.Parse("G__20260808_104557_AM__x").RecordedAt!.Value.Hour);
    }

    [Fact]
    public void Keeps_double_underscores_inside_a_clip_title()
    {
        Assert.Equal("Double kill then ace", ClipName.Parse("G__20260808_104557_PM__Double_kill__then_ace").Title);
    }

    [Fact]
    public void Falls_back_to_iso_dates_in_other_naming_schemes()
    {
        ClipName c = ClipName.Parse("My clip 2024-01-15 final");
        Assert.Equal(new DateTime(2024, 1, 15), c.RecordedAt);
        Assert.Equal("", c.Game);
    }

    [Fact]
    public void Tolerates_a_name_with_no_recognisable_date()
    {
        ClipName c = ClipName.Parse("just_a_clip");
        Assert.Null(c.RecordedAt);
        Assert.Equal("just a clip", c.Title);
    }
}

public class TitleTemplateTests
{
    private const string RealFile = "C:/rec/CounterStrike_2__20260808_104557_PM__Double_kill.mp4";

    [Fact]
    public void Regression_recording_date_works_on_steam_filenames()
    {
        // The original implementation matched only "yyyy-MM-dd", so {recording_date} expanded
        // to an empty string for every file Steam actually produces.
        Assert.Equal("2026-08-08", TitleTemplate.Expand("{recording_date}", RealFile));
    }

    [Fact]
    public void Expands_the_parts_of_a_steam_filename()
    {
        Assert.Equal("Double kill", TitleTemplate.Expand("{clip}", RealFile));
        Assert.Equal("CounterStrike 2", TitleTemplate.Expand("{game}", RealFile));
    }

    [Fact]
    public void Builds_a_usable_title_from_a_template()
    {
        Assert.Equal(
            "CounterStrike 2 - Double kill (2026-08-08)",
            TitleTemplate.Expand("{game} - {clip} ({recording_date})", RealFile));
    }

    [Fact]
    public void Filename_placeholders_stay_verbatim()
    {
        Assert.Equal(
            "CounterStrike_2__20260808_104557_PM__Double_kill",
            TitleTemplate.Expand("{filename}", RealFile));
        Assert.Equal(
            "CounterStrike_2__20260808_104557_PM__Double_kill.mp4",
            TitleTemplate.Expand("{filename_ext}", RealFile));
    }

    [Fact]
    public void Clock_placeholders_use_the_supplied_time()
    {
        var when = new DateTime(2030, 3, 4, 5, 6, 7);
        Assert.Equal("2030-03-04", TitleTemplate.Expand("{date}", RealFile, now: when));
        Assert.Equal("2030", TitleTemplate.Expand("{year}", RealFile, now: when));
        Assert.Equal("03", TitleTemplate.Expand("{month}", RealFile, now: when));
    }

    [Fact]
    public void Removes_steam_style_timestamps_when_asked()
    {
        string result = TitleTemplate.Expand("{filename}", RealFile, removeDateFromFilename: true);
        Assert.DoesNotContain("20260808", result);
        Assert.DoesNotContain("104557", result);
        Assert.Contains("Double_kill", result);
    }

    [Fact]
    public void Removes_iso_dates_when_asked() =>
        Assert.Equal("My clip", TitleTemplate.RemoveDates("My clip 2024-01-15"));

    [Fact]
    public void Removes_custom_patterns_case_insensitively() =>
        Assert.Equal("Double kill", TitleTemplate.RemovePatterns("CounterStrike Double kill", "counterstrike"));

    [Fact]
    public void Tidies_separators_left_behind_by_a_removal() =>
        Assert.Equal("Game - Title", TitleTemplate.RemovePatterns("Game - REMOVE - Title", "REMOVE"));
}

public class TimelineFixerTests
{
    [Fact]
    public void Regression_numeric_keys_order_numerically_not_as_text()
    {
        // Ordinal sorting gives 0, 1, 10, 11, 2 - which silently scrambles the timeline.
        string[] keys = { "0", "1", "10", "11", "2" };
        Assert.Equal(new[] { "0", "1", "2", "10", "11" }, TimelineFixer.OrderEntryKeys(keys));
    }

    [Fact]
    public void Non_numeric_keys_still_sort_ordinally() =>
        Assert.Equal(new[] { "a", "b", "c" }, TimelineFixer.OrderEntryKeys(new[] { "c", "a", "b" }));

    [Fact]
    public void Mixed_keys_fall_back_to_ordinal_ordering() =>
        Assert.Equal(new[] { "1", "2", "x" }, TimelineFixer.OrderEntryKeys(new[] { "2", "x", "1" }));

    [Fact]
    public void Converts_an_entries_object_into_an_ordered_array()
    {
        string dir = Path.Combine(Path.GetTempPath(), "srec-tl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "t.json");
        File.WriteAllText(file, """{"entries":{"10":{"n":10},"2":{"n":2},"1":{"n":1}}}""");

        Assert.True(TimelineFixer.FixFile(file));

        string fixedJson = File.ReadAllText(file);
        int i1 = fixedJson.IndexOf("\"n\": 1", StringComparison.Ordinal);
        int i2 = fixedJson.IndexOf("\"n\": 2", StringComparison.Ordinal);
        int i10 = fixedJson.IndexOf("\"n\": 10", StringComparison.Ordinal);
        Assert.True(i1 < i2 && i2 < i10, "entries must be ordered 1, 2, 10");
        Assert.True(File.Exists(file + ".bak"), "original must be backed up");
    }

    [Fact]
    public void Leaves_an_already_valid_file_untouched()
    {
        string dir = Path.Combine(Path.GetTempPath(), "srec-tl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "t.json");
        File.WriteAllText(file, """{"entries":[{"n":1}]}""");

        Assert.False(TimelineFixer.FixFile(file));
        Assert.False(File.Exists(file + ".bak"));
    }
}

public class FileOrganizerTests
{
    [Fact]
    public void Never_overwrites_an_existing_file_at_the_destination()
    {
        string dir = Path.Combine(Path.GetTempPath(), "srec-fo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "clip.mp4"), "original");

        string chosen = FileOrganizer.UniqueDestination(dir, "clip.mp4");

        Assert.Equal(Path.Combine(dir, "clip (2).mp4"), chosen);
        Assert.Equal("original", File.ReadAllText(Path.Combine(dir, "clip.mp4")));
    }

    [Fact]
    public void Uses_the_plain_name_when_the_destination_is_free()
    {
        string dir = Path.Combine(Path.GetTempPath(), "srec-fo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.Equal(Path.Combine(dir, "clip.mp4"), FileOrganizer.UniqueDestination(dir, "clip.mp4"));
    }
}

public class ThumbnailExtractorTests
{
    private static SourceMedia Media(int w, int h, string sar) => SourceMedia.Parse($$"""
        {"format":{"duration":"9.304"},
         "streams":[{"index":0,"codec_type":"video","codec_name":"hevc","width":{{w}},
                     "height":{{h}},"sample_aspect_ratio":"{{sar}}","pix_fmt":"yuv420p"}]}
        """, "C:/rec/a.mp4");

    [Fact]
    public void Preview_is_rendered_at_display_aspect_not_stored_size()
    {
        // A 1280x960 clip tagged SAR 4:3 must preview as 1706x960 - the stretched look.
        (int w, int h) = ThumbnailExtractor.DisplayDimensions(Media(1280, 960, "4:3"));
        Assert.Equal(1706, w);
        Assert.Equal(960, h);
    }

    [Fact]
    public void Square_pixel_clip_previews_at_its_stored_size()
    {
        (int w, int h) = ThumbnailExtractor.DisplayDimensions(Media(1920, 1080, "1:1"));
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    [Fact]
    public void Preview_seeks_into_the_clip_rather_than_grabbing_the_first_frame()
    {
        IReadOnlyList<string> args = ThumbnailExtractor.BuildArguments(Media(1280, 960, "4:3"), 4.652, "/tmp/t.jpg");
        int i = args.ToList().IndexOf("-ss");
        Assert.True(i >= 0);
        Assert.Equal("4.652", args[i + 1]);
    }
}
