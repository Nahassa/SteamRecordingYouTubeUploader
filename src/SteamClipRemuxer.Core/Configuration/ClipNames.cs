using System.Text.Json;

namespace SteamClipRemuxer.Core.Configuration;

/// <summary>
/// The names the user has given clips, keyed by the clip's content identity.
///
/// A generated name says what happened - "Counter-Strike 2 - Double kill with the AK-47" - which
/// is often right and sometimes not what you want on YouTube. A name set here wins, and it wins
/// for both the file and the upload title, because renaming the file and still uploading under
/// the generated title would be the more surprising of the two.
///
/// Kept apart from the processed-clip log: naming a clip says nothing about having handled it,
/// and a rename has to survive on a clip that has never been run.
/// </summary>
public sealed class ClipNames
{
    private readonly Dictionary<string, string> _names;

    public ClipNames() : this(new Dictionary<string, string>()) { }

    public ClipNames(IDictionary<string, string> names) =>
        _names = new Dictionary<string, string>(names, StringComparer.Ordinal);

    public int Count => _names.Count;

    /// <summary>The name set for a clip, or null when it still uses the generated one.</summary>
    public string? For(string id) =>
        _names.TryGetValue(id, out string? name) && name.Length > 0 ? name : null;

    /// <summary>
    /// Sets a name, or clears it when the text is blank. Returns the name actually stored, so the
    /// caller can show what was kept rather than what was typed.
    /// </summary>
    public string? Set(string id, string? name)
    {
        string cleaned = Highlights.ClipNaming.Sanitise(name ?? string.Empty);

        // Sanitise never returns empty - it falls back to "Clip" - so a blank entry has to be
        // caught before it, or clearing a name would silently rename the clip to "Clip".
        if (string.IsNullOrWhiteSpace(name))
        {
            _names.Remove(id);
            return null;
        }

        _names[id] = cleaned;
        return cleaned;
    }

    public bool Remove(string id) => _names.Remove(id);

    public IReadOnlyDictionary<string, string> Entries => _names;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Loads the names, falling back to none. A name that cannot be read is not worth refusing to run over.</summary>
    public static ClipNames Load(string? path = null, Action<string>? onError = null)
    {
        path ??= AppPaths.ClipNamesFile;

        try
        {
            if (!File.Exists(path)) return new ClipNames();

            Dictionary<string, string>? names =
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));

            return new ClipNames(names ?? new Dictionary<string, string>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            onError?.Invoke($"Could not read the clip names ({ex.Message}); using the generated ones.");
            return new ClipNames();
        }
    }

    /// <summary>Writes through a temporary file and renames over the original, as the processed log does.</summary>
    public void Save(string? path = null, Action<string>? onError = null)
    {
        path ??= AppPaths.ClipNamesFile;

        try
        {
            AppPaths.EnsureCreated();

            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_names, Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onError?.Invoke($"Could not save the clip names: {ex.Message}");
        }
    }
}
