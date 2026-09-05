# YouTube Upload Setup Guide

This guide explains how to set up YouTube upload functionality in the Steam Clip Remuxer.

## Prerequisites

- Google Account
- Access to Google Cloud Console
- Steam Clip Remuxer installed

## Step 1: Create a Google Cloud Project

1. Go to [Google Cloud Console](https://console.cloud.google.com)
2. Click "Select a project" → "New Project"
3. Enter a project name (e.g., "Steam Clip Remuxer")
4. Click "Create"

## Step 2: Enable YouTube Data API v3

1. In your project, go to "APIs & Services" → "Library"
2. Search for "YouTube Data API v3"
3. Click on it and press "Enable"

## Step 3: Create OAuth 2.0 Credentials

1. Go to "APIs & Services" → "Credentials"
2. Click "Create Credentials" → "OAuth client ID"
3. If prompted, configure the OAuth consent screen:
   - User Type: External
   - App name: "Steam Clip Remuxer" (or your choice)
   - User support email: Your email
   - Developer contact: Your email
   - Click "Save and Continue"
   - Scopes: Skip this step (click "Save and Continue")
   - Test users: Add your Google account email
   - Click "Save and Continue"
4. Back to "Create OAuth client ID":
   - Application type: "Desktop app"
   - Name: "Steam Clip Remuxer Desktop"
   - Click "Create"
5. Download the JSON file (click the download button)

## Step 4: Install the Credentials File

1. Rename the downloaded JSON file to exactly: `youtube_credentials.json`
2. Place it in `%APPDATA%\SteamClipRemuxer` (paste that into the Explorer address
   bar). The app creates the folder on first run.

Not beside the .exe: the app is published as a single file, and installed under
Program Files it cannot write next to itself without elevation.

```
%APPDATA%\SteamClipRemuxer\
├── youtube_credentials.json  ← Place here
├── settings.json             ← written by the app
└── youtube_token\            ← written after you sign in
```

## Step 5: Authenticate in the App

1. Launch SteamClipRemuxer.exe
2. Click "Settings", tick "Upload to YouTube after remuxing", click OK
3. Click "Remux Selected". There is no separate sign-in button: the app
   authenticates on the first remux after upload is enabled
4. Your browser will open asking you to sign in with Google
5. Select your Google account
6. You'll see a warning "Google hasn't verified this app" (this is normal for personal projects):
   - Click "Advanced"
   - Click "Go to [Your App Name] (unsafe)"
7. Grant the requested permissions (YouTube upload access)
8. You'll see "Authentication successful!" in the app

## Step 6: Configure Upload Settings

### Title Template

Use placeholders to automatically generate video titles:
- `{filename}` - File name without extension
- `{filename_ext}` - File name with extension
- `{game}` - Game name parsed from the recording's file name
- `{clip}` - Clip label parsed from the recording's file name
- `{recording_date}` - Date the clip was recorded
- `{recording_time}` - Time the clip was recorded
- `{date}` - Current date (YYYY-MM-DD)
- `{time}` - Current time (HH:mm:ss)
- `{datetime}` - Full datetime
- `{year}`, `{month}`, `{day}` - Individual date components

Examples:
- `{filename}` → "2025-11-16 d2 retake 3k"
- `CS2 Gameplay - {filename}` → "CS2 Gameplay - 2025-11-16 d2 retake 3k"
- `{date} - {filename}` → "2025-11-16 - 2025-11-16 d2 retake 3k"

### Description Template

Same placeholders as title. Supports multi-line text.

Example:
```
Gameplay recorded on {date}

Original file: {filename_ext}
Converted with Steam Clip Remuxer
```

### Tags

Comma-separated keywords for YouTube search/discovery.

Examples:
- `gaming,gameplay,cs2,counter-strike`
- `highlights,frag,movie`

### Privacy Status

- **private** - Only you can see it (default, recommended for testing)
- **unlisted** - Anyone with the link can see it
- **public** - Everyone can find and watch it

### Category

Select the YouTube category that best fits your content:
- Gaming (20) - For game recordings
- Entertainment (24)
- People & Blogs (22)
- Music (10)
- Science & Technology (28)

## Troubleshooting

### "No OAuth client file"
- Make sure `youtube_credentials.json` is in `%APPDATA%\SteamClipRemuxer`
- Check the filename is exactly `youtube_credentials.json`
- The message names the folder it looked in; open that path directly

### "Google hasn't verified this app"
- This is normal for personal projects
- Click "Advanced" → "Go to [App Name] (unsafe)"
- This only happens during initial setup

### "Access Not Configured"
- Make sure you enabled "YouTube Data API v3" in Google Cloud Console
- Wait a few minutes after enabling the API

### "Quota Exceeded"
- Free tier allows 10,000 quota units per day
- Each video upload costs ~1600 units (about 6 videos per day)
- Quota resets at midnight Pacific Time
- To increase: Apply for quota increase in Google Cloud Console

### "Upload Failed"
- Check your internet connection
- Verify YouTube channel is properly set up
- Check video file isn't corrupted
- Review quota limits

## Quota Management

YouTube Data API has daily quotas:
- **Free tier**: 10,000 units/day
- **Video upload**: ~1600 units per video
- **Maximum uploads/day**: ~6 videos

To check your quota usage:
1. Go to Google Cloud Console
2. Navigate to "APIs & Services" → "Dashboard"
3. Click "YouTube Data API v3"
4. View "Quotas" tab

## Security Notes

- Keep `youtube_credentials.json` private (don't share it)
- The app stores authentication tokens in the `youtube_token` folder beside it
- Both live in `%APPDATA%\SteamClipRemuxer`, outside the repository
- You can revoke access anytime in [Google Account Settings](https://myaccount.google.com/permissions)

## Template Examples

### For Gaming Highlights
```
Title: {filename} - Highlight Reel
Description: Epic gaming moment from {date}

Tags: gaming,highlights,gameplay
Privacy: unlisted
```

### For Tutorial Videos
```
Title: How to: {filename}
Description: Tutorial video created on {datetime}

Step-by-step guide for {filename}

Tags: tutorial,howto,guide
Privacy: public
```

### For Personal Archives
```
Title: {filename}
Description: Recorded: {datetime}
Original file: {filename_ext}

Tags: archive,gameplay,recording
Privacy: private
```

## Additional Resources

- [YouTube Data API Documentation](https://developers.google.com/youtube/v3)
- [Google Cloud Console](https://console.cloud.google.com)
- [OAuth 2.0 Setup Guide](https://developers.google.com/youtube/v3/guides/auth/installed-apps)
