using SteamRecUtility.Core.Probing;

namespace SteamRecUtility.Core.Planning;

public sealed record RemuxOptions
{
    /// <summary>The display aspect the output should present at. Defaults to 16:9.</summary>
    public AspectRatio TargetDisplayAspect { get; init; } = AspectRatio.Widescreen;

    /// <summary>Rewrite the moov atom to the front so the file starts playing without a full read.</summary>
    public bool FastStart { get; init; } = true;
}

public sealed record RemuxPlan
{
    public required IReadOnlyList<string> Arguments { get; init; }
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>True when the plan changes the display aspect, i.e. applies the stretch.</summary>
    public required bool AspectOverridden { get; init; }

    /// <summary>The sample aspect the output will carry, once the override is applied.</summary>
    public required AspectRatio ResultingSampleAspect { get; init; }

    /// <summary>The display aspect the output will present at.</summary>
    public required AspectRatio ResultingDisplayAspect { get; init; }

    public string Describe() =>
        AspectOverridden
            ? $"{Path.GetFileName(InputPath)}: stretch to {ResultingDisplayAspect} "
              + $"via SAR {ResultingSampleAspect} (video copied)"
            : $"{Path.GetFileName(InputPath)}: already {ResultingDisplayAspect} (video copied)";
}

/// <summary>
/// Builds the remux command. Pure and total: given the same source description and options it
/// always produces the same argument list, which is what makes the whole pipeline testable
/// without FFmpeg, a GPU, or a window.
/// </summary>
public static class RemuxPlanner
{
    public static RemuxPlan Plan(SourceMedia source, string outputPath, RemuxOptions? options = null)
    {
        options ??= new RemuxOptions();

        AspectRatio currentDar = source.DisplayAspect;
        AspectRatio targetDar = options.TargetDisplayAspect;
        bool needsAspect = currentDar != targetDar;

        AspectRatio resultingSar = needsAspect
            ? AspectRatio.SarForTargetDisplay(source.Width, source.Height, targetDar)
            : source.SampleAspect;

        var args = new List<string> { "-y", "-i", source.FilePath };

        // Copy every stream untouched: video, all audio tracks, subtitles, metadata, chapters.
        // No encoder is ever selected, so the output video bitstream is identical to the input.
        args.AddRange(new[] { "-c", "copy", "-map", "0", "-map_metadata", "0", "-map_chapters", "0" });

        if (needsAspect)
        {
            args.Add("-aspect");
            args.Add($"{targetDar.Numerator}:{targetDar.Denominator}");
        }

        // Without this tag QuickTime and several browsers refuse to play HEVC in MP4.
        if (source.IsHevc && IsMp4(outputPath))
        {
            args.Add("-tag:v");
            args.Add("hvc1");
        }

        if (options.FastStart && IsMp4(outputPath))
        {
            args.Add("-movflags");
            args.Add("+faststart");
        }

        args.Add(outputPath);

        return new RemuxPlan
        {
            Arguments = args,
            InputPath = source.FilePath,
            OutputPath = outputPath,
            AspectOverridden = needsAspect,
            ResultingSampleAspect = resultingSar,
            ResultingDisplayAspect = needsAspect ? targetDar : currentDar,
        };
    }

    private static bool IsMp4(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase);
    }
}
