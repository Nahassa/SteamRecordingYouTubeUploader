# Steam Clip Remuxer

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

## Two sources

**Source** on the main window chooses where clips come from.

**Exported video files** are the files Steam writes when you use its Export Clip button.
Nothing links an export back to the recording it came from — the name Steam suggests is the
moment you pressed Save, 25 minutes adrift from the clip's contents on one measured sample —
so a title can only be built from the filename.

**Steam clips** reads Steam's own clip folders under `<recording folder>/clips` and replaces
the export step entirely: the clip goes straight from what Steam recorded to a finished file.
Each folder carries a `clip.pb` locating it in the session timeline, which is what makes a
real title possible.

|                        | Exported files | Steam clips |
|---|---|---|
| Needs exporting first  | yes | no |
| Knows map, mode, round | no | yes |
| Titles from            | the filename | what actually happened |

Point **Input** at your Steam recording folder, or at its `clips` subfolder; either works.

Clips still at the full recording buffer are skipped, because an untouched clip spans whole
rounds and has no single moment worth uploading. **Include clips shorter than** is that cut:
only clips shorter than it are processed, and a clip of exactly that length is left out. Steam
writes an untouched clip at *exactly* the buffer length, so setting this to the buffer length
configured in Steam excludes them with no tolerance needed.

Clips already handled are listed greyed out rather than hidden, so Steam's clip list can be
left alone instead of deleting clips there to avoid uploading the same highlight twice.

## GUI

Pick an input folder and an output folder, tick the clips you want, press **Remux Selected**.
The preview shows each clip at its *display* aspect, so you see the stretched result before
committing. For a Steam clip it uses the thumbnail Steam already wrote, so it is instant.

Originals move to `<input>/processed/`. If YouTube upload is on, uploaded clips move to
`<output>/uploaded/` — they are kept, not deleted, so you can still play them locally.
Steam's own clip folders are only ever read; nothing is written back into them.

### Stitching clips together

Tick **Stitch into one video** and the checked clips become a single compilation instead of one
file each. It works from either source: Steam clips are remuxed into parts first, exported files
go straight in. Clips are ordered oldest first, and the description gets a timestamp per clip —
which YouTube shows as chapters when there are at least three and each runs 10 seconds or more.

The join is still lossless. The DASH chunks cannot simply be concatenated across clips — each
clip's init segment carries its own decoder configuration — so FFmpeg's concat demuxer does it
with `-c copy`, and the result is checked byte for byte: the compilation's video payload must
equal the parts' payloads concatenated, or the output is discarded. Parts that do not match the
first clip's codec, size, pixel format, pixel aspect, colour or audio track count are left out
rather than re-encoded to fit, and the log says which and why.

Every clip that goes into a compilation is recorded as remuxed, and as uploaded once the
compilation reaches YouTube, so it greys out and will not be swept into a second one. To upload
one of them on its own afterwards, remove its entry from the processed-clip log.

The toggle is deliberately not saved: it resets each time the app starts.

## CLI

```
sclip remux --in D:\rec --out D:\out [--aspect 16:9] [--keep-originals]
sclip run   --in D:\rec --out D:\out --upload [--privacy unlisted]
sclip upload --in D:\out [--privacy unlisted]
sclip probe --in clip.mp4
sclip fix-timelines --in D:\rec
```

`sclip probe` reports what a file actually is:

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

Reading Steam's clip folders adds `{highlight}`, `{highlight_full}`, `{weapon}`, `{map}`,
`{mode}`, `{round}` and `{kills}`, so `{game} - {highlight_full}` gives
*"Counter-Strike 2 - Double kill with the AK-47"*. No player name ever reaches a title:
Steam writes them into every event description, and they are dropped when the highlight is
worked out rather than filtered afterwards.

Steam's own multi-kill labels are not trusted, because measurement showed them wrong in both
directions — a triple reported as two separate events, and, in deathmatch, 45 of 74 labelled
multi-kills naming the same victim twice. Kills are recounted from the individual events.

The clip's recorded moment is sent as the video's **recording date**, so the default title
carries no timestamp at all rather than spending characters on one.

A clip folder names its game only by Steam app id. Counter-Strike 2 is built in; anything
else is named under **Game names** in settings, as `app id = name` lines.

### Why YouTube shows 720p

A stretched clip is 1280x960 stored with a 4:3 sample aspect, which displays as 1707x960.
YouTube normalises to its own ladder — 144, 240, 360, 480, 720, 1080 — and 960 is not on it.
It will not upscale, so 720p is the tallest rendition it can build.

This is not fixable by any setting here, and 960p cannot be forced. Reaching 1080p would mean
resampling, and resampling means re-encoding, which is the one thing this tool exists not to
do. Uploading unstretched 4:3 does not help either: still 960 tall, still 720p, and
pillarboxed. The deliberate choice is to keep every file a verbatim copy and let YouTube do
the downscale.

OAuth setup is in [docs/YOUTUBE_SETUP.md](docs/YOUTUBE_SETUP.md). Credentials, tokens and
settings live in `%APPDATA%\SteamClipRemuxer`.

## Layout

```
src/SteamClipRemuxer.Core/    net8.0, no UI reference - the whole pipeline
src/SteamClipRemuxer.Cli/     sclip
src/SteamClipRemuxer.Gui/     WinForms shell
tests/                       247 tests, no ffmpeg or GPU needed
```

`Core/Steam/` reads what Steam writes beside a clip: `clip.pb` through a small protobuf
reader, since Valve publishes no schema, and the DASH segments. `session.mpd` cannot be given
to FFmpeg directly — its `Period@start` is measured from the start of the recording session
while `mediaPresentationDuration` is only the clip's length, so the demuxer computes a
nonsense period and stops after one segment. The segments are concatenated instead, which is
a byte copy.

Core targets `net8.0` rather than `net8.0-windows` deliberately: a WinForms reference is a
compile error there, not merely bad practice. Commands are built as argument *lists* and
passed to `ProcessStartInfo.ArgumentList`, so plans can be asserted on directly and paths
containing quotes or spaces need no escaping.

Encoder policy, and the defects this rewrite exists to remove, are documented in
`.claude/skills/video-encoding-policy/`.

## Building

```
dotnet test tests/SteamClipRemuxer.Core.Tests    # runs anywhere
dotnet build SteamClipRemuxer.sln                # the GUI needs Windows
```
