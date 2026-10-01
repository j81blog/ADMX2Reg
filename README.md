# ADMX2Reg

A portable Group Policy editor that never applies anything. You build Group Policy Objects from ADMX templates the same way you would in GPMC and the Group Policy Editor, and then export them as:

- a `.reg` file
- a PowerShell script (standalone and idempotent, or a minimal list of commands)
- `Registry.pol` files (`Machine\Registry.pol` and `User\Registry.pol`), usable with LGPO.exe or in a real GPO

You can also go the other way: import a `.reg` or `Registry.pol` file and the tool maps the values back onto ADMX policies.

## Features

- Reads any PolicyDefinitions folder. The default is `C:\Windows\PolicyDefinitions`; you can switch to a domain central store (`\\domain\SYSVOL\domain\Policies\PolicyDefinitions`, with auto-detection) or any other folder.
- ADML language follows the Windows display language with a fallback to en-US, and can be changed in Settings.
- Multiple GPOs, each with Computer Configuration (HKLM) and User Configuration (HKCU) settings.
- Policy dialog like gpedit: Not Configured / Enabled / Disabled, comment, Supported on, help text, Previous/Next setting, and the options generated from the ADMX presentation (check boxes, drop-downs, numbers, text, multi-line text, lists).
- Live registry preview for the selected policy.
- Search across names, help text, registry keys and value names, plus a "Configured only" filter.
- Duplicate GPOs and copy settings between GPOs.
- GPMC-style HTML settings report.
- Registry values from an import that match no policy are kept as "Extra Registry Settings" and exported as-is, so an import never loses data.
- Fluent design (WPF-UI) with Mica and light/dark themes.

## Download and run

Build the portable exe (see below), or use the release build. Copy `ADMX2Reg.exe` anywhere and run it. No installation and no .NET runtime are needed.

On first start it creates `settings.json` and a `GPOs` folder next to the exe. If that folder is not writable, it uses `%LOCALAPPDATA%\ADMX2Reg` instead. Every GPO is one JSON file and changes are saved immediately.

The GPO folder is stored as the relative path `.\GPOs`, so you can move or copy the whole folder (for example to a USB stick) and your GPOs come along. In Settings you can point it to any other folder; a folder inside the app folder stays relative, anything else is stored as a full path.

You can also ship your own templates with the app: put a `PolicyDefinitions` folder with ADMX files next to the exe and it is used automatically on first start, as `.\PolicyDefinitions`. ADML language files can be in language subfolders (`en-US`, `nl-NL`, ...) or directly next to the ADMX files.

## Export behavior

The export follows what the Group Policy engine writes:

| State | Result |
| --- | --- |
| Enabled | `enabledValue` (or `REG_DWORD 1`), `enabledList`, and every option value |
| Disabled | `disabledValue` (or the value is deleted), `disabledList`, option values deleted, list keys cleared |
| Not configured | Nothing |

A few things to know:

- **List policies** replace all values in their key (like `**delvals.` in Registry.pol). The `.reg` format can only do that by deleting and recreating the key, which also removes its subkeys. The `.reg` file has a comment wherever this happens. PowerShell and Registry.pol clear only the values.
- **HKCU settings** in `.reg` and `.ps1` files apply to the user who runs them. Use Registry.pol (User) if you need them for other users.
- **The standalone PowerShell script** requires an elevated session when it contains HKLM settings. It supports `-WhatIf` and `-Verbose`, skips values that already match, and prints a Changed / Unchanged / Failed summary. Scripts run on Windows PowerShell 5.1 and PowerShell 7.
- `.reg` files are saved as UTF-16 LE, like regedit does. `.ps1` files are saved as UTF-8 with BOM, so Windows PowerShell 5.1 reads non-ASCII text correctly.

## Build

Requires the .NET 10 SDK on Windows.

```bash
dotnet build ADMX2Reg.sln
```

```bash
dotnet test tests/ADMX2Reg.Core.Tests
```

```bash
dotnet publish src/ADMX2Reg -p:PublishProfile=Portable
```

The portable exe ends up in `src/ADMX2Reg/bin/Release/net10.0-windows/win-x64/publish/`.

To sign the exe and publish a release on GitHub, see [RELEASING.md](RELEASING.md).

The test suite includes a round trip over every policy in the local `C:\Windows\PolicyDefinitions`. For each policy it goes from policy to registry operations to `.reg`/`Registry.pol` and back through the importer, then checks that the registry operations are identical. That test takes a few minutes.

## Project layout

| Path | Contents |
| --- | --- |
| `src/ADMX2Reg.Core` | ADMX/ADML parser, policy engine, GPO storage, exporters, importers, HTML report |
| `src/ADMX2Reg` | WPF user interface |
| `tests/ADMX2Reg.Core.Tests` | xUnit tests |

## License

ADMX2Reg is free and open source under the [MIT License](LICENSE). You may use, modify and redistribute it, also commercially, as long as the copyright notice is kept. The release exe bundles WPF UI, the .NET Community Toolkit and the .NET runtime, all MIT licensed; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
