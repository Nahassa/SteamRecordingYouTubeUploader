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

- Windows x64, .NET 10
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
configured in Steam excludes them with no tolerance needed. **Also list clips at or above the
threshold** brings them back when you want them, which is what the kill-highlights cut below is
for.

Clips already handled are listed greyed out rather than hidden, so Steam's clip list can be
left alone instead of deleting clips there to avoid uploading the same highlight twice.

## GUI

Pick an input folder and an output folder, tick the clips you want, press **Remux Selected**.
Ticking is on the checkbox only — clicking a clip's name selects it for preview without
changing what will be processed. The preview shows each clip at its *display* aspect, so you see
the stretched result before committing. For a Steam clip it uses the thumbnail Steam already
wrote, so it is instant.

The list has a column per status, ticked when it holds, and a filter for each above it:

| | |
|---|---|
| **R** | remuxed — an output file was written |
| **U** | uploaded — it reached YouTube |
| **P** | pending upload — written but not yet uploaded, the work an upload-only run picks up |
| **M** | output missing — written, still waiting to upload, and no longer where the log says |
| **H** | cut to highlights rather than kept whole |

Set a game's name under Settings → Game names, as `730 = CS2`, one per line. A clip folder
identifies its game only by app id, so without an entry an unrecognised game is called "App 440",
and anything set there beats the built-in name.

The **Clip** column shows the name the output will be given. Press F2, or right-click and choose
Rename, to give a clip a name of your own — it is used for the file *and* for the YouTube title,
and it survives restarts. Right-click and Reset Name puts the generated one back. Renaming applies
to Steam clips, where the name is generated; an exported file's output takes the input file's own
name.

Settings, Fix Timelines and Show Log are on the menu bar as well as on buttons — **File →
Settings…**, **Tools → Fix Timelines**, **View → Show Log** — so they stay reachable however
crowded the button row gets.

The window size and the divider between the list and the preview are remembered between
sessions.

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

### Kill highlights

Tick **Kill highlights only** and a clip is cut down to the fights in it, dropping everything
between them. Steam clips only — the kill times come out of the clip's timeline, and an exported
file does not have one, so the box is disabled for that source.

Alongside it: **Stop before deaths** ends a run before you get killed rather than carrying past it,
and **Min kills/round** keeps only rounds where that many of your kills are inside the clip. Both
are per run. How much footage to keep either side of a kill lives in Settings, at 3 seconds each by
default.

**Three seconds is the finest cut, and that is not a setting.** Steam writes its recording as
3-second DASH chunks with one keyframe at the start of each — measured at 4125.010, 4128.010 and
4131.011 on the sample clip, one I-frame per 180-frame GOP. A chunk can be kept or dropped whole as
a byte copy; cutting inside one would mean re-encoding. So each window snaps outward to chunk
boundaries.

Rounds are respected, because a clip is usually longer than a round: the competitive rounds
measured here run 97–106 seconds against a 120-second buffer, so a clip left at the full buffer
almost always spans a boundary. A run is clamped at the round it belongs to, and two runs either
side of a boundary are never merged — otherwise the round-end screen and the next round's buy time
end up in the middle of the reel.

Chapters name the round, and a round gets **one** line however many pieces it was cut into. A round
holding a double kill and a triple kill far enough apart to be cut separately used to publish two
timestamps for one round; it now reads `0:00  Round 19 - Ace`, because five kills in a round is what
Counter-Strike calls an ace. The weapon is kept only when every kill in the round used it. YouTube's
three-chapter minimum is judged on the lines actually printed, not on the pieces behind them.

Because this only ever sees the chunks inside the clip, it condenses a clip you saved; it cannot
mine a whole session. It also only knows what Steam wrote down — no video is analysed, so a kill
Steam did not log is a kill this does not find. In deathmatch there are no real rounds (one measured
session reports five "rounds" holding 117 and 302 kills), so the per-round threshold passes
everything there.

Untouched clips are where this pays off most, and they are exactly what the duration threshold
hides — so Settings has **Also list clips at or above the threshold** to bring them back.

### Naming

Settings carries six naming boxes under **Steam clips** — a file name and a YouTube title each for a
single clip, a compilation, and a highlights reel. All six default to what the code used to hardcode,
so an install nobody has touched names everything exactly as before.

| Placeholder | Means |
| --- | --- |
| `{game}` | the game, honouring the app-id overrides below |
| `{recording_date}` `{recording_time}` | when the clip was recorded, not when the batch ran |
| `{highlight}` `{highlight_full}` `{weapon}` `{kills}` | the clip's largest fight, ties broken by the earliest |
| `{map}` `{mode}` `{round}` | where and when, from the timeline |
| `{count}` | clips joined |
| `{fights}` | fights kept — a reel from three clips can hold seven |
| `{clip_name}` | the clip's own file name, so a reel follows the clip file name box |

