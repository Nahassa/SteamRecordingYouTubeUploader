using System.Globalization;
using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Highlights;
using SteamClipRemuxer.Core.Planning;
using SteamClipRemuxer.Core.Probing;
using SteamClipRemuxer.Core.Steam;
using SteamClipRemuxer.Core.Youtube;
using Xunit;

namespace SteamClipRemuxer.Core.Tests;

/// <summary>Shared fixtures for the stitching tests: probe output, and a runner that behaves like ffmpeg.</summary>
internal static class StitchFakes
{
    public static string Json(
        string codec = "hevc",
        int width = 1280,
        int height = 960,
        string pixelFormat = "yuvj420p",
        string colorRange = "pc",
        string sampleAspect = "1:1",
        double duration = 3,
        int audioStreams = 1)
    {
        var streams = new List<string>
        {
            $$"""
              {"index":0,"codec_type":"video","codec_name":"{{codec}}","width":{{width}},
               "height":{{height}},"pix_fmt":"{{pixelFormat}}","color_range":"{{colorRange}}",
               "sample_aspect_ratio":"{{sampleAspect}}","start_time":"0.000000"}
              """,
        };

        for (int i = 0; i < audioStreams; i++)
            streams.Add($$"""{"index":{{i + 1}},"codec_type":"audio","codec_name":"aac"}""");

        string seconds = duration.ToString("0.######", CultureInfo.InvariantCulture);
        return "{\"streams\":[" + string.Join(",", streams)
            + "],\"format\":{\"duration\":\"" + seconds + "\"}}";
    }

    public static SourceMedia Media(string path, string? json = null) =>
        SourceMedia.Parse(json ?? Json(), path);

    public sealed class Probe : IMediaProbe
    {
        private readonly Func<string, SourceMedia> _describe;
        public Probe(Func<string, SourceMedia> describe) => _describe = describe;

        public Task<SourceMedia> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(_describe(filePath));
    }

    /// <summary>
    /// Stands in for ffmpeg closely enough that losslessness is actually exercised: a join writes
    /// the bytes its list file names, and an extraction copies them straight back out. A test that
    /// only asserted on the argument list could not tell a copy from a re-encode.
    /// </summary>
    public sealed class Runner : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();

        /// <summary>What a join writes, when it should not be the concatenation of its inputs.</summary>
        public byte[]? Corrupt { get; set; }

        public ProcessResult Result { get; set; } = new(0, "", "");

        public Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(arguments);
            if (!Result.Succeeded) return Task.FromResult(Result);

            string destination = arguments[^1];
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (arguments.Contains("concat"))
            {
                string listPath = arguments[arguments.ToList().IndexOf("-i") + 1];
                byte[] joined = Corrupt ?? ReadListed(listPath);
                File.WriteAllBytes(destination, joined);
            }
            else
            {
                // Both the per-clip remux and the bitstream extraction copy their input.
                string source = arguments[arguments.ToList().IndexOf("-i") + 1];
                File.WriteAllBytes(
                    destination, File.Exists(source) ? File.ReadAllBytes(source) : new byte[] { 1 });
            }

            return Task.FromResult(Result);
        }

        private static byte[] ReadListed(string listPath)
        {
            var bytes = new List<byte>();
            foreach (string line in File.ReadAllLines(listPath))
            {
                if (!line.StartsWith("file '", StringComparison.Ordinal)) continue;
                string path = line[6..^1].Replace(@"'\''", "'", StringComparison.Ordinal);
                bytes.AddRange(File.ReadAllBytes(path));
            }

            return bytes.ToArray();
        }
    }
}

public class StitchArgumentTests
{
    private static readonly SourceMedia Clip = StitchFakes.Media("C:/parts/part00.mp4");

