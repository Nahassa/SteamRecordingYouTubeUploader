using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Steam;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

public class ClipNameStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "sclip-names-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void A_clip_with_no_name_set_uses_the_generated_one() =>
        Assert.Null(new ClipNames().For("timeline_x:1000:5000"));

    [Fact]
    public void A_name_is_kept_as_given()
    {
        var names = new ClipNames();
        Assert.Equal("Clutch on Mirage", names.Set("id", "Clutch on Mirage"));
        Assert.Equal("Clutch on Mirage", names.For("id"));
    }

    [Fact]
    public void A_name_is_made_safe_to_write_as_a_filename()
    {
        // It becomes a file name, so a colon or a slash would be an IOException rather than a
        // title. The sanitiser is the one the generated names already go through.
        var names = new ClipNames();

        string? stored = names.Set("id", "4k: ace / round 12");

        Assert.NotNull(stored);
        Assert.DoesNotContain(':', stored);
        Assert.DoesNotContain('/', stored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blanking_a_name_clears_it_rather_than_storing_something(string? blank)
    {
        // The sanitiser falls back to "Clip" for empty input, so a blank has to be caught before
        // it or clearing a name would rename the clip to "Clip".
        var names = new ClipNames();
        names.Set("id", "Something");

        Assert.Null(names.Set("id", blank));
        Assert.Null(names.For("id"));
        Assert.Equal(0, names.Count);
    }

    [Fact]
    public void Names_survive_a_save_and_load()
    {
        string path = TempFile();
        try
        {
            var names = new ClipNames();
            names.Set("timeline_x:1000:5000", "Clutch on Mirage");
            names.Set("timeline_x:9000:5000", "Ace");
            names.Save(path);

            ClipNames reloaded = ClipNames.Load(path);

            Assert.Equal("Clutch on Mirage", reloaded.For("timeline_x:1000:5000"));
            Assert.Equal("Ace", reloaded.For("timeline_x:9000:5000"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void An_unreadable_file_is_not_a_reason_to_refuse_to_run()
    {
        string path = TempFile();
        try
        {
            File.WriteAllText(path, "not json");
            string? reported = null;

            ClipNames names = ClipNames.Load(path, onError: m => reported = m);

            Assert.Equal(0, names.Count);
            Assert.NotNull(reported);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public class RenamedClipTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sclip-rename-" + Guid.NewGuid().ToString("N"));

    public RenamedClipTests()
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

    private ClipListing Listing(ClipNames? names = null) =>
        Assert.Single(ClipBatchService.FindClips(
            new AppSettings { InputFolder = _root }, processed: null, names));

    [Fact]
    public void Without_a_name_the_template_decides()
    {
        ClipListing listing = Listing();

        Assert.False(listing.IsRenamed);
        Assert.Null(listing.CustomName);
        Assert.StartsWith("Counter-Strike 2 - ", listing.SuggestedName);
    }

    [Fact]
    public void A_name_the_user_set_becomes_the_output_name()
    {
        var names = new ClipNames();
        names.Set(Listing().Clip.Manifest.Id, "Clutch on Mirage");

        ClipListing renamed = Listing(names);

        Assert.True(renamed.IsRenamed);
        Assert.Equal("Clutch on Mirage", renamed.CustomName);
        Assert.Equal("Clutch on Mirage", renamed.SuggestedName);
    }

    [Fact]
    public void Clearing_the_name_puts_the_generated_one_back()
    {
        var names = new ClipNames();
        string id = Listing().Clip.Manifest.Id;

        names.Set(id, "Clutch on Mirage");
        names.Set(id, "");

        Assert.False(Listing(names).IsRenamed);
        Assert.StartsWith("Counter-Strike 2 - ", Listing(names).SuggestedName);
    }

    [Fact]
    public void A_name_set_for_one_clip_does_not_reach_another()
    {
        var names = new ClipNames();
        names.Set("some_other_clip:1:2", "Not this one");

        Assert.False(Listing(names).IsRenamed);
    }
}
