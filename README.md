# SteamRec Utility

Turns Steam's 4:3 Counter-Strike recordings into stretched 16:9 clips **without
re-encoding them**, then optionally uploads them to YouTube.

If you play stretched — a 4:3 resolution scaled to fill a 16:9 monitor via the NVIDIA
Control Panel's "Full-screen" scaling — Steam records the game's native 4:3 framebuffer.
The stretch happens at display scanout, after capture, so it is missing from the file.
This tool puts it back.

## Why it is lossless

The stretch is an *aspect* change, not a pixel change. Tagging the container is enough:

```
ffmpeg -i in.mp4 -c copy -aspect 16:9 -tag:v hvc1 out.mp4
```

The encoded video is copied byte-for-byte. On a real 9-second clip that takes **0.034s**,
and the video payload hash is identical before and after. Every remux is verified this way:
if the video stream changed, the output is discarded rather than kept.

Re-encoding was measured for comparison, against a lossless reference:

| x265 preset slow | VMAF | size |
|---|---|---|
| CRF 16 | 80.0 | 7,959 KB |
| CRF 18 | 79.7 | 6,090 KB |
| CRF 20 | 79.3 | 4,602 KB |
| **stream copy** | **100** | *same as source* |

CRF 16 spends more bits than the source to gain 0.3 VMAF. Re-compressing already-compressed
high-motion footage is a losing trade at any bitrate, so this tool has no encoder at all.

What that buys you, beyond quality: audio keeps its original bitrate instead of being
silently re-encoded, every audio track survives, and full colour range (Steam records `pc`
range) and variable frame timing are preserved exactly.

## Requirements

- Windows x64, .NET 8
- `ffmpeg` and `ffprobe` on PATH, or passed with `--ffmpeg` / `--ffprobe`

## GUI

Pick an input folder and an output folder, tick the clips you want, press **Remux Selected**.
The preview shows each clip at its *display* aspect, so you see the stretched result before
committing.

Originals move to `<input>/processed/`. If YouTube upload is on, uploaded clips move to
`<output>/uploaded/` — they are kept, not deleted, so you can still play them locally.

## CLI

```
srec remux --in D:\rec --out D:\out [--aspect 16:9] [--keep-originals]
srec run   --in D:\rec --out D:\out --upload [--privacy unlisted]
srec upload --in D:\out [--privacy unlisted]
srec probe --in clip.mp4
srec fix-timelines --in D:\rec
```

`srec probe` reports what a file actually is:

```
video           hevc 1280x960 yuvj420p
sample aspect   1:1
display aspect  4:3
colour range    pc (full)
streams         2 (1 audio)
```

## YouTube

Templates accept `{game}`, `{clip}`, `{recording_date}`, `{recording_time}`, `{filename}`,
`{filename_ext}`, `{date}`, `{time}`, `{datetime}`, `{year}`, `{month}`, `{day}`.

Steam names clips like `CounterStrike_2__20260808_104557_PM__Double_kill.mp4`, so
`{game} - {clip}` gives *"CounterStrike 2 - Double kill"*.

OAuth setup is in [docs/YOUTUBE_SETUP.md](docs/YOUTUBE_SETUP.md). Credentials, tokens and
settings live in `%APPDATA%\SteamRecUtility`.

## Layout

```
src/SteamRecUtility.Core/    net8.0, no UI reference - the whole pipeline
src/SteamRecUtility.Cli/     srec
src/SteamRecUtility.Gui/     WinForms shell
tests/                       86 tests, no ffmpeg or GPU needed
```

Core targets `net8.0` rather than `net8.0-windows` deliberately: a WinForms reference is a
compile error there, not merely bad practice. Commands are built as argument *lists* and
passed to `ProcessStartInfo.ArgumentList`, so plans can be asserted on directly and paths
containing quotes or spaces need no escaping.

Encoder policy, and the defects this rewrite exists to remove, are documented in
`.claude/skills/video-encoding-policy/`.

## Building

```
dotnet test tests/SteamRecUtility.Core.Tests    # runs anywhere
dotnet build SteamRecUtility.sln                # the GUI needs Windows
```