    [Fact]
    public void Reads_the_list_through_the_concat_demuxer_and_copies_every_stream()
    {
        IReadOnlyList<string> args = ClipStitchService.BuildArguments(
            Clip, "C:/w/parts.txt", "C:/out/x.mp4", new RemuxOptions());

        Assert.Equal(new[] { "-f", "concat" }, Pair(args, "-f"));
        // Absolute paths in the list are refused without this.
        Assert.Equal(new[] { "-safe", "0" }, Pair(args, "-safe"));
        Assert.Equal(new[] { "-c", "copy" }, Pair(args, "-c"));
        Assert.Equal(new[] { "-map", "0" }, Pair(args, "-map"));
        Assert.DoesNotContain("-vf", args);
        Assert.DoesNotContain("-filter_complex", args);
    }

    [Fact]
    public void Tags_hevc_so_quicktime_will_play_the_compilation()
    {
        IReadOnlyList<string> args = ClipStitchService.BuildArguments(
            Clip, "C:/w/parts.txt", "C:/out/x.mp4", new RemuxOptions());

        Assert.Contains("hvc1", args);
    }

    [Fact]
    public void Leaves_h264_untagged()
    {
        SourceMedia h264 = StitchFakes.Media("C:/p.mp4", StitchFakes.Json(codec: "h264"));

        Assert.DoesNotContain(
            "hvc1",
            ClipStitchService.BuildArguments(h264, "C:/w/parts.txt", "C:/out/x.mp4", new RemuxOptions()));
    }

    [Fact]
    public void Reasserts_the_display_aspect_because_concat_takes_it_from_the_first_part()
    {
        // 1280x960 with square pixels is 4:3, so the 16:9 stretch has to be applied again here.
        IReadOnlyList<string> args = ClipStitchService.BuildArguments(
            Clip, "C:/w/parts.txt", "C:/out/x.mp4", new RemuxOptions());

        Assert.Equal(new[] { "-aspect", "16:9" }, Pair(args, "-aspect"));
    }

    [Fact]
    public void Omits_the_aspect_when_the_parts_already_display_at_the_target()
    {
        var options = new RemuxOptions { TargetDisplayAspect = new AspectRatio(4, 3) };

        Assert.DoesNotContain(
            "-aspect",
            ClipStitchService.BuildArguments(Clip, "C:/w/parts.txt", "C:/out/x.mp4", options));
    }

    [Fact]
    public void Keeps_variable_frame_timing_across_the_joins()
    {
        IReadOnlyList<string> args = ClipStitchService.BuildArguments(
            Clip, "C:/w/parts.txt", "C:/out/x.mp4", new RemuxOptions());

        Assert.Equal(new[] { "-fps_mode", "passthrough" }, Pair(args, "-fps_mode"));
    }

    [Fact]
    public void Faststart_follows_the_setting()
    {
        Assert.Contains(
            "+faststart",
            ClipStitchService.BuildArguments(Clip, "C:/w/l.txt", "C:/o/x.mp4", new RemuxOptions()));

        Assert.DoesNotContain(
            "+faststart",
            ClipStitchService.BuildArguments(
                Clip, "C:/w/l.txt", "C:/o/x.mp4", new RemuxOptions { FastStart = false }));
    }

    [Fact]
    public void Output_is_the_last_argument()
    {
        IReadOnlyList<string> args = ClipStitchService.BuildArguments(
            Clip, "C:/w/parts.txt", "C:/out/x.mp4", new RemuxOptions());

        Assert.Equal("C:/out/x.mp4", args[^1]);
    }

    private static string[] Pair(IReadOnlyList<string> args, string flag)
    {
        int i = args.ToList().IndexOf(flag);
        return i < 0 ? Array.Empty<string>() : new[] { args[i], args[i + 1] };
    }
}

public class ConcatListTests
{
    [Fact]
    public void Quotes_every_path_so_a_space_does_not_split_it()
    {
        string list = ClipStitchService.BuildConcatList(new[] { @"D:\My Clips\a.mp4" });
        Assert.Equal("file 'D:\\My Clips\\a.mp4'\n", list);
    }

