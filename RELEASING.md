# Releasing ADMX2Reg

Releases are built, signed and published from a local machine. There is no CI pipeline: the code-signing key never leaves the maintainer's machine.

## Prerequisites

- .NET 10 SDK
- Windows SDK (for `signtool.exe`)
- [GitHub CLI](https://cli.github.com/) (`gh`), logged in with `gh auth login`
- A code-signing certificate in your certificate store (a file, smart card or hardware token all work)

Find the thumbprint of your code-signing certificate:

```powershell
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Format-List Subject, NotAfter, Thumbprint
```

## 1. Set the release variables

Run everything below in one PowerShell session from the repository root.

```powershell
$Version    = '1.0.0'                                    # must match Directory.Build.props
$Thumbprint = '<code signing certificate thumbprint>'
$Timestamp  = 'http://timestamp.digicert.com'            # any RFC 3161 server, for example your CA's
$Repo       = '<owner>/ADMX2Reg'
$ReleaseDir = Join-Path $env:TEMP "ADMX2Reg-$Version"
$SignTool   = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
```

## 2. Bump the version

Set `Version`, `AssemblyVersion` and `FileVersion` in [Directory.Build.props](Directory.Build.props). Every project picks it up from there.

## 3. Test and publish

```powershell
dotnet test tests/ADMX2Reg.Core.Tests
dotnet publish src/ADMX2Reg -p:PublishProfile=Portable
```

The full test run includes a round trip over every policy in `C:\Windows\PolicyDefinitions` and takes a few minutes.

## 4. Copy the exe to a release folder

Copy only the exe. The publish folder may also contain a `settings.json` and a `GPOs` folder from running the app there, and a later publish would overwrite the signed file.

```powershell
New-Item -ItemType Directory -Force $ReleaseDir | Out-Null
Copy-Item src\ADMX2Reg\bin\Release\net10.0-windows\win-x64\publish\ADMX2Reg.exe $ReleaseDir
```

## 5. Sign and verify

Sign the published single-file exe as a whole. The timestamp keeps the signature valid after the certificate expires. A hardware token or cloud HSM may ask for a PIN here.

```powershell
& $SignTool sign /sha1 $Thumbprint /fd SHA256 /tr $Timestamp /td SHA256 /d "ADMX2Reg" /du "https://github.com/$Repo" "$ReleaseDir\ADMX2Reg.exe"
& $SignTool verify /pa /v "$ReleaseDir\ADMX2Reg.exe"
(Get-Item "$ReleaseDir\ADMX2Reg.exe").VersionInfo | Format-List FileVersion, ProductVersion
```

If you publish again after this step, you have to sign again.

## 6. Write the checksum

Do this after signing, because signing changes the file.

```powershell
"$((Get-FileHash "$ReleaseDir\ADMX2Reg.exe" -Algorithm SHA256).Hash)  ADMX2Reg.exe" |
    Set-Content "$ReleaseDir\ADMX2Reg.exe.sha256"
```

## 7. Write the release notes

Add `docs/release-notes/v$Version.md` to the repository: what's new, fixes, known issues, and the SHA256 from step 6. Earlier versions in that folder show the format. Or leave out `--notes-file` in step 9 and write the notes on GitHub.

## 8. Commit, tag and push

```powershell
git add -A
git commit -m "Release $Version"
git tag -a "v$Version" -m "ADMX2Reg $Version"
git push origin main "v$Version"
```

## 9. Create the GitHub release

`--draft` creates the release unpublished, so you can check it on GitHub before making it public.

```powershell
gh release create "v$Version" "$ReleaseDir\ADMX2Reg.exe" "$ReleaseDir\ADMX2Reg.exe.sha256" `
    --repo $Repo --title "ADMX2Reg $Version" --notes-file "docs\release-notes\v$Version.md" --draft
```

Publish the draft from the GitHub page, or with:

```powershell
gh release edit "v$Version" --repo $Repo --draft=false
```

## Verifying a download

Users can check a downloaded exe with:

```powershell
Get-AuthenticodeSignature .\ADMX2Reg.exe | Format-List Status, SignerCertificate
(Get-FileHash .\ADMX2Reg.exe -Algorithm SHA256).Hash
```

The hash must match `ADMX2Reg.exe.sha256` from the release.

## Notes

- With an OV certificate, Windows SmartScreen builds reputation over downloads, so the first users of a new release may still see a warning. EV certificates avoid this.
