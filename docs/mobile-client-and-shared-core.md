# Mobile client & the shared client core

> Applies to the state of the code **after** the mobile client (`MusicPlayerAvaloniaPortMobile`) and the
> shared client core (`MusicPlayerClientCore`) were introduced inside `music-player-avalonia-port`.
> Related documents: [song-library-migrations.md](song-library-migrations.md) and
> [song-database-entry-deduplication.md](song-database-entry-deduplication.md) describe the behaviour that is
> now shared by both clients.

## 1. What was added and why

The Avalonia client was one project: UI, local database, sync client, song registration, choosing, voting,
volume normalization and the audio backend all lived in `MusicPlayerAvaloniaPort`. A phone client needs the
**behaviour** (database, sync, choosing, voting, volume normalization) but none of the desktop UI, and it
needs a *different* audio backend (no FFT/diagram - on a phone that is pure CPU/battery cost).

Copying those services would have meant two implementations of the sync protocol, the migration applier and
the vote scoring that drift apart. So the client was split into

```
music-player-avalonia-port
├── MusicPlayerClientCore           platform independent client (NEW, class library, net10.0)
├── MusicPlayerAvaloniaPort         desktop head (Windows/Linux/macOS) - UI + desktop audio backend
└── MusicPlayerAvaloniaPortMobile   Android head (NEW)                  - mobile UI + mobile audio backend
```

Both heads reference `MusicPlayerClientCore`; the submodules (`MusicPlayerSyncInterface`, `EzAuth`,
`SoundFlow`, `Tmds.DBus`) stay where they were.

## 2. What lives where

| Concern | Project |
|---|---|
| `SongDbContext` (UpvotedSongs + history + queued requests), EF migrations, `SqlitePragmasInterceptor` | core |
| `Config` / `ConfigData`, `PersistenceLocations` (per-platform data directory) | core |
| `DbWrapperService` (registration, matching, duplicate merge, incremental pull) | core |
| `SongSyncService` (pull, votes, uploads, song library migrations, lazy tag/upload worker) | core |
| `SongChoosingService` (weighted choosing list, play chance), `SongPlaybackService` (runtime history) | core |
| `SongVotingService` (score/streak/likes/dislikes), `SongVolumeService` (normalization), `SongInfoService` | core |
| `HelperFuncs` (library walking, tag reading, string distances) | core |
| `ServiceContainer` + `RegisterImplementation` | core |
| Views, view models, export/statistics/history windows, `AudioLibWrapperService` (SoundFlow + FFT), key hook, MPRIS, system audio capture, download processor | desktop |
| Single main view + view model, `MobileAudioPlayerService` (SoundFlow, no FFT), Android bootstrap | mobile |

**Namespaces were deliberately kept** (`MusicPlayerAvaloniaPort.Services.Song`, …), so the desktop code
needed no `using` changes at all - only the files moved and the project gained a reference.

## 3. The one real abstraction: `IAudioPlaybackService`

Playback is the only thing that genuinely differs, so it is the only interface extracted:

```csharp
public interface IAudioPlaybackService
{
    float Volume { get; set; }
    float? PlayProgress { get; set; }
    float? SongDurationSeconds { get; }
    float SeekedPlayProgress { get; }          // what the vote scoring needs (seek vs. listen)
    event EventHandler<EventArgs>? PlaybackEnded;
    event EventHandler<EventArgs>? FinishedReading;
    void PlaySong(string songPath, bool measureWholeSongForVolumeNormalization);
    void TogglePlayPause(bool updateAudioDevicesInfo = false);
    float? CurrentSongRootMeanSquare { get; }
    string? MeasuredSongPath { get; }
}
```

* **Desktop** (`AudioLibWrapperService`) keeps everything it had: the pre-read strategy (`GlobalArray`) for
  songs whose loudness is unknown, the cheap `DirectRead` strategy for songs that were measured already, the
  samples window and the FFT spectrum analysis. It now also owns the RMS computation (it has the samples) and
  reports which song the measurement belongs to.
* **Mobile** (`MobileAudioPlayerService`) plays through the same SoundFlow/MiniAudio stack but has no sample
  reader and no spectrum analyzer. Its loudness measurement streams the file through a second decoder in
  4k-sample chunks and accumulates the sum of squares, so the memory cost is constant regardless of song
  length - the desktop variant materializes the whole decoded song instead.

`MeasuredSongPath` fixes a latent race in the old code: measuring a song can finish after the user already
skipped on, and without knowing which song a measurement belongs to, `SongVolumeService` would have written
the skipped song's loudness onto its successor (which then sticks forever, because volume is only written
when it is `<= 0`).

