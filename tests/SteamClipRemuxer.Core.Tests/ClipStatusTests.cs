using SteamClipRemuxer.Core.Configuration;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

public class ClipStatusTests
{
    private static ProcessedClip Entry(
        bool remuxed = false,
        bool uploaded = false,
        string outputPath = "C:/out/clip.mp4",
        bool cutToHighlights = false) => new()
    {
        Id = "timeline_x:1000:5000",
        OutputPath = outputPath,
        RemuxedAt = remuxed ? DateTimeOffset.UtcNow : null,
        UploadedAt = uploaded ? DateTimeOffset.UtcNow : null,
        CutToHighlights = cutToHighlights,
    };

    private static ClipStatus Status(ProcessedClip? entry, bool fileExists = true) =>
        ClipStatus.For(entry, _ => fileExists);

    [Fact]
    public void A_clip_with_no_entry_is_untouched()
    {
        ClipStatus status = Status(null);

        Assert.True(status.Untouched);
        Assert.False(status.Remuxed);
        Assert.False(status.Uploaded);
        Assert.False(status.PendingUpload);
        Assert.False(status.OutputMissing);
    }

    [Fact]
    public void A_remuxed_clip_is_pending_upload()
    {
        ClipStatus status = Status(Entry(remuxed: true));

        Assert.True(status.Remuxed);
        Assert.True(status.PendingUpload);
        Assert.False(status.Uploaded);
        Assert.False(status.Untouched);
    }

    [Fact]
    public void An_uploaded_clip_is_not_also_pending()
    {
        // Uploading is the finish line; showing it in both columns would make the pending filter
        // useless as a list of what is left to do.
        ClipStatus status = Status(Entry(remuxed: true, uploaded: true));

        Assert.True(status.Uploaded);
        Assert.False(status.PendingUpload);
    }

    [Fact]
    public void A_remuxed_clip_whose_file_has_gone_is_reported_missing()
    {
        Assert.True(Status(Entry(remuxed: true), fileExists: false).OutputMissing);
    }

    [Fact]
    public void An_uploaded_clip_is_never_reported_missing()
    {
        // A successful upload files the output under uploaded/, so the recorded path stops
        // existing by design. Flagging that would mark every finished clip as broken.
        Assert.False(Status(Entry(remuxed: true, uploaded: true), fileExists: false).OutputMissing);
    }

    [Fact]
    public void A_clip_with_no_recorded_path_is_not_reported_missing()
    {
        // Older entries were written before the path was recorded; nothing is known, so nothing
        // is claimed.
        Assert.False(Status(Entry(remuxed: true, outputPath: ""), fileExists: false).OutputMissing);
    }

    [Fact]
    public void The_highlights_flag_is_carried_through()
    {
        Assert.True(Status(Entry(remuxed: true, cutToHighlights: true)).CutToHighlights);
        Assert.False(Status(Entry(remuxed: true)).CutToHighlights);
    }
}
