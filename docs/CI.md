# Continuous integration (GitHub Actions)

Audiora has one workflow, **CI** (`.github/workflows/ci.yml`). It builds the whole solution, runs every automated
test and builds the Windows installer with the same `build.ps1` used on the PC. It never publishes a release, never
creates tags, never pushes, and needs no secrets: its token can only read the repository.

## When it runs

| Event | What runs |
|---|---|
| Push to `main` | The whole workflow, on that commit. |
| Pull request (any branch) | The whole workflow, on the result of merging the pull request. |
| Manual | **Actions → CI → Run workflow**, then choose a branch. This button only appears once `ci.yml` is on `main`. |

If you push again to the same branch or pull request while a run is still going, the older run is cancelled.
Standard GitHub-hosted runners are free for public repositories.

## What it does

**Job 1: Build and test** (Windows, about 5 minutes)

1. Installs the .NET 8 SDK and pins it with a temporary `global.json`. That file exists only on the runner and is not
   committed. The runner also has newer SDKs, and this keeps the build on the same 8.0 SDK as local builds.
2. `dotnet restore` and `dotnet build SonosFlow.sln -c Release`. This covers the app, Core, the backup engine and its
   CLI, the installer project and CaptureCheck.
3. `dotnet test SonosFlow.sln -c Release`: the App, Core and Backup test projects. A test that hangs for 10 minutes is
   stopped, and a dump is saved with the results.
4. Writes a **summary** on the run page (see below) and uploads the raw results as the **test-results** artifact
   (`.trx` files, kept 14 days).

**Job 2: Windows installer** (runs only if job 1 passed, about 3 minutes)

1. Runs `build.ps1` unchanged: it publishes the self-contained win-x64 app, packs it and builds `SonosFlow-Setup.exe`.
2. `.github/scripts/Write-BuildSummary.ps1` **fails the job** if a file is missing or if the installer does not embed
   exactly the `payload.zip` built in this run. That would mean an installer carrying an older app.
3. Uploads two artifacts, kept 14 days:
   - **Audiora-Setup-win-x64**: `SonosFlow-Setup.exe`.
   - **Audiora-portable-win-x64**: the app folder; open `SonosFlow.exe`.

   GitHub delivers each artifact as a `.zip`. These are unsigned test builds, not releases.

## How to tell whether it worked

- **Next to each commit and pull request:** a green ✓ means both jobs passed, a red ✗ means something failed, and a
  yellow dot means the run is still going. On a pull request, the **Checks** section lists **CI / Build and test** and
  **CI / Windows installer**.
- **README badge:** the badge at the top of `README.md` shows the result of the latest run on `main`.
- **Run page** (**Actions** tab → the run → **Summary**):
  - **Test results:** the passed, failed and skipped tests for each project. Failed tests are listed with the first
    line of their message.
  - **Returned early without checking anything:** tests that logged `OMITIDA` because something they need is missing
    on the runner (see the next section). They count as passed, so this list tells you what a green run did *not*
    check.
  - **Windows installer:** the app and installer versions (`1.0.0-beta.2+<commit>`), the size and the SHA-256 of the
    installer.
  - **Artifacts** (bottom of the page): the downloads.
- **Email:** by default, GitHub emails you when a run you triggered fails.

## What CI does not cover

- **Real audio and a real Sonos.** GitHub's Windows runners have no audio devices and no Sonos. Tests that need the
  silent "Steam Streaming" output or CaptureCheck's tone app return early, and the summary lists those that say so.
  This includes the app-restart simulation and the real-session diagnostics test. The effects-isolation test returns
  early without logging anything. Run them locally on a PC that has *Steam Streaming Speakers*. To make a missing
  output a failure instead of a silent skip, set `AUDIORA_REQUIRE_SESSION_TEST=1` locally.
- **The end-to-end self-test with the Sonos** (`SonosFlow.exe --selftest-session …`), listening tests, screenshots
  and UI Automation checks stay manual. See `CLAUDE.md`.
- **Code signing.** The CI installer is unsigned, like the releases.

## When it fails

1. Open the run and click the job marked with ✗. The failed step is red.
   - **Build (Release):** compiler errors are in that step's log.
   - **Test:** the summary lists the failed tests. For full messages and stack traces, download **test-results** and
     open the `.trx` files in Visual Studio or any text editor.
   - **Check and describe the build:** the installer is missing a file or embeds a stale payload. Read the error line.
2. If a timing-sensitive test fails once and passes locally, use **Re-run jobs → Re-run failed jobs**. If it fails
   again, treat it as a real failure.
3. Reproduce it locally with the commands below.

## Run the same checks locally

```powershell
dotnet build SonosFlow.sln -c Release
dotnet test SonosFlow.sln -c Release --logger trx --results-directory TestResults
.\.github\scripts\Write-TestSummary.ps1 -ResultsDirectory TestResults
```

For the installer, follow the `build.ps1` steps (see `CLAUDE.md`), then:

```powershell
powershell -NoProfile -File .github\scripts\Write-BuildSummary.ps1 -Artifacts artifacts
```

`TestResults/` and `artifacts/` are in `.gitignore`.

## Releases stay manual

CI builds are for testing. A GitHub Release is still made by hand, as for `v1.0.0-beta.2`:

1. Set the same SemVer in `src/SonosFlow.App/SonosFlow.App.csproj` and `src/SonosFlow.Setup/SonosFlow.Setup.csproj`
   (for example `1.0.0-beta.3`). The assembly and file versions keep only the numeric part.
2. Write the English notes in `docs/releases/v<version>.md`, with a **Fixed** section that lists only verified fixes,
   and add the entry to `CHANGELOG.md`.
3. Push to `main` and wait for a green CI run on that commit.
4. Build the files with the `build.ps1` steps. Name them `Audiora-Beta-Setup-win-x64.exe` (the installer) and
   `Audiora-Beta-win-x64.zip` (the app folder inside a top-level `Audiora` folder), and add `SHA256SUMS.txt`.
5. Tag the commit (`git tag -a v<version>`), push the tag, and create the release with
   `gh release create v<version> <files> --prerelease --title "Audiora <version>" --notes-file <notes>`.

## Maintenance

- **Actions used:** only GitHub's official actions, by major version: `actions/checkout@v7`,
  `actions/setup-dotnet@v6` and `actions/upload-artifact@v7`. Move to a newer major version when GitHub deprecates
  one; the run page shows a warning first.
- **Moving to a newer .NET:** change `dotnet-version` in both jobs, together with the projects' target frameworks.
- **Scripts:** `.github/scripts/Write-TestSummary.ps1` and `Write-BuildSummary.ps1` only read files and write the
  summary. The build summary needs Windows PowerShell 5.1, because .NET Framework is what can open the installer.
