# CloudFrame

A lightweight Windows slideshow application that displays images directly from OneDrive (and future cloud providers) without storing the full collection locally. Designed to run efficiently on older hardware (e.g. Surface devices) with minimal CPU/memory footprint.

## Current Status

The application builds and runs. The following is working:
- Settings UI with account management, Edge profile picker for sign-in, live OneDrive folder browser, and include/exclude filter rules
- Microsoft Graph API authentication via MSAL.NET with DPAPI-encrypted token cache
- Parallel folder scanning (up to 8 concurrent Graph API requests)
- Delta sync via Graph Delta API — only fetches changes since last scan after first run
- LRU disk cache storing images at screen resolution
- Prefetch queue keeping N images ready ahead of display
- Non-intrusive status window (bottom-right corner, not TopMost) showing scan progress
- Runs at BelowNormal process priority to keep fans quiet

**Known outstanding issue:** ~~The scan sometimes hangs after the initial folder fan-out completes.~~ Fixed — see change log in `GraphApiClient.cs`.

## Solution Layout

```
CloudFrame/
├── CloudFrame.sln
├── README.md
├── docs/
│   └── AzureAppRegistration.md     ← Azure AD app setup guide
└── src/
    ├── CloudFrame.Core/             ← No WinForms dependency, pure .NET
    │   ├── Cloud/
    │   │   └── ICloudProvider.cs   ← Interface + CloudImageEntry DTO
    │   ├── Config/
    │   │   ├── AppSettings.cs      ← Root settings + AccountConfig
    │   │   └── SettingsService.cs  ← JSON load/save, fast startup
    │   ├── Filtering/
    │   │   └── FilterEngine.cs     ← Glob + regex include/exclude rules
    │   └── Index/
    │       ├── ImageIndex.cs       ← Merged, weighted-random image pool
    │       └── IndexCacheService.cs ← Persist index to disk for fast launch
    │
    ├── CloudFrame.Providers.OneDrive/
    │   ├── MsalAuthManager.cs      ← MSAL token acquisition + cache
    │   ├── EdgeProfileDetector.cs  ← Reads Edge profiles from disk
    │   ├── GraphApiClient.cs       ← Graph API HTTP wrapper, parallel scan
    │   ├── DeltaSyncService.cs     ← Graph Delta API, incremental updates
    │   └── OneDriveProvider.cs     ← Implements ICloudProvider
    │
    ├── CloudFrame.App/              ← WinForms project (.NET 8, x64)
    │   ├── Program.cs              ← Synchronous Main(), hands off to SlideshowForm
    │   ├── ProviderFactory.cs      ← Builds ICloudProvider list from AppSettings
    │   ├── SlideshowForm.cs        ← Main form, owns startup sequence in OnLoad
    │   ├── SettingsForm.cs         ← Tabbed settings dialog
    │   ├── OneDriveFolderPicker.cs ← Live OneDrive folder browser (TreeView)
    │   ├── TrayIconController.cs   ← System tray icon + context menu
    │   ├── StatusWindow.cs         ← Non-blocking floating status window
    │   └── Engine/
    │       ├── SlideshowEngine.cs  ← Timer, image sequencing, coordinates all
    │       ├── PrefetchQueue.cs    ← Background download + decode pipeline
    │       ├── DiskCache.cs        ← LRU size-capped local cache
    │       └── IndexService.cs     ← Orchestrates cold-start + cloud refresh
    │
    └── CloudFrame.Tests/
        ├── CloudFrame.Tests.csproj
        └── EngineTests.cs          ← xUnit tests for FilterEngine + ImageIndex
```

## Key Design Decisions

### Synchronous Main()
`Program.Main()` is a plain `void` (not `async Task`). All async startup work happens in `SlideshowForm.OnLoad` after `Application.Run()` has started the message loop. This is critical — making Main async breaks the WinForms STA thread requirement and causes silent failures with tray icons and `BeginInvoke`.

### Fast startup strategy
1. `SettingsService.LoadOrCreate()` — synchronous, <10 ms
2. `IndexCacheService.TryLoadAsync()` — loads last saved index from disk, <100 ms
3. Engine starts with cached index immediately — first image can show before any network call
4. Graph API index refresh runs in background, feeds new index into running engine via `PrefetchQueue.UpdateIndex()`