The interface is free of SoundFlow types on purpose, so the core does not depend on an audio backend at all.

## 4. Dependency injection

`ServiceContainer` moved into the core and became assembly-aware, because the services now live in two
assemblies:

* it always scans **its own** assembly (the shared services),
* each application registers its own assembly from a `[ModuleInitializer]`
  (`MusicPlayerAvaloniaPort/ServiceContainerSetup.cs`, `MusicPlayerAvaloniaPortMobile/ServiceContainerSetup.cs`),
  which the runtime guarantees runs before any other code of that assembly - i.e. before the first resolve,
* `AddServices(...)` adds what only the application can know (the `HttpClient`, the `EzKeycloak` auth backend,
  MPRIS on Linux) and **binds the audio abstraction**:
  `services.AddSingleton<IAudioPlaybackService>(sp => sp.GetRequiredService<AudioLibWrapperService>())`
  (mobile: `MobileAudioPlayerService`). Binding it to the *same* singleton the UI resolves is what keeps one
  single audio pipeline - two would mean the volume normalization drives a silent one.

Two implementation details worth knowing:

* The provider is published **before** the startup hooks run. Without that, a hook resolving a service finds
  the container "not built" and re-enters `Build()` forever (this was a real crash found by the harness below).
* `AddServiceAssembly`/`AddServices`/`AddStartupHook` throw once the container was built, so a registration
  that was forgotten or made too late fails loudly at startup instead of leaving a service silently missing.

The desktop startup hooks (`SongDownloadRequestProcessorService.Init()`, `KeyHookService.Init()`) moved from
the old static constructor into `MusicPlayerAvaloniaPort/ServiceContainerSetup.cs` - same instant (first
resolve) as before.

## 5. Mobile specifics