    [Fact]
    public void Escapes_a_quote_inside_a_path()
    {
        // The demuxer only understands '\'' - anything else ends the string early and the
        // remainder of the path becomes a stray option.
        string list = ClipStitchService.BuildConcatList(new[] { "/clips/it's here.mp4" });
        Assert.Equal(@"file '/clips/it'\''s here.mp4'" + "\n", list);
    }

    [Fact]
    public void One_line_per_part_in_the_order_given()
    {
        string list = ClipStitchService.BuildConcatList(new[] { "/a.mp4", "/b.mp4", "/c.mp4" });
        Assert.Equal(new[] { "file '/a.mp4'", "file '/b.mp4'", "file '/c.mp4'" },
            list.TrimEnd('\n').Split('\n'));
    }
}

public class StitchCompatibilityTests
{
    private static SourceMedia M(string json) => StitchFakes.Media("/x.mp4", json);

    [Fact]
    public void Two_clips_from_the_same_recorder_match()
    {
        Assert.Null(ClipStitchService.Mismatch(M(StitchFakes.Json()), M(StitchFakes.Json())));
    }

    [Fact]
    public void Duration_alone_never_stops_a_join() =>
        Assert.Null(ClipStitchService.Mismatch(
            M(StitchFakes.Json(duration: 3)), M(StitchFakes.Json(duration: 41))));

    [Theory]
    [InlineData("codec")]
    [InlineData("size")]
    [InlineData("pixel format")]
    [InlineData("pixel aspect")]
    [InlineData("colour range")]
    [InlineData("audio tracks")]
    public void Anything_the_decoder_configuration_depends_on_stops_it(string difference)
    {
        string other = difference switch
        {
            "codec" => StitchFakes.Json(codec: "h264"),
            "size" => StitchFakes.Json(width: 1920, height: 1080),
            "pixel format" => StitchFakes.Json(pixelFormat: "yuv420p10le"),
            "pixel aspect" => StitchFakes.Json(sampleAspect: "4:3"),
            "colour range" => StitchFakes.Json(colorRange: "tv"),
            _ => StitchFakes.Json(audioStreams: 2),
        };

        string? reason = ClipStitchService.Mismatch(M(StitchFakes.Json()), M(other));

        Assert.NotNull(reason);
        // The message has to name what differs, or the log cannot explain the exclusion.
        Assert.Contains("match the compilation's", reason);
    }

    [Fact]
    public void A_second_audio_track_is_reported_by_count()
    {
        string? reason = ClipStitchService.Mismatch(
            M(StitchFakes.Json()), M(StitchFakes.Json(audioStreams: 2)));

        Assert.Contains("audio track", reason);
    }
}

public class StitchChapterTests
{
    private static StitchPart Part(string label, double startsAt, double duration) => new()
    {
        Path = "/x.mp4",
        Label = label,
        StartsAt = TimeSpan.FromSeconds(startsAt),
        Duration = TimeSpan.FromSeconds(duration),
    };