### Delta sync
- First run: full parallel folder scan, saves a Graph delta token to `%LOCALAPPDATA%\CloudFrame\{accountId}_{hash}.deltatoken`
- Subsequent runs: sends delta token, Graph returns only changes (additions, deletions, modifications)
- Token invalidated automatically on settings change or 410 Gone response from Graph
- Makes subsequent scans fast even with 50,000+ images

### Filter evaluation
Rules checked top-to-bottom, first match wins:
- Match is Include → image allowed
- Match is Exclude → image denied
- No match + Include rules exist → denied (whitelist mode)
- No match + only Exclude rules → allowed (blacklist mode)

### Weighted-random account selection
`ImageIndex` selects images using two-stage random: first pick an account weighted by `AccountConfig.SelectionWeight` (default 1.0 = equal probability per account, not per image), then pick uniformly within that account. Prevents a large account from drowning out a small one.

### StatusWindow threading
`StatusWindow.SetStatus(message)` just writes a `volatile string` field — safe from any thread, no marshalling. A `System.Windows.Forms.Timer` on the UI thread polls every 300ms and updates the label. This avoids `Invoke`/`BeginInvoke` deadlocks that occur when background threads try to marshal to a UI thread that is itself blocked waiting for background work to complete.

### Atomic file writes
Both `SettingsService` and `IndexCacheService` write to a `.tmp` file then `File.Move(..., overwrite: true)`. Prevents JSON corruption if the process is killed mid-write.

## Known Issues

### ~~Scan hangs after initial folder fan-out~~ (Fixed)
Root cause was two compounding issues in `GraphApiClient.ScanFolderAsync`:
1. `GetPageAsync` called `EnsureSuccessStatusCode()` on 429 responses, throwing an exception that propagated up through all nested `Task.WhenAll` calls and crashed the entire scan — even though all images had already been found.
2. Any single subfolder failure (throttle, network hiccup) terminated the whole recursive tree via `Task.WhenAll`.

**Fix applied:** `GetPageAsync` now retries on 429 with up to 5 attempts, respecting the `Retry-After` header and falling back to exponential back-off (4 s, 8 s, 16 s, 32 s, 64 s). After all retries are exhausted it returns `null` (skip the folder) rather than throwing. Subfolder tasks are now wrapped in a `ScanSafe` local function that swallows non-cancellation exceptions so one unreachable folder no longer kills its siblings.

## Setup Before First Build

1. Register an Azure AD app — see `docs/AzureAppRegistration.md`
2. Replace `YOUR_CLIENT_ID_HERE` in `MsalAuthManager.cs` with your Client ID
3. Create a placeholder icon or use the generated one at `src/CloudFrame.App/Resources/cloudframe.ico`
4. Build solution (`dotnet build` or F5 in Visual Studio 2022+)

## First Run Flow

1. App launches → tray icon appears → Settings opens automatically (no accounts configured)
2. Accounts tab → "Add OneDrive…" → give it a name → pick Edge profile → "Sign in…"
3. Browser opens in chosen Edge profile → sign in with Microsoft account → grant Files.Read permission
4. "Add folder…" → live folder browser opens → browse and select root folder(s)
5. Click OK → engine starts → status window shows scan progress in bottom-right corner
6. First image appears once prefetch queue has downloaded and decoded it

## Settings Persistence

All settings saved to `%LOCALAPPDATA%\CloudFrame\settings.json`. Index cache at `%LOCALAPPDATA%\CloudFrame\index.json`. Image cache at `%LOCALAPPDATA%\CloudFrame\ImageCache\`. Delta tokens at `%LOCALAPPDATA%\CloudFrame\{accountId}_{hash}.deltatoken`.

## Keyboard Shortcuts (while slideshow is running)

| Key | Action |
|-----|--------|
| Space / → | Next image |
| S | Open settings |
| Esc | Exit |

## Adding a New Cloud Provider

1. Implement `ICloudProvider` in a new project (e.g. `CloudFrame.Providers.GooglePhotos`)
2. Add a case to `ProviderFactory.Build()` for the new `ProviderType` string
3. Add any provider-specific settings to `AccountConfig.ProviderSettings`
4. Implement `DeltaSyncService` equivalent or reuse with provider-specific delta endpoint

No other changes needed — `ImageIndex`, `FilterEngine`, `IndexService`, and the UI all work against `ICloudProvider` and `CloudImageEntry` abstractions.