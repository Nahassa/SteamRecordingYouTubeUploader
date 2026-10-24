using System.Globalization;
using System.Text.Json;

namespace SteamClipRemuxer.Core.Probing;

public sealed record MediaStream(int Index, string CodecType, string CodecName);

/// <summary>
/// Everything the pipeline is allowed to know about an input file. Every decision downstream
/// reads from here; nothing assumes geometry, codec, colour or stream layout.
/// </summary>
public sealed record SourceMedia
{
    public required string FilePath { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string VideoCodec { get; init; }
    public required AspectRatio SampleAspect { get; init; }
    public required string PixelFormat { get; init; }
    public string? ColorRange { get; init; }
    public string? ColorSpace { get; init; }

    /// <summary>
    /// Colour tags beyond the two above. Needed the moment anything re-encodes: filters do not
    /// reliably carry these across a format conversion, so they have to be read here and tagged
    /// explicitly on output. Leaving them unspecified pushes the guess onto every player, which
    /// is how one file looks different in two apps.
    /// </summary>
    public string? ColorPrimaries { get; init; }

    public string? ColorTransfer { get; init; }

    /// <summary>Where chroma samples sit relative to luma; "left" on Steam's captures.</summary>
    public string? ChromaLocation { get; init; }

    /// <summary>The codec profile, e.g. "Main" for 8-bit HEVC or "Main 10" for 10-bit.</summary>
    public string? Profile { get; init; }

    /// <summary>
    /// ffprobe's bits_per_raw_sample, when it reports one. Steam's captures do not carry it, so
    /// this is null on the files this tool exists for - read <see cref="BitDepth"/> instead.
    /// </summary>
    public int? BitsPerRawSample { get; init; }

    /// <summary>Frames in the video stream, when the container counts them.</summary>
    public int? FrameCount { get; init; }

    /// <summary>The nominal and measured frame rates, as ffprobe's raw "N/D". For the log.</summary>
    public string? RFrameRate { get; init; }

    public string? AvgFrameRate { get; init; }

    /// <summary>The first audio stream's codec and channel count, or null when there is no audio.</summary>
    public string? AudioCodec { get; init; }

    public int? AudioChannels { get; init; }

    public double DurationSeconds { get; init; }

    /// <summary>
    /// Presentation time of the first frame. Concatenated DASH segments keep the timestamps
    /// they had inside the recording session, so this is a long way from zero and a trim offset
    /// has to be measured against it rather than assumed to start at the beginning.
    /// </summary>
    public double StartTimeSeconds { get; init; }
    public required IReadOnlyList<MediaStream> Streams { get; init; }

    /// <summary>How the file actually displays, accounting for non-square pixels.</summary>
    public AspectRatio DisplayAspect => SampleAspect.DisplayAspectFor(Width, Height);

    public bool IsHevc => VideoCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Bits per sample, from the container's own figure when it gives one and from the pixel
    /// format otherwise.
    ///
    /// The fallback is not a nicety. Steam's recordings carry no bits_per_raw_sample at all, so
    /// reading only that key reports zero for the exact files this pipeline handles - and a
    /// zero read as "8" by accident is how a 10-bit source silently loses precision.
    /// </summary>
    public int BitDepth => BitsPerRawSample
        ?? PixelFormat switch
        {
            var f when f.Contains("p16", StringComparison.Ordinal) => 16,
            var f when f.Contains("p14", StringComparison.Ordinal) => 14,
            var f when f.Contains("p12", StringComparison.Ordinal) => 12,
            var f when f.Contains("p10", StringComparison.Ordinal) => 10,
            // NVENC's own 10-bit surface format, which does not follow the pNN spelling.
            "p010le" or "p010be" => 10,
            _ => 8,
        };

    /// <summary>Full-range ("pc") video, which Steam produces and which must not be re-tagged.</summary>
    public bool IsFullRange => string.Equals(ColorRange, "pc", StringComparison.OrdinalIgnoreCase);

    public int AudioStreamCount => Streams.Count(s => s.CodecType == "audio");

    /// <summary>
    /// Parses `ffprobe -print_format json -show_format -show_streams` output.
    /// Pure, so it is tested directly against captured real-world output.
    /// </summary>
    public static SourceMedia Parse(string ffprobeJson, string filePath)
    {
        using JsonDocument doc = JsonDocument.Parse(ffprobeJson);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("streams", out JsonElement streams) || streams.ValueKind != JsonValueKind.Array)
            throw new InvalidMediaException($"ffprobe returned no stream list for '{filePath}'.");

        var all = new List<MediaStream>();
        JsonElement? video = null;
        JsonElement? audio = null;
        foreach (JsonElement s in streams.EnumerateArray())
        {
            string type = Str(s, "codec_type") ?? "";
            string name = Str(s, "codec_name") ?? "";
            all.Add(new MediaStream(Int(s, "index") ?? all.Count, type, name));
            if (video is null && type == "video") video = s;
            if (audio is null && type == "audio") audio = s;
        }

        if (video is null)
            throw new InvalidMediaException($"'{filePath}' contains no video stream.");

        JsonElement v = video.Value;
        int width = Int(v, "width") ?? throw new InvalidMediaException($"'{filePath}' has no frame width.");
        int height = Int(v, "height") ?? throw new InvalidMediaException($"'{filePath}' has no frame height.");

        // ffprobe omits sample_aspect_ratio entirely for square pixels, and emits "0:1" when
        // it is unknown. Both mean square.
        AspectRatio sar = AspectRatio.TryParse(Str(v, "sample_aspect_ratio")) ?? AspectRatio.Square;

        double startTime = 0;
        if (double.TryParse(
                Str(v, "start_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out double st))
        {
            startTime = st;
        }

        double duration = 0;
        if (root.TryGetProperty("format", out JsonElement fmt) &&
            double.TryParse(Str(fmt, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            duration = d;
        }

        return new SourceMedia
        {
            FilePath = filePath,
            Width = width,
            Height = height,
            VideoCodec = Str(v, "codec_name") ?? "",
            SampleAspect = sar,
            PixelFormat = Str(v, "pix_fmt") ?? "",
            ColorRange = Str(v, "color_range"),
            ColorSpace = Str(v, "color_space"),
            ColorPrimaries = Str(v, "color_primaries"),
            ColorTransfer = Str(v, "color_transfer"),
            ChromaLocation = Str(v, "chroma_location"),
            Profile = Str(v, "profile"),
            BitsPerRawSample = IntOrParsed(v, "bits_per_raw_sample"),
            FrameCount = IntOrParsed(v, "nb_frames"),
            RFrameRate = Str(v, "r_frame_rate"),
            AvgFrameRate = Str(v, "avg_frame_rate"),
            AudioCodec = audio is { } a ? Str(a, "codec_name") : null,
            AudioChannels = audio is { } ac ? IntOrParsed(ac, "channels") : null,
            DurationSeconds = duration,
            StartTimeSeconds = startTime,
            Streams = all,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;

    /// <summary>
    /// An integer ffprobe may emit either as a number or as a quoted string - it is inconsistent
    /// about nb_frames and bits_per_raw_sample in particular, across both versions and
    /// containers.
    /// </summary>
    private static int? IntOrParsed(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement p)) return null;

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetInt32(),
            JsonValueKind.String when int.TryParse(
                p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => null,
        };
    }
}

public sealed class InvalidMediaException : Exception
{
    public InvalidMediaException(string message) : base(message) { }
}
