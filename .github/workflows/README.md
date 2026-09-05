# GitHub Actions workflows

## ci.yml

Tests the core library and builds the Windows executables.

### Triggers

- Push to `main` or any `claude/**` branch
- Pull requests
- A published release
- Manual dispatch from the Actions tab

There are no path filters: every source file in the repository feeds one of the
two jobs, so restricting the trigger would only ever skip a build that mattered.

### Jobs

**`test` (ubuntu-latest)** restores and runs `tests/SteamRecUtility.Core.Tests`
in Release. `SteamRecUtility.Core` targets `net8.0` rather than `net8.0-windows`
and has no UI dependency, so its tests run anywhere and finish in seconds. This
job gates the Windows one, which means a logic regression is reported without
waiting on a Windows runner and a WinForms compile.

The restore step names only the test project. MSBuild accepts one project per
invocation, and the test project pulls `SteamRecUtility.Core` in through its
`ProjectReference` anyway.

**`build` (windows-latest)** builds the whole solution including the WinForms
GUI, re-runs the tests against that build, then publishes both front ends as
self-contained, single-file, ReadyToRun `win-x64` executables:

| Project | Executable |
|---|---|
| `src/SteamRecUtility.Gui` | `SteamRecUtility-<version>-win-x64.exe` |
| `src/SteamRecUtility.Cli` | `srec-<version>-win-x64.exe` |

`<version>` comes from the tag on a `v*` tag build (`v1.0.0` → `1.0.0`), and
from the first seven characters of the commit SHA otherwise.

The two publishes write to separate folders. Two self-contained single-file
publishes sharing an output directory overwrite each other's runtime files.

### Getting the executables

From a run: **Actions** → the run → **Artifacts** → `SteamRecUtility-<version>`,
kept for 30 days.

From a release: publishing a release uploads both executables to it as assets,
using the runner's own `gh` and the automatic `GITHUB_TOKEN`.

### Permissions

The workflow is `contents: read` by default. The `build` job takes
`contents: write` because it uploads release assets; nothing else writes.

## cleanup-artifacts.yml

Manual-dispatch only. Deletes build artifacts older than a day, keeping the five
most recent. Artifacts already expire after 30 days on their own, so this is for
reclaiming storage sooner rather than something that needs to run on a schedule.
