namespace SteamRecUtility.Core.Files;

/// <summary>
/// Files originals and finished clips into their subfolders. Moves are non-destructive: an
/// existing file at the destination is never overwritten, it is given a suffix instead.
/// </summary>
public static class FileOrganizer
{
    public const string ProcessedFolderName = "processed";
    public const string UploadedFolderName = "uploaded";

    /// <summary>Moves a source recording into "processed" beside it, once its output is safely written.</summary>
    public static string MoveToProcessed(string sourcePath, string inputFolder) =>
        MoveInto(sourcePath, Path.Combine(inputFolder, ProcessedFolderName));

    /// <summary>Moves a finished clip into "uploaded" after a successful upload. The file is kept, not deleted.</summary>
    public static string MoveToUploaded(string outputPath, string outputFolder) =>
        MoveInto(outputPath, Path.Combine(outputFolder, UploadedFolderName));

    public static string MoveInto(string sourcePath, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);
        string target = UniqueDestination(destinationFolder, Path.GetFileName(sourcePath));
        File.Move(sourcePath, target);
        return target;
    }

    /// <summary>
    /// A free path in the folder, appending " (2)", " (3)" and so on rather than clobbering an
    /// existing file. Steam names clips with timestamps so collisions are rare, but a silent
    /// overwrite here would destroy a recording.
    /// </summary>
    public static string UniqueDestination(string folder, string fileName)
    {
        string candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);

        for (int i = 2; i < int.MaxValue; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        throw new IOException($"Could not find a free filename for '{fileName}' in '{folder}'.");
    }
}