    [Fact]
    public void First_mark_is_zero_and_the_rest_accumulate()
    {
        string text = StitchChapters.Describe(new[]
        {
            Part("Double kill", 0, 12),
            Part("Ace", 12, 20),
            Part("Triple kill", 32, 15),
        });

        Assert.Equal(
            "0:00  Double kill" + Environment.NewLine
            + "0:12  Ace" + Environment.NewLine
            + "0:32  Triple kill",
            text);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9, "0:09")]
    [InlineData(70, "1:10")]
    [InlineData(3670, "1:01:10")]
    public void Stamps_read_the_way_youtube_expects(double seconds, string expected) =>
        Assert.Equal(expected, StitchChapters.Stamp(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Three_marks_of_ten_seconds_or_more_qualify() =>
        Assert.True(StitchChapters.QualifyAsYouTubeChapters(new[]
        {
            Part("a", 0, 10), Part("b", 10, 30), Part("c", 40, 12),
        }));

    [Fact]
    public void Two_clips_are_too_few_for_chapters() =>
        Assert.False(StitchChapters.QualifyAsYouTubeChapters(new[]
        {
            Part("a", 0, 30), Part("b", 30, 30),
        }));

    [Fact]
    public void A_clip_under_ten_seconds_disqualifies_the_list()
    {
        // Short clips are exactly what this tool produces, so this is the normal case, not an error.
        Assert.False(StitchChapters.QualifyAsYouTubeChapters(new[]
        {
            Part("a", 0, 30), Part("b", 30, 7), Part("c", 37, 30),
        }));
    }

    [Fact]
    public void Chapters_go_below_the_description_with_a_blank_line_between()
    {
        string text = ClipBatchService.WithChapters(
            "Recorded 2026-08-28", new[] { Part("Ace", 0, 12), Part("Kill", 12, 12) });

        Assert.StartsWith("Recorded 2026-08-28", text);
        Assert.Contains(Environment.NewLine + Environment.NewLine + "0:00  Ace", text);
    }

    [Fact]
    public void An_empty_description_leaves_no_blank_line_above_the_timestamps()
    {
        string text = ClipBatchService.WithChapters("", new[] { Part("Ace", 0, 12) });
        Assert.Equal("0:00  Ace", text);
    }
}

public class StitchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sclip-stitch-tests-" + Guid.NewGuid().ToString("N"));

    public StitchServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Part(string name, byte content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new[] { content });
        return path;
    }

    /// <summary>Probes real parts as given, and anything else (the joined file) as the total.</summary>
    private static StitchFakes.Probe ProbeOf(Dictionary<string, string> byName, double joinedDuration) =>
        new(path => StitchFakes.Media(
            path,
            byName.TryGetValue(Path.GetFileName(path), out string? json)
                ? json
                : StitchFakes.Json(duration: joinedDuration)));

    private static Func<int, string> Named(string name) => _ => name;

    [Fact]
    public async Task Joins_the_parts_and_proves_the_video_was_copied()
    {
        string a = Part("a.mp4", 0xA1);
        string b = Part("b.mp4", 0xB2);

        var runner = new StitchFakes.Runner();
        var service = new ClipStitchService(
            runner,
            ProbeOf(new()
            {
                ["a.mp4"] = StitchFakes.Json(duration: 3),
                ["b.mp4"] = StitchFakes.Json(duration: 4),
            }, joinedDuration: 7));

        ClipStitchResult result = await service.StitchAsync(
            new[] { new StitchInput(a, "Ace"), new StitchInput(b, "Double kill") },
            Path.Combine(_root, "out"), Named("Compilation"));

        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal("Compilation.mp4", Path.GetFileName(result.OutputPath));
        Assert.Equal(new byte[] { 0xA1, 0xB2 }, await File.ReadAllBytesAsync(result.OutputPath));
        Assert.Empty(result.Excluded);
    }

    [Fact]
    public async Task Lays_the_parts_out_end_to_end_for_the_chapter_list()
    {
        string a = Part("a.mp4", 1);
        string b = Part("b.mp4", 2);

        var service = new ClipStitchService(
            new StitchFakes.Runner(),
            ProbeOf(new()
            {
                ["a.mp4"] = StitchFakes.Json(duration: 12),
                ["b.mp4"] = StitchFakes.Json(duration: 20),
            }, joinedDuration: 32));

        ClipStitchResult result = await service.StitchAsync(
            new[] { new StitchInput(a, "Ace"), new StitchInput(b, "Kill") },
            Path.Combine(_root, "out"), Named("C"));

        Assert.Equal(TimeSpan.Zero, result.Parts[0].StartsAt);
        Assert.Equal(TimeSpan.FromSeconds(12), result.Parts[1].StartsAt);
        Assert.Equal(new[] { "Ace", "Kill" }, result.Parts.Select(p => p.Label));
    }

    [Fact]
    public async Task Leaves_out_a_part_that_cannot_be_copied_in_and_names_the_file_for_what_remains()
    {
        string a = Part("a.mp4", 1);
        string odd = Part("odd.mp4", 2);
        string c = Part("c.mp4", 3);

        var service = new ClipStitchService(
            new StitchFakes.Runner(),
            ProbeOf(new()
            {
                ["a.mp4"] = StitchFakes.Json(),
                ["odd.mp4"] = StitchFakes.Json(width: 1920, height: 1080),
                ["c.mp4"] = StitchFakes.Json(),
            }, joinedDuration: 6));

        ClipStitchResult result = await service.StitchAsync(
            new[]
            {
                new StitchInput(a, "Ace"),
                new StitchInput(odd, "Kill"),
                new StitchInput(c, "Triple kill"),
            },
            Path.Combine(_root, "out"),
            kept => $"Compilation ({kept} clips)");

        Assert.Equal(2, result.Parts.Count);
        Assert.Single(result.Excluded);
        Assert.Contains("Kill", result.Excluded[0]);
        // The name is decided after the exclusions, so it cannot claim a clip that is not in it.
        Assert.Equal("Compilation (2 clips).mp4", Path.GetFileName(result.OutputPath));
        Assert.Equal(new byte[] { 1, 3 }, await File.ReadAllBytesAsync(result.OutputPath));
    }

    [Fact]
    public async Task Refuses_when_only_one_clip_matches()
    {
        string a = Part("a.mp4", 1);
        string odd = Part("odd.mp4", 2);

        var service = new ClipStitchService(
            new StitchFakes.Runner(),
            ProbeOf(new()
            {
                ["a.mp4"] = StitchFakes.Json(),
                ["odd.mp4"] = StitchFakes.Json(codec: "h264"),
            }, joinedDuration: 3));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StitchAsync(
            new[] { new StitchInput(a, "Ace"), new StitchInput(odd, "Kill") },
            Path.Combine(_root, "out"), Named("C")));

        Assert.False(Directory.Exists(Path.Combine(_root, "out"))
            && Directory.EnumerateFiles(Path.Combine(_root, "out")).Any());
    }

    [Fact]
    public async Task One_clip_is_not_a_compilation() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ClipStitchService(
                new StitchFakes.Runner(), ProbeOf(new(), 3))
            .StitchAsync(
                new[] { new StitchInput(Part("a.mp4", 1), "Ace") },
                Path.Combine(_root, "out"), Named("C")));

    [Fact]
    public async Task Discards_the_output_when_the_video_did_not_survive_the_join()
    {
        string a = Part("a.mp4", 1);
        string b = Part("b.mp4", 2);

        // Anything other than the parts' bytes means something re-encoded or dropped a packet.
        var runner = new StitchFakes.Runner { Corrupt = new byte[] { 9, 9, 9 } };
        var service = new ClipStitchService(runner, ProbeOf(new(), joinedDuration: 6));

        await Assert.ThrowsAsync<IntegrityCheckException>(() => service.StitchAsync(
            new[] { new StitchInput(a, "Ace"), new StitchInput(b, "Kill") },
            Path.Combine(_root, "out"), Named("C")));

        string outFolder = Path.Combine(_root, "out");
        Assert.Empty(Directory.Exists(outFolder)
            ? Directory.GetFiles(outFolder)
            : Array.Empty<string>());
    }

    [Fact]
    public async Task Refuses_to_write_the_compilation_over_one_of_its_own_parts()
    {
        // Exported files can be joined in place, so the output folder may be the input folder.
        string a = Part("Compilation.mp4", 1);
        string b = Part("b.mp4", 2);

        var service = new ClipStitchService(new StitchFakes.Runner(), ProbeOf(new(), 6));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StitchAsync(
                new[] { new StitchInput(a, "Ace"), new StitchInput(b, "Kill") },
                _root, Named("Compilation")));

        Assert.Contains("Compilation.mp4", error.Message);
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(a));
    }

    [Fact]
    public async Task Leaves_no_partial_file_when_ffmpeg_fails()
    {
        var runner = new StitchFakes.Runner { Result = new ProcessResult(1, "", "broken") };
        var service = new ClipStitchService(runner, ProbeOf(new(), 6));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StitchAsync(
            new[]
            {
                new StitchInput(Part("a.mp4", 1), "Ace"),
                new StitchInput(Part("b.mp4", 2), "Kill"),
            },
            Path.Combine(_root, "out"), Named("C")));

        string outFolder = Path.Combine(_root, "out");
        Assert.Empty(Directory.Exists(outFolder)
            ? Directory.GetFiles(outFolder)
            : Array.Empty<string>());
    }
}

