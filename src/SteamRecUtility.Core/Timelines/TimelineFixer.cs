using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SteamRecUtility.Core.Execution;

namespace SteamRecUtility.Core.Timelines;

public sealed class TimelineFixResult
{
    public int Fixed { get; set; }
    public int AlreadyValid { get; set; }
    public int Errors { get; set; }
}

/// <summary>
/// Repairs Steam timeline JSON where "entries" was written as an object keyed by index rather
/// than as an array. Unrelated to the video pipeline; kept because Steam still produces these.
/// </summary>
public static class TimelineFixer
{
    public static IReadOnlyList<string> FindTimelineFiles(string inputFolder)
    {
        var files = new List<string>();

        string timelines = Path.Combine(inputFolder, "timelines");
        if (Directory.Exists(timelines)) files.AddRange(Directory.GetFiles(timelines, "*.json"));

        string clips = Path.Combine(inputFolder, "clips");
        if (Directory.Exists(clips))
        {
            foreach (string clipDir in Directory.GetDirectories(clips))
            {
                string clipTimelines = Path.Combine(clipDir, "timelines");
                if (Directory.Exists(clipTimelines)) files.AddRange(Directory.GetFiles(clipTimelines, "*.json"));
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>
    /// Orders the object's keys the way Steam meant them. Keys are indices, so an ordinal sort
    /// yields 0, 1, 10, 11, 2 - silently scrambling the timeline. Numeric keys are compared
    /// numerically; anything else falls back to ordinal.
    /// </summary>
    public static IReadOnlyList<string> OrderEntryKeys(IEnumerable<string> keys)
    {
        List<string> list = keys.ToList();

        bool allNumeric = list.Count > 0 && list.All(
            k => long.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));

        if (allNumeric)
        {
            return list
                .OrderBy(k => long.Parse(k, NumberStyles.Integer, CultureInfo.InvariantCulture))
                .ToList();
        }

        return list.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Rewrites one file if its "entries" is an object. Returns true when a fix was applied.
    /// The original is copied to ".bak" first.
    /// </summary>
    public static bool FixFile(string filePath)
    {
        JsonNode? root = JsonNode.Parse(File.ReadAllText(filePath));
        if (root is not JsonObject obj) return false;

        if (!obj.TryGetPropertyValue("entries", out JsonNode? entries)) return false;
        if (entries is not JsonObject entriesObj) return false;

        File.Copy(filePath, filePath + ".bak", overwrite: true);

        var ordered = new JsonArray();
        foreach (string key in OrderEntryKeys(entriesObj.Select(kv => kv.Key)))
        {
            JsonNode? value = entriesObj[key];
            ordered.Add(value?.DeepClone());
        }

        obj["entries"] = ordered;
        File.WriteAllText(filePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    public static TimelineFixResult FixAll(string inputFolder, IPipelineLog? log = null)
    {
        log ??= NullPipelineLog.Instance;
        var result = new TimelineFixResult();

        IReadOnlyList<string> files = FindTimelineFiles(inputFolder);
        if (files.Count == 0)
        {
            log.Warning("No timeline JSON files found.");
            return result;
        }

        log.Info($"Checking {files.Count} timeline file(s)");

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            try
            {
                if (FixFile(file))
                {
                    log.Success($"  {name}: entries converted to an array (original kept as .bak)");
                    result.Fixed++;
                }
                else
                {
                    log.Info($"  {name}: already valid");
                    result.AlreadyValid++;
                }
            }
            catch (Exception ex)
            {
                log.Error($"  {name}: {ex.Message}");
                result.Errors++;
            }
        }

        return result;
    }
}
