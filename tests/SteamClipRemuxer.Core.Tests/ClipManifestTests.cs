using SteamClipRemuxer.Core.Steam;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>
/// Tested against real clip.pb files copied out of Steam's recording folder, unmodified.
/// Steam publishes no schema, so the only thing that can justify the field mapping is that it
/// reproduces values independently confirmed from session.mpd and the session timeline.
/// </summary>
public class ClipManifestTests
{
    private static ClipManifest Load(string name)
    {
        ClipManifest? m = ClipManifest.Parse(File.ReadAllBytes(Path.Combine("Fixtures", name)));
        Assert.NotNull(m);
        return m!;
    }

    [Fact]
    public void Reads_the_session_join_from_a_real_clip()
    {
        ClipManifest m = Load("clip_cropped_7435ms.pb");

        Assert.Equal("timeline_73020260828_204331", m.TimelineFile);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787949811), m.SessionStart);
        Assert.Equal(TimeSpan.FromMilliseconds(4151528), m.StartInSession);
        Assert.Equal(TimeSpan.FromMilliseconds(7435), m.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(4158963), m.EndInSession);
    }

    [Fact]
    public void Start_offset_reconciles_with_the_dash_manifest()
    {
        // session.mpd for this clip carries Period@start = PT1H8M46.14S, measured from the
        // video session. Adding the video session's own offset must land on the timeline
        // coordinate, and this is what makes the two files agree.
        ClipManifest m = Load("clip_cropped_7435ms.pb");

        TimeSpan periodStart = TimeSpan.FromMilliseconds(4126140);
        Assert.Equal(TimeSpan.FromMilliseconds(25388), m.VideoSessionOffset);
        Assert.Equal(m.StartInSession, periodStart + m.VideoSessionOffset);
    }

    [Fact]
    public void Reads_the_video_session_folder()
    {
        ClipManifest m = Load("clip_cropped_7435ms.pb");
        Assert.Equal("bg_730_20260828_204356", m.VideoSessionFolder);
        Assert.Equal(1280, m.Width);
        Assert.Equal(960, m.Height);
    }

    [Theory]
    [InlineData("clip_cropped_7435ms.pb", 7435)]
    [InlineData("clip_cropped_41707ms.pb", 41707)]
    [InlineData("clip_cropped_42037ms.pb", 42037)]
    [InlineData("clip_uncropped_120000ms.pb", 120000)]
    [InlineData("clip_uncropped_120000ms_b.pb", 120000)]
    public void Reads_duration_from_every_real_sample(string fixture, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), Load(fixture).Duration);
    }

    [Theory]
    [InlineData("clip_cropped_7435ms.pb")]
    [InlineData("clip_cropped_41707ms.pb")]
    [InlineData("clip_cropped_42037ms.pb")]
    public void Cropped_clips_are_shorter_than_the_buffer(string fixture)
    {
        Assert.True(Load(fixture).IsCropped(TimeSpan.FromSeconds(120)));
    }

    [Theory]
    [InlineData("clip_uncropped_120000ms.pb")]
    [InlineData("clip_uncropped_120000ms_b.pb")]
    public void An_untouched_clip_is_exactly_the_buffer_length_and_is_not_cropped(string fixture)
    {
        // Steam writes exactly the configured buffer, not approximately, so a strict
        // comparison excludes untouched clips without needing a fudge factor.
        ClipManifest m = Load(fixture);
        Assert.Equal(TimeSpan.FromSeconds(120), m.Duration);
        Assert.False(m.IsCropped(TimeSpan.FromSeconds(120)));
    }

    [Fact]
    public void The_video_session_offset_differs_per_session_so_it_is_never_assumed()
    {
        // Observed between 19.6s and 35.3s. Hardcoding any single value silently misplaces
        // the clip window by up to fifteen seconds.
        Assert.Equal(TimeSpan.FromMilliseconds(25388), Load("clip_cropped_7435ms.pb").VideoSessionOffset);
        Assert.Equal(TimeSpan.FromMilliseconds(29952), Load("clip_cropped_41707ms.pb").VideoSessionOffset);
        Assert.Equal(TimeSpan.FromMilliseconds(19621), Load("clip_cropped_42037ms.pb").VideoSessionOffset);
        Assert.Equal(TimeSpan.FromMilliseconds(28228), Load("clip_uncropped_120000ms.pb").VideoSessionOffset);
        Assert.Equal(TimeSpan.FromMilliseconds(35299), Load("clip_uncropped_120000ms_b.pb").VideoSessionOffset);
    }

    [Fact]
    public void Reads_map_and_mode_when_steam_recorded_them()
    {
        ClipManifest m = Load("clip_cropped_7435ms.pb");
        Assert.Equal("Dust II", m.Attributes["Map"]);
        Assert.Equal("Competitive", m.Attributes["Mode"]);
        Assert.Equal("28/12/4", m.Stats["K/D/A"]);
        Assert.Equal("13 : 9", m.Stats["Score"]);
    }

    [Fact]
    public void Tolerates_clips_that_carry_no_attributes_at_all()
    {
        // Only one of five real samples had the block. Treating it as required would reject
        // the majority of clips, so map and mode come from the timeline instead.
        ClipManifest m = Load("clip_uncropped_120000ms.pb");
        Assert.Empty(m.Attributes);
        Assert.Empty(m.Stats);
        Assert.Equal("timeline_73020260904_191432", m.TimelineFile);
    }

    [Fact]
    public void Resolves_the_game_from_the_app_id()
    {
        // A clip folder carries no game name, only the id, so a clip read this way would
        // otherwise be named "App 730".
        ClipManifest m = Load("clip_cropped_7435ms.pb");
        Assert.Equal(730, m.AppId);
        Assert.Equal("Counter-Strike 2", m.GameName);
    }

    [Fact]
    public void Suggested_name_is_optional()
    {
        Assert.Equal("Counter-Strike 2 - 2026-09-05 10:27:35 PM",
            Load("clip_cropped_41707ms.pb").SuggestedName);
        Assert.Null(Load("clip_uncropped_120000ms.pb").SuggestedName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a protobuf at all")]
    [InlineData("\x08")]
    public void Refuses_unrecognisable_input_rather_than_throwing(string junk)
    {
        // A clip folder written in a format we do not understand should be skipped, not
        // allowed to take down a batch that has other clips to process.
        Assert.Null(ClipManifest.Parse(System.Text.Encoding.UTF8.GetBytes(junk)));
    }

    [Fact]
    public void Refuses_a_truncated_file()
    {
        byte[] full = File.ReadAllBytes(Path.Combine("Fixtures", "clip_cropped_7435ms.pb"));
        Assert.Null(ClipManifest.Parse(full.AsSpan(0, full.Length / 2)));
    }
}
