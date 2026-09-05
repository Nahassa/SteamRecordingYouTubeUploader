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
    public double DurationSeconds { get; init; }
    public required IReadOnlyList<MediaStream> Streams { get; init; }

    /// <summary>How the file actually displays, accounting for non-square pixels.</summary>
    public AspectRatio DisplayAspect => SampleAspect.DisplayAspectFor(Width, Height);

    public bool IsHevc => VideoCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase);

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
        foreach (JsonElement s in streams.EnumerateArray())
        {
            string type = Str(s, "codec_type") ?? "";
            string name = Str(s, "codec_name") ?? "";
            all.Add(new MediaStream(Int(s, "index") ?? all.Count, type, name));
            if (video is null && type == "video") video = s;
        }

        if (video is null)
            throw new InvalidMediaException($"'{filePath}' contains no video stream.");

        JsonElement v = video.Value;
        int width = Int(v, "width") ?? throw new InvalidMediaException($"'{filePath}' has no frame width.");
        int height = Int(v, "height") ?? throw new InvalidMediaException($"'{filePath}' has no frame height.");

        // ffprobe omits sample_aspect_ratio entirely for square pixels, and emits "0:1" when
        // it is unknown. Both mean square.
        AspectRatio sar = AspectRatio.TryParse(Str(v, "sample_aspect_ratio")) ?? AspectRatio.Square;

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
            DurationSeconds = duration,
            Streams = all,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
}

public sealed class InvalidMediaException : Exception
{
    public InvalidMediaException(string message) : base(message) { }
}
