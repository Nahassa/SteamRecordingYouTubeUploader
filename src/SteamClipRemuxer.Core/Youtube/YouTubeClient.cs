using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using SteamClipRemuxer.Core.Configuration;

namespace SteamClipRemuxer.Core.Youtube;

public sealed record UploadRequest
{
    public required string FilePath { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string[] Tags { get; init; } = Array.Empty<string>();
    public string PrivacyStatus { get; init; } = "private";
    public string CategoryId { get; init; } = "20";
    public bool MadeForKids { get; init; }
    public bool AgeRestricted { get; init; }
}

public sealed record UploadResult(bool Success, string? VideoId, string? VideoUrl, string? Error)
{
    public static UploadResult Ok(string id) =>
        new(true, id, $"https://www.youtube.com/watch?v={id}", null);

    public static UploadResult Failed(string error) => new(false, null, null, error);
}

/// <summary>
/// YouTube upload. Reports failures through its return value rather than a dialog, so a
/// failed upload cannot block a batch on a modal window.
/// </summary>
public sealed class YouTubeClient
{
    private static readonly string[] Scopes = { YouTubeService.Scope.YoutubeUpload };
    private const string ApplicationName = "Steam Clip Remuxer";

    private YouTubeService? _service;

    public bool IsAuthenticated => _service is not null;

    public static bool HasCredentialsFile => File.Exists(AppPaths.YouTubeCredentialsFile);

    /// <summary>Restores a saved session without prompting. Returns false if none is usable.</summary>
    public async Task<bool> TryRestoreAsync(CancellationToken ct = default)
    {
        try
        {
            if (!HasCredentialsFile || !Directory.Exists(AppPaths.YouTubeTokenStore)) return false;

            string[] tokens = Directory.GetFiles(
                AppPaths.YouTubeTokenStore, "Google.Apis.Auth.OAuth2.Responses.TokenResponse-*");
            if (tokens.Length == 0) return false;

            UserCredential credential = await AuthorizeAsync(ct).ConfigureAwait(false);

            if (credential.Token.IsStale && !await credential.RefreshTokenAsync(ct).ConfigureAwait(false))
                return false;

            _service = CreateService(credential);
            return true;
        }
        catch
        {
            // A stored session that will not load is not an error; the user can sign in again.
            return false;
        }
    }

    /// <summary>Signs in interactively, opening a browser if needed.</summary>
    public async Task<UploadResult> AuthenticateAsync(CancellationToken ct = default)
    {
        if (!HasCredentialsFile)
        {
            return UploadResult.Failed(
                $"No OAuth client file. Place 'youtube_credentials.json' in {AppPaths.DataDirectory}.");
        }

        try
        {
            _service = CreateService(await AuthorizeAsync(ct).ConfigureAwait(false));
            return new UploadResult(true, null, null, null);
        }
        catch (Exception ex)
        {
            return UploadResult.Failed($"Sign-in failed: {ex.Message}");
        }
    }

    public async Task<UploadResult> UploadAsync(
        UploadRequest request, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (_service is null) return UploadResult.Failed("Not signed in to YouTube.");
        if (!File.Exists(request.FilePath)) return UploadResult.Failed($"File not found: {request.FilePath}");

        try
        {
            var video = new Video
            {
                Snippet = new VideoSnippet
                {
                    Title = request.Title,
                    Description = request.Description,
                    Tags = request.Tags,
                    CategoryId = request.CategoryId,
                },
                Status = new VideoStatus
                {
                    PrivacyStatus = request.PrivacyStatus,
                    SelfDeclaredMadeForKids = request.MadeForKids,
                },
            };

            // The requested parts must include contentDetails, or the rating below is dropped
            // silently by the API - which is what happened previously.
            string parts = "snippet,status";
            if (request.AgeRestricted)
            {
                video.ContentDetails = new VideoContentDetails
                {
                    ContentRating = new ContentRating { YtRating = "ytAgeRestricted" },
                };
                parts += ",contentDetails";
            }

            await using var stream = new FileStream(
                request.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            VideosResource.InsertMediaUpload insert =
                _service.Videos.Insert(video, parts, stream, "video/*");

            long length = stream.Length;
            insert.ProgressChanged += p =>
            {
                switch (p.Status)
                {
                    case UploadStatus.Uploading when length > 0:
                        progress?.Report((int)(p.BytesSent * 100 / length));
                        break;
                    case UploadStatus.Completed:
                        progress?.Report(100);
                        break;
                }
            };

            IUploadProgress final = await insert.UploadAsync(ct).ConfigureAwait(false);

            if (final.Status != UploadStatus.Completed)
                return UploadResult.Failed(final.Exception?.Message ?? "Upload did not complete.");

            string? id = insert.ResponseBody?.Id;
            return id is null ? UploadResult.Failed("YouTube returned no video id.") : UploadResult.Ok(id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UploadResult.Failed($"Upload failed: {ex.Message}");
        }
    }

    private static async Task<UserCredential> AuthorizeAsync(CancellationToken ct)
    {
        AppPaths.EnsureCreated();
        await using var stream = new FileStream(
            AppPaths.YouTubeCredentialsFile, FileMode.Open, FileAccess.Read);

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            Scopes,
            "user",
            ct,
            new FileDataStore(AppPaths.YouTubeTokenStore, fullPath: true)).ConfigureAwait(false);
    }

    private static YouTubeService CreateService(UserCredential credential) =>
        new(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName,
        });
}
