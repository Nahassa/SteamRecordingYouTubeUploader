# GitHub Actions workflows

## ci.yml

Tests the core library and builds the Windows executables.

### Triggers

- Push to `main` or any `claude/**` branch
- Pull requests
- A published release
- Manual dispatch from the Actions tab

There are no path filters: every source file in the repository feeds one of the
jobs, so restricting the trigger would only ever skip a build that mattered.

### Jobs

Compiling and packaging are deliberately separate. Proving the code builds is
wanted on every push; producing a self-contained executable is not. Packaging is
the slow part of the run and leaves a ~105MB artifact behind, so ten
work-in-progress commits once produced a gigabyte of artifacts nobody asked for.

**`test` (ubuntu-latest)** restores and runs `tests/SteamClipRemuxer.Core.Tests`
in Release. `SteamClipRemuxer.Core` targets `net8.0` rather than `net8.0-windows`
and has no UI dependency, so its tests run anywhere and finish in seconds. This
job gates the Windows ones, which means a logic regression is reported without
waiting on a Windows runner and a WinForms compile.

The restore step names only the test project. MSBuild accepts one project per
invocation, and the test project pulls `SteamClipRemuxer.Core` in through its
`ProjectReference` anyway.

**`build` (windows-latest)** builds the whole solution and re-runs the tests
against that build. It produces no artifact. The Linux SDK omits the
WindowsDesktop targeting pack, so this is the only place the WinForms GUI is
compiled at all, which is why it runs on every push despite packaging nothing.

**`package` (windows-latest)** publishes both front ends as self-contained,
single-file, ReadyToRun `win-x64` executables:

| Project | Executable |
|---|---|
| `src/SteamClipRemuxer.Gui` | `SteamClipRemuxer-<version>-win-x64.exe` |
| `src/SteamClipRemuxer.Cli` | `sclip-<version>-win-x64.exe` |

It runs only when a binary is actually wanted:

| Event | Packages |
|---|---|
| Push to a `claude/**` branch | no |
| Pull request | no |
| Push to `main` | yes |
| Published release | yes, and attaches the assets |
| Manual dispatch | yes, on whichever branch you pick |

`<version>` comes from the tag on a `v*` tag build (`v1.0.0` → `1.0.0`), and
from the first seven characters of the commit SHA otherwise.

The two publishes write to separate folders. Two self-contained single-file
publishes sharing an output directory overwrite each other's runtime files.

### Building a branch before merging it

**Actions** → **CI** → **Run workflow** → pick the branch. `workflow_dispatch`
is not restricted to `main`, so this is how a change gets tried out as a real
executable before it is merged. Those artifacts expire after 7 days rather than
30, since they exist to be tested rather than kept.

The Run workflow button reads the workflow file on the default branch, so this
is only available once these changes are on `main`.

### Concurrency

Superseded runs on a feature branch or pull request are cancelled: pushing three
commits in a row should not occupy a Windows runner three times. Runs on `main`,
on a release and on a manual trigger produce something, so those always finish.

### Getting the executables

From a run: **Actions** → the run → **Artifacts** → `SteamClipRemuxer-<version>`.

From a release: publishing a release uploads both executables to it as assets,
using the runner's own `gh` and the automatic `GITHUB_TOKEN`.

### Permissions

The workflow is `contents: read` by default. Only `package` takes
`contents: write`, because it uploads release assets; nothing else writes.

## cleanup-artifacts.yml

Manual-dispatch only. Deletes build artifacts older than a day, keeping the five
most recent. Artifacts already expire after 30 days on their own, so this is for
reclaiming storage sooner rather than something that needs to run on a schedule.
