using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Steam;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

public class ProcessedClipLogTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "sclip-log-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void An_unseen_clip_is_never_skipped()
    {
        var log = new ProcessedClipLog();
        Assert.False(log.ShouldSkip("timeline_x:1000:5000", uploadEnabled: false));
        Assert.False(log.ShouldSkip("timeline_x:1000:5000", uploadEnabled: true));
    }

    [Fact]
    public void A_remuxed_clip_is_skipped_when_not_uploading()
    {
        var log = new ProcessedClipLog();
        log.MarkRemuxed("id", "clip_730_1", "Double kill");

        Assert.True(log.ShouldSkip("id", uploadEnabled: false));
    }

    [Fact]
    public void A_remuxed_clip_is_not_skipped_when_uploading()
    {
        // Remuxed with upload switched off is not done for a run that does upload. Treating it
        // as done would silently withhold a video the user wants on YouTube.
        var log = new ProcessedClipLog();
        log.MarkRemuxed("id", "clip_730_1", "Double kill");

        Assert.False(log.ShouldSkip("id", uploadEnabled: true));
    }

    [Fact]
    public void An_uploaded_clip_is_skipped_when_uploading()
    {
        var log = new ProcessedClipLog();
        log.MarkRemuxed("id", "clip_730_1", "Double kill");
        log.MarkUploaded("id", "yt-abc123");

        Assert.True(log.ShouldSkip("id", uploadEnabled: true));
        Assert.Equal("yt-abc123", log.Find("id")!.YouTubeVideoId);
    }

    [Fact]
    public void Marking_uploaded_keeps_what_the_remux_recorded()
    {
        var log = new ProcessedClipLog();
        log.MarkRemuxed("id", "clip_730_1", "Double kill with the AK-47");
        log.MarkUploaded("id", "yt-abc123");

        ProcessedClip entry = log.Find("id")!;
        Assert.Equal("Double kill with the AK-47", entry.Title);
        Assert.Equal("clip_730_1", entry.ClipFolder);
        Assert.NotNull(entry.RemuxedAt);
        Assert.NotNull(entry.UploadedAt);
    }

    [Fact]
    public void Forgetting_a_clip_lets_it_run_again()
    {
        var log = new ProcessedClipLog();
        log.MarkRemuxed("id", "clip_730_1", "Double kill");

        Assert.True(log.Forget("id"));
        Assert.False(log.ShouldSkip("id", uploadEnabled: false));
        Assert.False(log.Forget("id"));
    }

    [Fact]
    public void Survives_a_round_trip_through_disk()
    {
        string path = TempFile();
        try
        {
            var log = new ProcessedClipLog();
            log.MarkRemuxed("timeline_a:100:200", "clip_730_1", "Double kill");
            log.MarkUploaded("timeline_a:100:200", "yt-1");
            log.Save(path);

            ProcessedClipLog loaded = ProcessedClipLog.Load(path);
            Assert.Equal(1, loaded.Count);
            Assert.True(loaded.ShouldSkip("timeline_a:100:200", uploadEnabled: true));
            Assert.Equal("yt-1", loaded.Find("timeline_a:100:200")!.YouTubeVideoId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_log_loads_empty_rather_than_failing()
    {
        Assert.Equal(0, ProcessedClipLog.Load(TempFile()).Count);
    }

    [Fact]
    public void Unreadable_json_starts_empty_and_reports_why()
    {
        string path = TempFile();
        File.WriteAllText(path, "{ this is not the log }");
        try
        {
            string? reported = null;
            ProcessedClipLog log = ProcessedClipLog.Load(path, e => reported = e);

            // Redoing work is recoverable; refusing to run is not.
            Assert.Equal(0, log.Count);
            Assert.NotNull(reported);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        string path = TempFile();
        try
        {
            var log = new ProcessedClipLog();
            log.MarkRemuxed("id", "clip", "title");
            log.Save(path);

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Duplicate_ids_in_a_hand_edited_file_collapse_to_one()
    {
        var log = new ProcessedClipLog(new[]
        {
            new ProcessedClip { Id = "same", Title = "first" },
            new ProcessedClip { Id = "same", Title = "second" },
        });

        Assert.Equal(1, log.Count);
        Assert.Equal("second", log.Find("same")!.Title);
    }

    [Fact]
    public void A_real_clip_identifies_itself_by_content_not_by_folder()
    {
        // The folder is named for when Save was pressed, so re-saving the same moment would look
        // like new work. Session, offset and length do not move.
        ClipManifest a = ClipManifest.Parse(
            File.ReadAllBytes(Path.Combine("Fixtures", "clip_cropped_7435ms.pb")))!;

        Assert.Equal("timeline_73020260828_204331:4151528:7435", a.Id);
    }

    [Fact]
    public void Different_clips_from_one_session_do_not_collide()
    {
        // A 90 minute session yields many clips sharing a single timeline file, which is why the
        // timeline name alone cannot be the key.
        ClipManifest a = ClipManifest.Parse(
            File.ReadAllBytes(Path.Combine("Fixtures", "clip_cropped_41707ms.pb")))!;
        ClipManifest b = ClipManifest.Parse(
            File.ReadAllBytes(Path.Combine("Fixtures", "clip_cropped_42037ms.pb")))!;

        Assert.NotEqual(a.Id, b.Id);
    }
}
