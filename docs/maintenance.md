# Keeping Windshot up to date

How each dependency reaches people's PCs, what updates by itself, and the routine for a
maintenance release.

## What updates on its own

| Dependency | How it ships | Updates without a new release? |
|---|---|---|
| .NET 10 runtime | Bundled in the package (`SelfContained`) | No. A rebuild picks up the patch the installed SDK carries. |
| Windows App SDK / WinUI | Shared framework package from the Store | Patch releases, yes: the Store services the framework. A new minor or major version needs a version bump here. |
| Win2D | DLL bundled in the package | No. |
| Screen capture, Media Foundation, WASAPI, text recognition | Part of Windows | Yes, through Windows Update. These are the likeliest source of surprise changes. |
| Microsoft.Windows.SDK.BuildTools | Build time only | Not relevant to users. |

## Dependabot

`.github/dependabot.yml` checks the NuGet packages in `src/Windshot` monthly and opens at most
three pull requests: one for the Windows App SDK packages (kept in step), one for the rest.
Nothing changes until a pull request is merged. Before merging, build a test copy and run the
release checklist below. The release build is trimmed, so a package update can break something
only at run time in Release (not in Debug).

Dependabot doesn't update .NET itself: that comes from the installed SDK (see below).

## Maintenance release (every 3 to 6 months)

1. Update the .NET 10 SDK to the latest patch (`winget upgrade Microsoft.DotNet.SDK.10`, or
   Visual Studio), and check with `dotnet --version`.
2. Merge any waiting Dependabot pull requests, or bump the versions in
   `src/Windshot/Windshot.csproj` by hand.
3. Raise the version in `src/Windshot/Package.appxmanifest` (`Identity` `Version`).
4. Build a test copy: `powershell -File tools/package.ps1 -TestInstall`.
5. Run the release checklist.
6. Upload `artifacts\store\Windshot_<version>_x64.msix` in Partner Center, then remove the test
   copy with `tools/package.ps1 -Uninstall`.

Most .NET security patches don't affect Windshot (it makes no network connections, and Windows
decodes images), so there's no need to release for each one. Release sooner if a patch fixes
something Windshot uses, such as WinForms, JSON or COM interop.

### Release checklist

Run in the test copy, at the PC's usual display scale:

- Capture a region, a window and the whole screen (Enter); copy, save as PNG and JPEG, and pin.
- Annotate: arrow, text, numbered steps, blur, a pasted picture; reopen the layers panel.
- Scrolling capture of a long web page.
- Copy text from a region.
- Record about 10 seconds with sound, pause and resume, then stop.
- In the editor: add a zoom and a highlight, scrub and play, trim, export MP4 and GIF, and play
  the MP4 in another player.
- Open an image and a video with Open with in File Explorer.
- Settings: change a shortcut, then quit and restart Windshot.
- Check the log in `%LOCALAPPDATA%\Packages\<package family name>\LocalState` for errors.

## Dates to plan for

- **November 2028:** .NET 10 support ends. Move to .NET 12 (the next long-term release, due
  November 2027) during 2028: change `TargetFramework`, rebuild, and run the checklist.
- **Yearly:** check that the Windows App SDK version in use is still supported, and move to
  the current stable line if not.
- **After each big Windows feature update:** watch reviews and Partner Center's crash reports
  for capture or recording problems.