`{highlight}`, `{weapon}`, `{map}` and `{round}` describe one clip, so they come out empty on anything
spanning several; the name closes up around them rather than leaving a `" - - "` behind. Renaming a
clip in the list still beats every box.

One of these was a bug rather than a preference: a reel cut from a single clip went to YouTube titled
*"Counter-Strike 2 - 1 clip compilation"*, because every joined output was titled through the
compilation template and a one-clip reel expanded `{count}` to 1. Reels have their own title now.

The cut selects chunks rather than asking FFmpeg to seek, and the difference is not cosmetic. On
the sample, `-ss 6 -t 3` wrote 360 packets for a 180-frame window and hid the excess behind an edit
list; the concat demuxer discards edit lists, so that skipped footage reappears in the reel with
colliding timestamps. Selecting chunk 3 writes 180 packets and shows 180 frames. Cutting a real
clip to two fights and joining them gives 360 packets, 360 frames, monotonic timestamps, and a
video payload md5 of `bbfeb9cb94f6236423a78e01992eed3d` — identical to the two chunks' payloads
concatenated.

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

960p cannot be forced, and uploading unstretched 4:3 does not help either: still 960 tall, still
720p, and pillarboxed. Reaching 1080 means resampling, and resampling means re-encoding — the one
thing this tool otherwise never does. So it is off by default and, when switched on, it applies to
the upload only.

### Upscaling for the upload

**Upscale for YouTube** in Settings takes `Off`, `1080p` or `1440p`. It was measured before it was
built:

| | SSIM | PSNR |
| --- | --- | --- |
| 720p rung, as YouTube builds it today | 0.8986 | 34.23 dB |
| 1080p rung after upscaling | 0.9312 | **36.60 dB** |
| 720p rung at the 1080p bitrate (control) | — | +0.78 dB |
| 1440p rung after upscaling | 0.9510 | 38.53 dB |

So +2.37 dB, of which only 0.78 dB is the extra bitrate — the rest is the rung. The Lanczos
resample itself measured 47.2 dB round-trip, near-transparent, which is why the resample is the
point and the encoder is not: the whole encoder field, SVT-AV1 through x264, spanned **0.19 dB
across a 30x range of encode times**. Hence one software default (`libx265 -preset fast -crf 18`,
10-bit) and one optional GPU path (`hevc_nvenc`, HEVC Main10 as well, so a 5080 and a 3080 emit the
same format and differ only in speed).

**The file kept on disk is still the lossless one.** The scaled copy is written to a scratch folder,
uploaded, and deleted; only the verbatim remux is filed into `uploaded/`. That is enforced in code
by keeping the upload path, the archive path and the temporary as three separate values, and it is
covered by a test that reads the bytes back off disk.

Before anything is uploaded the scaled copy is checked against the original: duration within 0.5s,
frame count equal, display aspect equal, colour range, primaries, transfer and space each equal, bit
depth not reduced, audio codec and channel count untouched, stream count unchanged, and SSIM at
least 0.98 after scaling back down. Anything off and the original is uploaded instead — a failed
upscale costs the upscale, never the upload.

Two hazards are handled explicitly. Steam records full range (`color_range=pc`), and `zscale` spells
that `full` while the output tag spells it `pc`; mixing the two vocabularies is how full-range
footage comes out tagged limited, crushing every black. And NVENC has a long history of writing `tv`
regardless of what it was handed — so a hardware result whose range does not match is rejected and
re-encoded in software rather than accepted. The GPU option is tested by actually encoding with the
exact flag set, not by grepping `ffmpeg -encoders`, which returns true on machines with no NVIDIA
card in them.

Upscaling applies to Steam clips uploaded through the GUI — single clips, compilations and
highlights reels alike. The CLI's exported-file path uploads verbatim.

OAuth setup is in [docs/YOUTUBE_SETUP.md](docs/YOUTUBE_SETUP.md). Credentials, tokens and
settings live in `%APPDATA%\SteamClipRemuxer`.

## Layout

```
src/SteamClipRemuxer.Core/    net10.0, no UI reference - the whole pipeline
src/SteamClipRemuxer.Cli/     sclip
src/SteamClipRemuxer.Gui/     WinForms shell
tests/                       320 tests, no ffmpeg or GPU needed
```

`Core/Steam/` reads what Steam writes beside a clip: `clip.pb` through a small protobuf
reader, since Valve publishes no schema, and the DASH segments. `session.mpd` cannot be given
to FFmpeg directly — its `Period@start` is measured from the start of the recording session
while `mediaPresentationDuration` is only the clip's length, so the demuxer computes a
nonsense period and stops after one segment. The segments are concatenated instead, which is
a byte copy.

Core targets `net10.0` rather than `net10.0-windows` deliberately: a WinForms reference is a
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