* **Data directory**: `PersistenceLocations.Configure(appName, resolver)` lets the mobile client point the
  shared core at `Context.FilesDir` (the app package is read-only, so there is no "folder next to the
  executable" as on the desktop). Desktop keeps the old behaviour including the one-time migration of the
  legacy `Persistence` folder; the migration is skipped when a custom directory is configured.
* **Music folder**: the public `Music` directory of shared storage is discovered automatically; a configured
  folder wins while it still contains mp3s.
* **Permissions**: `READ_MEDIA_AUDIO` on API 33+, `READ_EXTERNAL_STORAGE` below (declared with `maxSdkVersion`),
  requested in `MainActivity`.
* **Native libraries**: SoundFlow's `runtimes/android-*/native/libminiaudio.so` files are copied into the
  output folder by a normal project reference, which is *not* where Android loads natives from. The mobile
  csproj re-declares them as `<AndroidNativeLibrary ... Abi="..."/>`, which packages them as
  `lib/<abi>/libminiaudio.so` inside the APK. Without that, playback compiles fine and fails at runtime with a
  `DllNotFoundException`.
* **UI**: one view (`MobileMainView`) - the header row is a **library search field**, then artwork with
  embedded cover art (or a placeholder), title/artist, a single chip with the vote numbers, seek slider,
  transport, ONE vote gesture (a round, icon-only heart) and the volume slider (0..200%).
  `Sync` opens a bottom sheet with host/account, login+pull, rescan, the folder the library is read from and
  the library take-over prompt.
  There is no permanent status text on the player screen: the running log lives in the sheet's STATUS /
  SYNC STATE fields, and the line under the search field is reserved for failures and action results, which
  hide themselves after a few seconds. The search ranks with the shared modified-Levenshtein matching
  (`SongPlaybackService.FindBestSongMatches`, the same one the desktop's "play a song quickly" flow uses) and
  shows the best eight as a drop down; tapping one plays it. Everything the UI used to show that a listener
  cannot act on is gone: the five most likely next songs (display-only), the play-chance chip, the
  volume-normalization chip, the vote button's label and its explanation line.
* **The UI never calls the voting service.** The single `Upvote` button only flips
  `SongPlaybackService.UpvoteLockedIn`; the vote itself is cast by the shared playback logic when the song
  ends or is skipped, and a song skipped early is voted down by that same logic - exactly like the desktop
  client (an earlier mobile UI had `Dislike`/`Lock in`/`Upvote` buttons that called `SongVotingService`
  directly, which duplicated rules that already live in `GetNextSong`/`GetPreviousSong`).
* **Media notification / background playback**: `MobilePlaybackNotificationService` is a foreground service
  (type `mediaPlayback`) owning an ongoing `MediaStyle` notification and a `MediaSession` - cover art, title,
  artist/album and previous/play-pause/next in the shade and on the lock screen. It is the reason playback
  survives leaving the app. Its buttons call the same shared services the UI calls, so the vote logic cannot
  drift between the two. Platform APIs only (`Notification.MediaStyle` + `MediaSession`), no AndroidX Media.
  Note that the notification's progress row reads the duration from `MediaMetadata`, not from
  `PlaybackState`.
* **Sync session controls**: the mobile sync sheet exposes "Log in & upload" (`Init(..., TryCallApiInit: true)`,
  the whole-library `/sync/init` bootstrap plus the queued-retry pass), "Log in (no upload)"
  (`TryCallApiInit: false` *and* `RetryUnsyncedEntries: false` - pull only, nothing is sent) and "Log out"
  (`SongSyncService.Logout()`). Logging out only forgets the stored refresh token locally; the local database
  and the configured account name stay, so the library keeps working offline. There is no Keycloak logout call
  because `EzAuth` exposes no logout endpoint. A logged-out client makes no requests at all: the network paths
  check the session, and queued uploads/votes stay queued instead of being lost. Verified by
  `.build-check/sync-login-harness` against a fake sync server (asserts that "no upload" never sends
  `POST /v1/sync/init` while the ordinary login does, and that logout clears the session without touching the
  database).
* **Launcher icon**: converted from the desktop client's `MusicPlayerAvaloniaPort/Assets/icon.ico` (the
  256x256 PNG frame embedded in it is extracted losslessly - Android cannot use `.ico`). Android 8+ gets a
  real **adaptive icon** (`mipmap-anydpi-v26/ic_launcher.xml`: `@color/ic_launcher_background` + a 108dp
  foreground with the mark inside the central 66dp safe zone), with the legacy bitmaps kept for older
  releases. The background is a declared colour on purpose: a launcher always paints a shape behind an icon
  and fills it with the icon's declared background, so `@android:color/transparent` behaved differently per
  device (MIUI painted it black, and a legacy-only icon got MIUI's white plate) even though the artwork is
  demonstrably transparent.
* **Release trimming + profile-guided AOT** (`PublishTrimmed`, `TrimMode=partial`, `RunAOTCompilation`,
  `AndroidEnableProfiledAot`; Debug does neither). See §7 for the measurements and §7.1 for why the
  reflection based DI survives it - and what would break it.

## 6. Startup order (identical in both clients)

```
EnsureDatabaseUpToDate → resolve library folder → Pull() (+ apply song library migrations)
                       → UpdateAvailableSongPaths() (scan, lazy registration, kicks the tag/upload worker)
```

The mobile view model runs exactly this sequence off the UI thread (`MobileMainViewModel.StartStartup`), which
is the same order the desktop client uses on its song-setup thread
(`MainView.SetupUi`: `ResolveSongLibraryPath → StartupSync → ScanSongLibrary`).

## 7. Verification performed

* Desktop client: builds (`dotnet build ... -p:UsedAvaloniaProducts=`), 0 errors.
* Android client: builds in **both** `Debug` and `Release` against `net10.0-android` and produces a signed
  APK; the APK contains `lib/{arm64-v8a,x86_64}/{libminiaudio,libe_sqlite3,libSkiaSharp,libHarfBuzzSharp}.so`.
* DI: an integration harness (`.build-check/di-harness`, not part of any repo) resolves every service of both
  assemblies through the real container, asserts that `IAudioPlaybackService` is the same instance as
  `AudioLibWrapperService`, and opens the SQLite database (`DI HARNESS RESULT: PASS`). It found both the
  container re-entrancy bug and the missing `IAudioPlaybackService` registration during the refactor.

Not verified here: behaviour on a real device from the build environment, and therefore notably the trimmed
`SoundFlow` playback path in the `Release` APK.

### 7.2 On-device verification (and the first real bug)

The Release APK was then installed and exercised on a real Android phone: library scan (343 songs), playback,
cover art, volume normalization (stored RMS), upvoting (score/likes) and the upvote-lock toggle all work,
with an empty crash buffer.

The first attempt crashed at startup, and it is worth recording what it was *not*:

```
System.ArgumentNullException: Arg_ParamName_Name, source
  at System.Linq.Enumerable.First
  at MusicPlayerAvaloniaPortMobile.Services.MobileAudioPlayerService..ctor
```

Not trimming, not AOT, not the DI container - although the stack trace runs through
`Microsoft.Extensions.DependencyInjection` (which is what made it look like a DI/trimming problem). The
player asked for `PlaybackDevices.FirstOrDefault(d => d.IsDefault)` and then indexed that device's
`SupportedDataFormats`. Android enumerates exactly one device and does **not** flag it as default, so
`FirstOrDefault` returned `default(DeviceInfo)` - a struct with a null format array - and `.First()` threw in
the constructor. Since the constructor runs during DI activation, the app died before its UI existed.

The fix has two independent halves, and both matter:

1. the device is optional (`InitializePlaybackDevice` takes a nullable `DeviceInfo`; null = "open the system
   default"), with a 48 kHz stereo F32 fallback format;
2. audio setup is no longer fatal - engine and output device are created on first use inside a `try`/`catch`
   and failures surface as `IsAvailable`/`LastError`, which the UI shows.

The debugging gap that made the crash hard to read is closed as well: the mobile head now routes everything
(startup stages, folder accept/reject reasons, audio device/format, measurements, and all unhandled
exceptions) through `MobileLog` to logcat under the `MusicPlayerMobile` tag, in Release builds too - the
Android crash dialog only offers "send the summary to the OS developers", `Debug.WriteLine` is compiled out of
Release and the shared core's `Console.WriteLine` never reaches logcat there.

### 7.1 Trimming: what is actually trimmed, and what that means for the DI

Release APK sizes (arm64 + x86_64): untrimmed 90.2 MB → trimmed 52.2 MB → trimmed + profiled AOT 62.4 MB
(shipped); trimmed + *full* AOT 106.9 MB, i.e. compiling everything costs more than the IL it replaces.

Comparing the linked assemblies against their inputs shows partial trimming only trims what opts in:

| Assembly | untrimmed | linked | trimmed? |
|---|---|---|---|
| `SoundFlow.dll` | 1 032 192 | 143 360 | **yes** (`IsAotCompatible`) |
| `Microsoft.Extensions.DependencyInjection.dll` | 96 056 | 46 080 | **yes** |
| `MusicPlayerClientCore.dll` | 125 440 | 125 440 | no |
| `MusicPlayerAvaloniaPortMobile.dll` | 105 472 | 105 472 | no |
| `Microsoft.EntityFrameworkCore.dll` | 2 533 408 | 2 533 408 | no |
| `taglib-sharp.dll` | 491 008 | 491 008 | no |

So on Android the reflective machinery (the container's `Assembly.GetTypes()` scan, EF Core's model, TagLib's
file type resolution) keeps its metadata - which is why there are no trim warnings and why the app works.

**This safety is a property of the platform's linker defaults, not of the code.** Publishing the *desktop*
client trimmed (self-contained, `TrimMode=partial`, same shared core) trims our assemblies hard and the
container then fails at runtime:

```
MusicPlayerClientCore.dll      125 440 -> 11 776
MusicPlayerAvaloniaPort.dll    607 744 -> 214 016
FAILED to resolve SongSyncService: A suitable constructor for type
'...SongSyncService' could not be located.
```

That is the concrete argument for replacing the reflection scan with a source generated registration: it
would make the wiring independent of trim mode and platform linker defaults, and would be what makes
`TrimMode=full` possible. It is *not* required for the shipped Android configuration (partial trimming never
touches those assemblies) - and `full` would additionally require keeping EF Core and TagLib whole anyway,
which together are 3 MB of the 62 MB APK.

## 8. Cheat sheet

| Concern | Where |
|---|---|
| Shared client core | `music-player-avalonia-port/MusicPlayerClientCore` |
| Audio abstraction | `MusicPlayerClientCore/Services/Infrastructure/IAudioPlaybackService.cs` |
| Desktop audio backend (FFT, pre-read/direct-read) | `MusicPlayerAvaloniaPort/Services/Infrastructure/AudioLibWrapperService.cs` |
| Mobile audio backend (lean player, streaming RMS) | `MusicPlayerAvaloniaPortMobile/Services/MobileAudioPlayerService.cs` |
| DI container / attribute | `MusicPlayerClientCore/DependencyInjection/ServiceContainer.cs` |
| Desktop DI wiring + startup hooks | `MusicPlayerAvaloniaPort/ServiceContainerSetup.cs` |
| Mobile DI wiring | `MusicPlayerAvaloniaPortMobile/ServiceContainerSetup.cs` |
| Paths override | `MusicPlayerClientCore/Persistence/PersistenceLocations.cs` (`Configure`) |
| Android bootstrap (paths, library folder, intents) | `MusicPlayerAvaloniaPortMobile/MobilePlatform.cs` |
| Mobile UI | `MusicPlayerAvaloniaPortMobile/Views/MobileMainView.axaml(.cs)` + `ViewModels/MobileMainViewModel.cs` |
| Build instructions / limitations | `MusicPlayerAvaloniaPortMobile/README.md` |
