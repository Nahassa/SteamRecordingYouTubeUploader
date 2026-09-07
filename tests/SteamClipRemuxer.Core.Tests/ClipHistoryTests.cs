using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Highlights;
using SteamClipRemuxer.Core.Youtube;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>
/// The per-clip history the clip list is drawn from, and the groundwork for uploading clips
/// that were remuxed while upload was switched off.
/// </summary>
public class ClipHistoryTests
{
    [Fact]
    public void A_clip_moves_through_new_then_remuxed_then_uploaded()
    {
        var log = new ProcessedClipLog();
        Assert.Equal(ClipState.New, log.StateOf("id"));

        log.MarkRemuxed("id", "clip_730_1", "Double kill", "D:/out/clip.mp4");
        Assert.Equal(ClipState.Remuxed, log.StateOf("id"));

        log.MarkUploaded("id", "yt-1");
        Assert.Equal(ClipState.Uploaded, log.StateOf("id"));
    }

    [Fact]
    public void Remuxed_but_not_uploaded_clips_are_listed_for_a_later_upload_run()
    {
        var log = new ProcessedClipLog();
        log.MarkRemuxed("a", "clip_a", "Double kill", "D:/out/a.mp4");
        log.MarkRemuxed("b", "clip_b", "Triple kill", "D:/out/b.mp4");
        log.MarkUploaded("b", "yt-b");

        IReadOnlyList<ProcessedClip> pending = log.PendingUpload();

        Assert.Single(pending);
        Assert.Equal("a", pending[0].Id);
    }

    [Fact]
    public void A_pending_clip_carries_what_an_upload_needs_without_remuxing_again()
    {
        var recorded = new DateTimeOffset(2026, 8, 28, 21, 52, 42, TimeSpan.Zero);
        var log = new ProcessedClipLog();
        log.MarkRemuxed("a", "clip_a", "Double kill with the AK-47", "D:/out/a.mp4", recorded);

        ProcessedClip pending = log.PendingUpload().Single();

        Assert.Equal("D:/out/a.mp4", pending.OutputPath);
        Assert.Equal(recorded, pending.RecordedAt);
        Assert.Equal("Double kill with the AK-47", pending.Title);
    }

    [Fact]
    public void Uploading_keeps_the_output_path_and_recorded_moment()
    {
        var recorded = new DateTimeOffset(2026, 8, 28, 21, 52, 42, TimeSpan.Zero);
        var log = new ProcessedClipLog();
        log.MarkRemuxed("a", "clip_a", "Double kill", "D:/out/a.mp4", recorded);
        log.MarkUploaded("a", "yt-a");

        ProcessedClip entry = log.Find("a")!;
        Assert.Equal("D:/out/a.mp4", entry.OutputPath);
        Assert.Equal(recorded, entry.RecordedAt);
    }

    [Fact]
    public void History_survives_a_round_trip_so_the_list_can_be_greyed_out_after_a_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), "sclip-hist-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var recorded = new DateTimeOffset(2026, 8, 28, 21, 52, 42, TimeSpan.Zero);
            var log = new ProcessedClipLog();
            log.MarkRemuxed("a", "clip_a", "Double kill", "D:/out/a.mp4", recorded);
            log.MarkRemuxed("b", "clip_b", "Ace", "D:/out/b.mp4", recorded);
            log.MarkUploaded("b", "yt-b");
            log.Save(path);

            ProcessedClipLog loaded = ProcessedClipLog.Load(path);

            Assert.Equal(ClipState.Remuxed, loaded.StateOf("a"));
            Assert.Equal(ClipState.Uploaded, loaded.StateOf("b"));
            Assert.Equal("D:/out/a.mp4", loaded.PendingUpload().Single().OutputPath);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class YouTubeClipTitleTests
{
    private static readonly Highlight DoubleKill = new()
    {
        KillCount = 2, Weapon = "AK-47", Map = "Mirage", Mode = "Competitive", Round = 7,
    };

    [Fact]
    public void The_default_clip_title_carries_no_timestamp()
    {
        string title = TitleTemplate.Expand(
            TitleTemplate.DefaultClipTitle,
            "Counter-Strike 2 - 2026-08-28 21-52-42 - Double kill.mp4",
            highlight: DoubleKill,
            recordedAt: new DateTimeOffset(2026, 8, 28, 21, 52, 42, TimeSpan.Zero),
            game: "Counter-Strike 2");

        Assert.Equal("Counter-Strike 2 - Double kill with the AK-47", title);
        Assert.DoesNotContain("2026", title);
        Assert.DoesNotContain("21-52-42", title);
    }

    [Fact]
    public void Stripping_the_timestamp_removes_the_time_as_well_as_the_date()
    {
        // The settings checkbox says "Strip the timestamp out of titles", but the time in this
        // tool's own clip names survived it.
        string title = TitleTemplate.Expand(
            "{filename}",
            "Counter-Strike 2 - 2026-08-28 21-52-42 - Double kill.mp4",
            removeDateFromFilename: true);

        Assert.Equal("Counter-Strike 2 - Double kill", title);
    }

    [Theory]
    [InlineData("Clip 2026-08-28 21-52-42 end", "Clip end")]
    [InlineData("Clip 2026-08-28 end", "Clip end")]
    [InlineData("Clip 21-52-42 end", "Clip end")]
    [InlineData("Clip 21:52:42 end", "Clip end")]
    // Tidy collapses the doubled separator a removal leaves behind, which is pre-existing.
    [InlineData("CounterStrike_2__20260808_104557_PM__Double_kill", "CounterStrike_2-Double_kill")]
    public void Removes_every_timestamp_layout_the_tool_has_produced(string input, string expected)
    {
        Assert.Equal(expected, TitleTemplate.RemoveDates(input));
    }

    [Fact]
    public void A_scoreline_is_not_mistaken_for_a_time()
    {
        Assert.Equal("Score 13 : 9", TitleTemplate.RemoveDates("Score 13 : 9"));
    }

    [Fact]
    public void Placeholders_are_available_for_a_richer_title()
    {
        string title = TitleTemplate.Expand(
            "{highlight} on {map} ({mode}, round {round}) - {kills} kills with the {weapon}",
            "clip.mp4", highlight: DoubleKill);

        Assert.Equal("Double kill on Mirage (Competitive, round 7) - 2 kills with the AK-47", title);
    }

    [Fact]
    public void The_exact_recorded_moment_beats_anything_guessed_from_the_filename()
    {
        string title = TitleTemplate.Expand(
            "{recording_date} {recording_time}",
            "CounterStrike_2__20260808_104557_PM__Double_kill.mp4",
            recordedAt: new DateTimeOffset(2026, 8, 28, 21, 52, 42, TimeSpan.Zero));

        Assert.Equal("2026-08-28 21:52:42", title);
    }
}
