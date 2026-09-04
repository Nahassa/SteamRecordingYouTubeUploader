# GitHub Actions Workflows

This directory contains automated workflows for the Steam Recording Video Converter project.

## build-app.yml

Automatically builds the Steam Recording Utility executable for Windows x64.

### When It Runs

The workflow triggers on:

1. **Push to branches**:
   - `main` branch
   - Any `claude/**` branch
   - Only when changes are made to `SteamRecUtility/` or the workflow file itself

2. **Pull requests**:
   - When PRs modify `SteamRecUtility/`

3. **Releases**:
   - When a GitHub release is created or published

4. **Manual trigger**:
   - Can be manually triggered from the Actions tab

### What It Does

1. **Checks out code** from the repository
2. **Sets up .NET 8.0** SDK
3. **Restores dependencies** (Newtonsoft.Json)
4. **Builds the executable**:
   - Target: Windows x64
   - Self-contained (includes .NET runtime)
   - Single file executable
   - Ready-to-run compilation for faster startup
5. **Gets version**:
   - From git tag if available (e.g., `v1.0.0` → `1.0.0`)
   - From commit SHA if no tag (first 7 characters)
6. **Renames executable** with version: `SteamRecUtility-1.0.0-win-x64.exe`
7. **Uploads as artifact**:
   - Available for download from the Actions run page
   - Retained for 30 days
8. **Attaches to release** (only for release events):
   - Automatically uploads the executable to the GitHub release

### Accessing Built Executables

#### From Actions Run:
1. Go to the "Actions" tab in your repository
2. Click on a workflow run
3. Scroll down to "Artifacts"
4. Download `SteamRecUtility-[version]`

#### From Releases:
- When you create a release, the executable is automatically attached as an asset

### Manual Triggering

To manually build the executable:

1. Go to "Actions" tab
2. Select "Build SteamRec Utility" workflow
3. Click "Run workflow"
4. Select the branch
5. Click "Run workflow" button

The built executable will be available as an artifact.

### Requirements

No setup required - GitHub Actions provides everything needed:
- Windows runner
- .NET SDK
- PowerShell

### Notes

- Build time is typically 2-5 minutes
- The executable is fully portable (self-contained)
- No secrets or tokens needed (except for release uploads, which use the automatic `GITHUB_TOKEN`)