public class CompilationNamingTests
{
    private static readonly DateTimeOffset Recorded =
        new(2026, 8, 28, 21, 52, 42, TimeSpan.Zero);

    [Fact]
    public void The_file_name_says_how_many_clips_went_in()
    {
        string name = ClipNaming.Expand(
            ClipNaming.DefaultCompilationTemplate, "Counter-Strike 2", Recorded, highlight: null, count: 4);

        Assert.Equal("Counter-Strike 2 - Compilation - 2026-08-28 21-52-42 (4 clips)", name);
    }

    [Fact]
    public void An_unnamed_game_does_not_leave_a_dangling_separator()
    {
        string name = ClipNaming.Expand(
            ClipNaming.DefaultCompilationTemplate, "", Recorded, highlight: null, count: 2);

        Assert.StartsWith("Compilation", name);
    }

    [Fact]
    public void A_per_clip_name_is_unaffected_by_the_new_placeholder()
    {
        // {count} means nothing outside a compilation, and must not leave a gap behind.
        Assert.Equal(
            "Counter-Strike 2 - 2026-08-28 21-52-42 - Clip",
            ClipNaming.Expand(ClipNaming.DefaultTemplate, "Counter-Strike 2", Recorded, highlight: null));
    }

    [Fact]
    public void The_youtube_title_counts_the_clips_rather_than_naming_one_highlight()
    {
        string title = TitleTemplate.Expand(
            TitleTemplate.DefaultCompilationTitle, "/out/whatever.mp4",
            game: "Counter-Strike 2", count: 5);

        Assert.Equal("Counter-Strike 2 - 5 clip compilation", title);
    }

