using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Steam;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>
/// Finding nothing has to say why.
///
/// Every per-clip skip was already explained - no clip.pb, unreadable clip.pb, no segments - but
/// finding no clips at all returned an empty list in silence. That is the hardest failure to
/// report, because the window just looks empty, and the easiest to blame on whatever changed
/// most recently.
/// </summary>
public class ClipDiscoveryLogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sclip-discover-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _messages = new();
    private readonly IPipelineLog _log;

    public ClipDiscoveryLogTests()
    {
        Directory.CreateDirectory(_root);
        _log = new DelegatePipelineLog((_, message) => _messages.Add(message));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Said => string.Join(" | ", _messages);

    [Fact]
    public void No_folder_set_says_so()
    {
        Assert.Empty(ClipFolder.Discover(string.Empty, _log));
        Assert.Contains("No recording folder is set", Said);
    }

    [Fact]
    public void A_folder_that_is_not_there_says_so()
    {
        string missing = Path.Combine(_root, "nope");

        Assert.Empty(ClipFolder.Discover(missing, _log));
        Assert.Contains("does not exist", Said);
        Assert.Contains(missing, Said);
    }

    [Fact]
    public void A_folder_with_no_clips_in_it_explains_where_they_live()
    {
        // The likeliest real case: the recording folder is set, but to the wrong level.
        Assert.Empty(ClipFolder.Discover(_root, _log));
        Assert.Contains("No clip folders under", Said);
        Assert.Contains("clips", Said);
        Assert.Contains("clip_<app id>", Said);
    }

    [Fact]
    public void Finding_clips_says_where_it_looked()
    {
        // So a run against the wrong folder is visible even when it does find something.
        MakeClip("clip_730_20260828_221805", "clip_cropped_7435ms.pb");

        Assert.Single(ClipFolder.Discover(_root, _log));
        Assert.Contains("Reading clips from", Said);
    }

    [Fact]
    public void A_clip_that_cannot_be_read_is_counted_as_well_as_named()
    {
        // One good, one with no clip.pb at all.
        MakeClip("clip_730_20260828_221805", "clip_cropped_7435ms.pb");
        Directory.CreateDirectory(Path.Combine(_root, ClipFolder.ClipsFolderName, "clip_730_broken"));

        Assert.Single(ClipFolder.Discover(_root, _log));
        Assert.Contains("no clip.pb", Said);
        Assert.Contains("1 of 2 clip folder(s) could not be read", Said);
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
}
