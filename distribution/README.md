# Building and releasing Kairos Studio

Every version ships with the same number in three formats:

1. `KairosStudio-VERSION-Docker.zip`: a clean build context, Compose files, instructions and start scripts for
   Windows, Linux and macOS. The first build needs Docker and internet access.
2. `KairosStudio-VERSION-Setup-win-x64.exe`: per-user Inno Setup installer in five languages, with shortcuts and
   an uninstaller. It installs WebView2 if missing and keeps the existing library in
   `%LOCALAPPDATA%\ClipperStudio`.
3. `KairosStudio-VERSION-Portable-win-x64.zip`: extract everything and run; `portable.flag` keeps the data in
   `data/` beside the executable. It needs a writable folder. After moving to another computer or Windows account,
   enter the keys again.

The release process on GitHub (branches, version bump, tags and draft releases) is described in
[CONTRIBUTING.md](../CONTRIBUTING.md#releases). This page covers what happens underneath and how to build locally.

## Local build

Requirements: Windows x64, the .NET 10 SDK (or a compatible later one), Python 3.12+, a running Docker and internet
access. Inno Setup is downloaded from its official release and its signature is validated before it runs; review
its license terms for commercial use. The WebView2 bootstrapper must also carry a valid Microsoft signature.

```powershell
python scripts/set-version.py 0.10.1
# Update CHANGELOG.md and run the tests.
python scripts/check-version.py --changelog
./release.ps1 -ExpectedVersion 0.10.1
python scripts/verify-release.py releases/0.10.1
```

The command writes the three packages to `releases/VERSION/`, plus the local image `kairos-studio:VERSION`,
`SHA256SUMS.txt` and `release-manifest.json`. It doesn't replace the running container or touch existing libraries.
A finished directory is never overwritten; use `-OutputDirectory` to rebuild. `-Target Desktop` and
`-Target Docker` build one platform at a time, as CI does.

The installer uses a stable AppId to upgrade the same installation, and uninstalling never deletes user data.
Close Kairos before upgrading; Restart Manager warns about files in use. To upgrade the portable copy, keep
`data/`, `portable.flag` and any external video folders.

## Signing

Without a publisher identity the builds are **unsigned** local builds, marked `publicReleaseReady: false`. No test
certificates are created. To require signatures:

```powershell
./release.ps1 -RequireSignature -CertificateThumbprint YOUR_THUMBPRINT
# Or with Microsoft Artifact Signing:
./release.ps1 -RequireSignature -SigningMetadata METADATA_PATH -SigningDlib DLIB_PATH
```

The app's own binaries, the installer and the embedded uninstaller are signed, timestamped and verified.
Third-party executables keep their original signatures. See [SIGNING.md](../desktop/SIGNING.md). The manifest
only marks a complete build as ready for signed distribution when the signature was verified; this doesn't
guarantee immediate SmartScreen reputation and doesn't replace a license review.

## GitHub workflows

- [`ci.yml`](../.github/workflows/ci.yml) runs on every push to `dev` and `main` and on every pull request: version
  check, Python tests on Linux, and the desktop build and integration tests on Windows.
- [`release.yml`](../.github/workflows/release.yml) runs when a `vX.Y.Z` tag is pushed. It rejects tags that are
  not on `main` or don't match the version and a `CHANGELOG.md` section, builds Windows and Docker, runs the tests
  audits secrets and dependencies, and attaches the three packages and a combined `SHA256SUMS.txt` to a **draft release** in the `production`
  environment. On a tag it also publishes the Docker image to GitHub Packages as
  `ghcr.io/zntoff/kairos-studio:VERSION`. Only stable releases update `:latest`. It can also be run manually to get the packages as
  workflow artifacts, without releasing.

The default workflow builds Windows unsigned and never publishes on its own; it has no access to certificates or
personal keys. The Docker download builds the image locally and doesn't depend on an external registry.

## Private files and third-party components

Packaging blocks `.env`, keys and certificates, databases, libraries and personal settings. Docker uses an
explicit list of folders. Never share a used portable copy together with its `data/` folder.

Packages include LICENSE, NOTICE, the font and sound licenses, the FFmpeg GPL text and the yt-dlp license. Before
a public distribution, keep the sources matching the exact FFmpeg build and its dependencies, and provide access
to them as their licenses require; generic links don't replace that. Check the bundled versions with
`tools/ffmpeg.exe -version` and `tools/yt-dlp.exe --version`.