    [Fact]
    public void An_absent_count_expands_to_nothing()
    {
        Assert.Equal("Counter-Strike 2", TitleTemplate.Expand(
            "{game} {count}", "/out/x.mp4", game: "Counter-Strike 2"));
    }
}

public class StitchBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sclip-stitch-batch-" + Guid.NewGuid().ToString("N"));

    public StitchBatchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A clip folder built around one of the real clip.pb fixtures.</summary>
    private string MakeClip(string folderName, string fixture, byte content)
    {
        string clips = Path.Combine(_root, "in", ClipFolder.ClipsFolderName);
        string clip = Path.Combine(clips, folderName);
        string video = Path.Combine(clip, "video", "bg_730_20260828_204356");
        Directory.CreateDirectory(video);

        File.Copy(Path.Combine("Fixtures", fixture), Path.Combine(clip, "clip.pb"));
        File.WriteAllBytes(Path.Combine(video, "init-stream0.m4s"), new[] { content });
        File.WriteAllBytes(Path.Combine(video, "chunk-stream0-00001.m4s"), new[] { content });
        return clip;
    }

    [Fact]
    public async Task Every_clip_in_the_compilation_is_recorded_as_done()
    {
        MakeClip("clip_730_20260828_221805", "clip_cropped_41707ms.pb", 0xA1);
        MakeClip("clip_730_20260828_223000", "clip_cropped_42037ms.pb", 0xB2);

        var settings = new AppSettings
        {
            InputFolder = Path.Combine(_root, "in"),
            OutputFolder = Path.Combine(_root, "out"),
            // The crop is exercised by the remux tests; this one is about what gets marked.
            RespectSteamCrop = false,
        };

        var processed = new ProcessedClipLog();
        IReadOnlyList<ClipListing> listings = ClipBatchService.FindClips(settings, processed);
        Assert.Equal(2, listings.Count);

        var runner = new StitchFakes.Runner();
        var probe = new StitchFakes.Probe(path => StitchFakes.Media(path));
        var service = new ClipBatchService(
            new ClipRemuxService(runner, probe, new AlwaysSameHasher()),
            new ClipStitchService(runner, probe));

        IReadOnlyList<ClipOutcome> outcomes =
            await service.RunStitchAsync(listings, settings, processed);

        // One compilation, not one file per clip.
        ClipOutcome outcome = Assert.Single(outcomes);
        Assert.True(outcome.Succeeded);
        Assert.True(File.Exists(outcome.OutputPath));

        foreach (ClipListing listing in listings)
            Assert.Equal(ClipState.Remuxed, processed.StateOf(listing.Clip.Manifest.Id));
    }

    [Fact]
    public async Task Two_clips_are_joined_oldest_first()
    {
        MakeClip("clip_730_20260828_221805", "clip_cropped_41707ms.pb", 0xA1);
        MakeClip("clip_730_20260828_223000", "clip_cropped_42037ms.pb", 0xB2);

        var settings = new AppSettings
        {
            InputFolder = Path.Combine(_root, "in"),
            OutputFolder = Path.Combine(_root, "out"),
            RespectSteamCrop = false,
        };

        var processed = new ProcessedClipLog();
        IReadOnlyList<ClipListing> listings = ClipBatchService.FindClips(settings, processed);

        // The list hands them over newest first, which is the wrong way round to watch.
        Assert.True(listings[0].RecordedAt > listings[1].RecordedAt);

        var runner = new StitchFakes.Runner();
        var probe = new StitchFakes.Probe(path => StitchFakes.Media(path));
        var service = new ClipBatchService(
            new ClipRemuxService(runner, probe, new AlwaysSameHasher()),
            new ClipStitchService(runner, probe));

        IReadOnlyList<ClipOutcome> outcomes =
            await service.RunStitchAsync(listings, settings, processed);

        byte[] joined = await File.ReadAllBytesAsync(outcomes[0].OutputPath!);
        Assert.Equal(new byte[] { 0xB2, 0xB2, 0xA1, 0xA1 }, joined);
    }

    [Fact]
    public async Task A_single_unprocessed_clip_is_reported_rather_than_stitched()
    {
        MakeClip("clip_730_20260828_221805", "clip_cropped_41707ms.pb", 0xA1);

        var settings = new AppSettings
        {
            InputFolder = Path.Combine(_root, "in"),
            OutputFolder = Path.Combine(_root, "out"),
        };

        var processed = new ProcessedClipLog();
        IReadOnlyList<ClipListing> listings = ClipBatchService.FindClips(settings, processed);

        var runner = new StitchFakes.Runner();
        var probe = new StitchFakes.Probe(path => StitchFakes.Media(path));
        var service = new ClipBatchService(
            new ClipRemuxService(runner, probe, new AlwaysSameHasher()),
            new ClipStitchService(runner, probe));

        ClipOutcome outcome = Assert.Single(
            await service.RunStitchAsync(listings, settings, processed));

        Assert.False(outcome.Succeeded);
        Assert.Contains("at least two", outcome.Error);
    }

    private sealed class AlwaysSameHasher : IVideoStreamHasher
    {
        public Task<string> HashAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult("same");
    }
}
