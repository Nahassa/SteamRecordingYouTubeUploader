using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Steam;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>
/// The threshold that separates a clip cropped in Steam from one left at the full recording
/// buffer, exercised against the real clip.pb files rather than constructed values.
/// </summary>
public class ClipFilterTests
{
    private static ClipManifest Load(string name) =>
        ClipManifest.Parse(File.ReadAllBytes(Path.Combine("Fixtures", name)))!;

    [Fact]
    public void The_default_threshold_matches_steams_default_buffer()
    {
        Assert.Equal(120, new AppSettings().MaxClipSeconds);
    }

    [Fact]
    public void Default_source_is_the_existing_exported_file_workflow()
    {
        // Reading Steam's clip folders replaces the export step, so it is opt-in rather than a
        // change of behaviour for an existing install.
        Assert.Equal(ClipSource.ExportedFiles, new AppSettings().ClipSource);
    }

    [Theory]
    [InlineData("clip_cropped_7435ms.pb")]
    [InlineData("clip_cropped_41707ms.pb")]
    [InlineData("clip_cropped_42037ms.pb")]
    public void Clips_cropped_in_steam_pass_the_default_threshold(string fixture)
    {
        Assert.True(Load(fixture).IsCropped(TimeSpan.FromSeconds(new AppSettings().MaxClipSeconds)));
    }

    [Theory]
    [InlineData("clip_uncropped_120000ms.pb")]
    [InlineData("clip_uncropped_120000ms_b.pb")]
    public void Untouched_clips_are_excluded_by_the_default_threshold(string fixture)
    {
        Assert.False(Load(fixture).IsCropped(TimeSpan.FromSeconds(new AppSettings().MaxClipSeconds)));
    }

    [Fact]
    public void A_lowered_threshold_excludes_longer_crops_too()
    {
        // The buffer length is a Steam setting, so the threshold has to follow it rather than
        // assume 120.
        ClipManifest fortyOneSeconds = Load("clip_cropped_41707ms.pb");

        Assert.True(fortyOneSeconds.IsCropped(TimeSpan.FromSeconds(60)));
        Assert.False(fortyOneSeconds.IsCropped(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void A_clip_exactly_at_the_buffer_length_is_excluded()
    {
        // Steam writes the buffer length exactly, not approximately, so the comparison is
        // strict and needs no tolerance.
        Assert.False(Load("clip_uncropped_120000ms.pb").IsCropped(TimeSpan.FromMilliseconds(120000)));
        Assert.True(Load("clip_uncropped_120000ms.pb").IsCropped(TimeSpan.FromMilliseconds(120001)));
    }
}
