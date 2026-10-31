# Working on this project

Notes for whoever picks this up next, including Claude. Everything here was learned by
getting it wrong at least once.

## Start from `main`

**Branch from `main`, and do not assume a named branch is current.** A session once arrived
with `claude/lossless-remux-repo-setup-3626im` configured as its development branch; it was
thirteen commits stale, built cleanly, and produced an application missing half its settings.
A day went into hunting a regression that did not exist.

If a branch is handed to you by configuration, check it against `main` before trusting it:

```bash
git fetch origin
git log --oneline origin/main..HEAD   # what you have that main does not
git log --oneline HEAD..origin/main   # what main has that you do not
```

The second command returning anything means the branch is behind. Rebase or start again.

## Build and test

```bash
dotnet build                                   # all four projects, 0 warnings expected
dotnet test tests/SteamClipRemuxer.Core.Tests  # 448 tests
```

`TreatWarningsAsErrors` is on for Core. A warning is a build failure, not a suggestion.

The WinForms GUI **compiles on Linux** — `EnableWindowsTargeting` in its csproj restores the
Windows targeting pack from NuGet — so `dotnet build` covers it on any host. It cannot be
*rendered* anywhere but Windows, so no layout or theming change has been seen before it ships.
Say so when handing one over rather than implying it was checked.

## The invariant: the file kept on disk is bit-identical to the recording

This is the whole point of the tool, and almost every design decision follows from it.

- Steam's DASH segments are byte-concatenated and muxed with `-c copy`. Never re-encode to
  fix geometry, aspect or colour — tag it instead.
- **Three seconds is the finest cut available.** Steam writes one keyframe per DASH chunk, so
  windows snap outward to chunk boundaries. Do not "improve" this with `-ss`: on the sample,
  `-ss 6 -t 3` wrote 360 packets for a 180-frame window and hid the excess behind an edit
  list, which the concat demuxer then discards.
- The **one** exception is the YouTube upscale. It re-encodes a *temporary* that is uploaded
  and deleted; `UploadPayload` keeps the upload path, the archive path and the temporary as
  three separate values precisely so they cannot be confused. `ArchivePath` is always the
  lossless file. `ArchiveInvariantTests` reads the bytes back off disk to prove it.

## Verify before trusting, and before destroying

Exit code 0 has never been evidence here.

- Copy paths verify by video payload md5 against the source.
- The re-encode path cannot, so it checks duration, frame count, display aspect, colour range,
  primaries, transfer, space, bit depth, audio codec and channels, stream count, and an SSIM
  floor. The floor is calibrated against measurements, documented on `MinimumSsim`.
- When FFmpeg warns that a result may be incorrect, treat the result as unmeasurable. Parsing
  past such a warning is what made two working encoders look broken.

## Architecture

`SteamClipRemuxer.Core` has **no UI reference and must never gain one** — the pipeline takes a
request object and reports through `IProgress<T>` and `IPipelineLog`. This is enforced by the
project structure, and it is what makes the UI replaceable.

FFmpeg commands are built as `IReadOnlyList<string>`, one token per element, by `static` pure
methods so they are covered by tests rather than discovered during a run. Never concatenate a
command string.

House rules for encoder flags, colour, scaling and capability probing live in the sibling repo:
`SteamRecordingUtility/.claude/skills/video-encoding-policy/`. Read it before touching any
FFmpeg argument builder. Its `references/anti-patterns.md` is a list of defects this codebase
has actually shipped.

## Releases

CI (`.github/workflows/ci.yml`) tests on Linux, builds the solution on Windows, and packages
self-contained single-file executables. Packaging runs only on a release, a push to `main`, or
a manual `workflow_dispatch` — building it on every commit once left a gigabyte of artifacts.

To cut one: push a tag, publish a GitHub release from it, and CI attaches both exes. A
`workflow_dispatch` run produces the same binaries as a build artifact instead, with a shorter
retention, which is the way to try a branch without releasing it.

## Things the container cannot do

No Windows, no NVIDIA GPU, no VMAF in the FFmpeg build. So the GUI is never seen here, the
NVENC path cannot run here, and quality is measured with SSIM and PSNR rather than VMAF. State
these plainly when reporting work rather than letting "tests pass" imply more than it does.

What has been confirmed on real hardware, on an RTX 5080, is worth not re-litigating. Both
upscale paths produce a correct 1080p file from 1280x960 source:

| | SSIM | time | footage |
| --- | --- | --- | --- |
| `libx265` | 0.9832 | 121s | 75s reel |
| `hevc_nvenc` | 0.9851 | 35s | 63s reel |

NVENC passed the capability probe with the full option set, including `-b_ref_mode middle` and
`-tune hq`, and tagged `pc` correctly rather than the `tv` it is notorious for — so the colour
range guard did not fire. zscale accepted `yuvj420p` directly, so the swscale fallback is still
untested. The archived file was verified on disk afterwards as `1280x960 yuvj420p pc`: the
upload is a temporary, the recording is untouched.

Different clips, so do not read those two SSIM figures against each other. What is real is that
NVENC is roughly three times faster at equivalent quality.
