using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Execution;
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

    [Fact]
    public void Long_clips_stay_hidden_unless_asked_for() =>
        Assert.False(new AppSettings().IncludeLongClips);
}

/// <summary>
/// The threshold as the list actually applies it, over real clip folders rather than manifests
/// on their own.
/// </summary>
public class LongClipListingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sclip-long-" + Guid.NewGuid().ToString("N"));

    public LongClipListingTests()
    {
        // One clip cropped in Steam, one left at the full 120s buffer.
        MakeClip("clip_730_cropped", "clip_cropped_41707ms.pb");
        MakeClip("clip_730_untouched", "clip_uncropped_120000ms.pb");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void MakeClip(string folderName, string fixture)
    {
        string clip = Path.Combine(_root, ClipFolder.ClipsFolderName, folderName);
        string video = Path.Combine(clip, "video", "bg_730_20260828_204356");
        Directory.CreateDirectory(video);

        File.Copy(Path.Combine("Fixtures", fixture), Path.Combine(clip, "clip.pb"));
        File.WriteAllBytes(Path.Combine(video, "init-stream0.m4s"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(video, "chunk-stream0-00001.m4s"), new byte[] { 2 });
    }

    private AppSettings Settings(bool includeLong) => new()
    {
        InputFolder = _root,
        IncludeLongClips = includeLong,
    };

    [Fact]
    public void By_default_only_the_clip_cropped_in_steam_is_listed()
    {
        ClipListing listing = Assert.Single(
            ClipBatchService.FindClips(Settings(includeLong: false)));

        Assert.Equal("clip_730_cropped", listing.Clip.FolderName);
    }

    [Fact]
    public void Asking_for_the_long_ones_brings_the_untouched_clip_back()
    {
        // The kill-highlights cut exists for exactly these, so hiding them has to be reversible.
        IReadOnlyList<ClipListing> listings =
            ClipBatchService.FindClips(Settings(includeLong: true));

        Assert.Equal(2, listings.Count);
        Assert.Contains(listings, l => l.Clip.FolderName == "clip_730_untouched");
    }

    [Fact]
    public void The_threshold_still_decides_which_clips_are_the_long_ones()
    {
        // Raising it past the buffer makes the untouched clip cropped by the same strict rule,
        // so it lists without the option at all.
        var settings = Settings(includeLong: false);
        settings.MaxClipSeconds = 121;

        Assert.Equal(2, ClipBatchService.FindClips(settings).Count);
    }
}
