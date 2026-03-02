# MousePassport

MousePassport is a Windows tray application that prevents the cursor from being pulled onto an unwanted secondary display. It is aimed at users who run a small auxiliary monitor (for example 1024×700 px) alongside one or more primary displays. Windows tends to attract the pointer toward the bottom edges of the multi-monitor arrangement, so when reaching for the system tray, clock, Start button, or taskbar icons, the mouse often jumps onto the small display instead of staying on the main screen. MousePassport stops that by controlling where the cursor is allowed to cross between monitors.

## How it works

The app runs in the system tray and enforces **monitor boundaries**: the cursor cannot leave the current monitor except through **pass-through segments** (ports) that you configure on shared edges. By default, every edge between two monitors is closed—no pass-through—so the pointer stays on the monitor it is on until the user deliberately moves through a port. Users can open the setup window (double-click the tray icon or choose **Configure...** from the tray menu) to define which parts of each shared edge act as ports. The cursor can cross only where a port is defined; everywhere else, movement is blocked and the cursor is kept on the current monitor.

Two enforcement modes are available: **ClipCursor** (default), which uses the Windows cursor clip API and is generally smoother, and **Hook**, a legacy mode that uses a low-level mouse hook. Configuration is stored per display layout in `%AppData%\MousePassport\config.json`, so when monitors or arrangement change, the app can reload and recalculate edges.

A **panic hotkey** (Ctrl+Alt+Pause) disables enforcement immediately so the user can move the cursor freely if needed; the tray icon can be used to turn enforcement back on or to exit.

## Requirements

- Windows 10 or later (64-bit recommended for the pre-built release)
- For building from source: .NET 8.0 SDK (Windows desktop workload). A self-contained release does not require .NET to be installed on the target machine.

## CI and release pipeline (GitHub Actions)

- **CI** (`.github/workflows/ci.yml`): On every push and pull request to `main`/`master`, the workflow builds the solution and runs the unit tests on Windows. This keeps the main branch buildable and regression-free.

- **Release** (`.github/workflows/release.yml`): When you push a version tag (e.g. `v1.0.0`), it runs the same build and tests, then publishes a self-contained win-x64 build, zips it, and creates a GitHub Release with the zip attached. Users can download `MousePassport-1.0.0-win-x64.zip` from the Releases page.

  **To cut a release:** update the version in `MousePassport.App.csproj` if desired, commit, then create and push a tag:
  ```bash
  git tag v1.0.0
  git push origin v1.0.0
  ```
  The release is created automatically. You can also run the “Release” workflow manually from the Actions tab (optionally with a version input).

## Creating a release locally

To build a distributable release (self-contained, no .NET install required):

```bash
dotnet publish MousePassport.App\MousePassport.App.csproj -c Release /p:PublishProfile=Release-win-x64
```

Output is written to the `publish\` folder at the solution root. Zip that folder and distribute it; users run `MousePassport.App.exe` from inside it.

To publish for 32-bit Windows instead:

```bash
dotnet publish MousePassport.App\MousePassport.App.csproj -c Release -r win-x86 --self-contained true -o publish
```

## Building and running

1. Open `MousePassport.sln` in Visual Studio or build from the command line:
   ```bash
   dotnet build MousePassport.sln -c Release
   ```
2. Run the unit tests (optional):
   ```bash
   dotnet test MousePassport.sln -c Release
   ```
3. Run the app:
   ```bash
   dotnet run --project MousePassport.App\MousePassport.App.csproj -c Release
   ```
   Or run the built executable from `MousePassport.App\bin\Release\net8.0-windows\`.

After starting, the user should see the MousePassport icon in the system tray. Double-click it to open the setup window and adjust pass-through edges for the current monitor layout.

## License

See the repository for license information.
