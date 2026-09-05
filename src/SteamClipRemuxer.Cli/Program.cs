using SteamClipRemuxer.Core.Configuration;
using SteamClipRemuxer.Core.Execution;
using SteamClipRemuxer.Core.Probing;
using SteamClipRemuxer.Core.Timelines;
using SteamClipRemuxer.Core.Youtube;

namespace SteamClipRemuxer.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            return await RunAsync(args, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        string command = args[0].ToLowerInvariant();
        Options options = Options.Parse(args.Skip(1));

        return command switch
        {
            "remux" => await RemuxAsync(options, upload: false, ct).ConfigureAwait(false),
            "run" => await RemuxAsync(options, upload: options.Upload, ct).ConfigureAwait(false),
            "upload" => await UploadOnlyAsync(options, ct).ConfigureAwait(false),
            "probe" => await ProbeAsync(options, ct).ConfigureAwait(false),
            "fix-timelines" => FixTimelines(options),
            _ => Unknown(command),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Try 'srec --help'.");
        return 2;
    }

    private static ConsoleLog NewLog() => new();

    private static (BatchService batch, RemuxService remux) BuildServices(IPipelineLog log, string ffmpeg, string ffprobe)
    {
        var runner = new ProcessRunner();
        var remux = new RemuxService(
            new MediaProbe(runner, ffprobe), runner, new VideoStreamHasher(runner, ffmpeg), log, ffmpeg);
        return (new BatchService(remux, log), remux);
    }

    private static async Task<int> RemuxAsync(Options o, bool upload, CancellationToken ct)
    {
        if (o.Input is null || o.Output is null)
        {
            Console.Error.WriteLine("error: --in and --out are required.");
            return 2;
        }

        AppSettings settings = AppSettings.Load(o.SettingsPath);
        settings.InputFolder = o.Input;
        settings.OutputFolder = o.Output;
        settings.MoveProcessedFiles = o.MoveProcessed ?? settings.MoveProcessedFiles;
        settings.EnableYouTubeUpload = upload;
        if (o.Aspect is not null) settings.TargetDisplayAspect = o.Aspect;
        if (o.Privacy is not null) settings.YouTubePrivacyStatus = o.Privacy;

        IReadOnlyList<string> files = BatchService.FindRecordings(o.Input);
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"No recordings found in '{o.Input}'.");
            return 1;
        }

        ConsoleLog log = NewLog();
        log.Info($"{files.Count} clip(s) in {o.Input}");
        log.Info($"target display aspect {settings.ParsedTargetAspect}, video stream copied (lossless)");
        log.Info("");

        YouTubeClient? youtube = null;
        if (upload)
        {
            youtube = new YouTubeClient();
            if (!await youtube.TryRestoreAsync(ct).ConfigureAwait(false))
            {
                Console.Error.WriteLine(
                    "error: not signed in to YouTube. Run the GUI once to authorise, or place "
                    + $"youtube_credentials.json in {AppPaths.DataDirectory}.");
                return 1;
            }
        }

        (BatchService batch, _) = BuildServices(log, o.Ffmpeg, o.Ffprobe);
        IReadOnlyList<ClipOutcome> outcomes =
            await batch.RunAsync(files, settings, youtube, progress: null, ct).ConfigureAwait(false);

        return outcomes.All(x => x.Succeeded) ? 0 : 1;
    }

    private static async Task<int> UploadOnlyAsync(Options o, CancellationToken ct)
    {
        if (o.Input is null)
        {
            Console.Error.WriteLine("error: --in is required.");
            return 2;
        }

        AppSettings settings = AppSettings.Load(o.SettingsPath);
        settings.OutputFolder = o.Input;
        settings.EnableYouTubeUpload = true;
        settings.MoveProcessedFiles = false;
        if (o.Privacy is not null) settings.YouTubePrivacyStatus = o.Privacy;

        var youtube = new YouTubeClient();
        if (!await youtube.TryRestoreAsync(ct).ConfigureAwait(false))
        {
            Console.Error.WriteLine("error: not signed in to YouTube.");
            return 1;
        }

        ConsoleLog log = NewLog();
        int failures = 0;

        foreach (string file in BatchService.FindRecordings(o.Input))
        {
            log.Info(Path.GetFileName(file));
            var request = new UploadRequest
            {
                FilePath = file,
                Title = TitleTemplate.Expand(settings.YouTubeTitleTemplate, file,
                    settings.YouTubeRemoveDateFromFilename, settings.YouTubeRemoveTextPatterns),
                Description = TitleTemplate.Expand(settings.YouTubeDescriptionTemplate, file,
                    settings.YouTubeRemoveDateFromFilename, settings.YouTubeRemoveTextPatterns),
                Tags = settings.ParsedTags,
                PrivacyStatus = settings.YouTubePrivacyStatus,
                CategoryId = settings.YouTubeCategoryId,
                MadeForKids = settings.YouTubeMadeForKids,
                AgeRestricted = settings.YouTubeAgeRestricted,
            };

            UploadResult result = await youtube.UploadAsync(request, null, ct).ConfigureAwait(false);
            if (result.Success) log.Success($"  {result.VideoUrl}");
            else { log.Error($"  {result.Error}"); failures++; }
        }

        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> ProbeAsync(Options o, CancellationToken ct)
    {
        if (o.Input is null)
        {
            Console.Error.WriteLine("error: --in <file> is required.");
            return 2;
        }

        var runner = new ProcessRunner();
        SourceMedia media = await new MediaProbe(runner, o.Ffprobe).ProbeAsync(o.Input, ct).ConfigureAwait(false);

        Console.WriteLine($"file            {Path.GetFileName(media.FilePath)}");
        Console.WriteLine($"video           {media.VideoCodec} {media.Width}x{media.Height} {media.PixelFormat}");
        Console.WriteLine($"sample aspect   {media.SampleAspect}");
        Console.WriteLine($"display aspect  {media.DisplayAspect}");
        Console.WriteLine($"colour range    {media.ColorRange ?? "unspecified"}{(media.IsFullRange ? " (full)" : "")}");
        Console.WriteLine($"streams         {media.Streams.Count} ({media.AudioStreamCount} audio)");
        Console.WriteLine($"duration        {media.DurationSeconds:0.000}s");
        return 0;
    }

    private static int FixTimelines(Options o)
    {
        if (o.Input is null)
        {
            Console.Error.WriteLine("error: --in is required.");
            return 2;
        }

        TimelineFixResult result = TimelineFixer.FixAll(o.Input, NewLog());
        return result.Errors == 0 ? 0 : 1;
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        srec - lossless remux and YouTube upload for Steam recordings

          srec remux --in <dir> --out <dir> [--aspect 16:9] [--move-processed|--keep-originals]
          srec run   --in <dir> --out <dir> --upload [--privacy unlisted]
          srec upload --in <dir> [--privacy unlisted]
          srec probe --in <file>
          srec fix-timelines --in <dir>

        Options
          --in <path>          input folder, or file for 'probe'
          --out <path>         output folder (must differ from the input folder)
          --aspect <N:D>       display aspect to tag, default 16:9
          --upload             upload after remuxing ('run' only)
          --privacy <status>   private | unlisted | public
          --move-processed     move originals into processed/ (default)
          --keep-originals     leave originals where they are
          --settings <path>    settings file to use
          --ffmpeg <path>      ffmpeg executable, default 'ffmpeg'
          --ffprobe <path>     ffprobe executable, default 'ffprobe'

        The video stream is always copied, never re-encoded. Every remux is verified by
        comparing the video payload hash before and after.
        """);
}

internal sealed record Options
{
    public string? Input { get; init; }
    public string? Output { get; init; }
    public string? Aspect { get; init; }
    public string? Privacy { get; init; }
    public string? SettingsPath { get; init; }
    public bool Upload { get; init; }
    public bool? MoveProcessed { get; init; }
    public string Ffmpeg { get; init; } = "ffmpeg";
    public string Ffprobe { get; init; } = "ffprobe";

    public static Options Parse(IEnumerable<string> args)
    {
        string? input = null, output = null, aspect = null, privacy = null, settings = null;
        string ffmpeg = "ffmpeg", ffprobe = "ffprobe";
        bool upload = false;
        bool? moveProcessed = null;

        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            string? Next() => i + 1 < list.Count ? list[++i] : null;

            switch (a)
            {
                case "--in": input = Next(); break;
                case "--out": output = Next(); break;
                case "--aspect": aspect = Next(); break;
                case "--privacy": privacy = Next(); break;
                case "--settings": settings = Next(); break;
                case "--ffmpeg": ffmpeg = Next() ?? ffmpeg; break;
                case "--ffprobe": ffprobe = Next() ?? ffprobe; break;
                case "--upload": upload = true; break;
                case "--move-processed": moveProcessed = true; break;
                case "--keep-originals": moveProcessed = false; break;
                default:
                    throw new ArgumentException($"Unknown option '{a}'.");
            }
        }

        return new Options
        {
            Input = input, Output = output, Aspect = aspect, Privacy = privacy,
            SettingsPath = settings, Upload = upload, MoveProcessed = moveProcessed,
            Ffmpeg = ffmpeg, Ffprobe = ffprobe,
        };
    }
}

internal sealed class ConsoleLog : IPipelineLog
{
    public void Write(LogLevel level, string message)
    {
        TextWriter w = level == LogLevel.Error ? Console.Error : Console.Out;
        ConsoleColor? colour = level switch
        {
            LogLevel.Success => ConsoleColor.Green,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            _ => null,
        };

        if (colour is null) { w.WriteLine(message); return; }

        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = colour.Value;
        w.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
