<a href="https://github.com/Zntoff/kairos-studio/releases/latest">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/readme/banner-dark.png">
    <img src="docs/readme/banner-light.png" width="100%" alt="Kairos Studio. The exact moment. Turn any long video into vertical shorts: the AI finds the moments, edits, captions and adds sound, and you approve every clip.">
  </picture>
</a>

<!-- downloads:start -->
<p align="center">
  <a href="https://github.com/Zntoff/kairos-studio/releases/download/v0.12.0-beta.11/KairosStudio-0.12.0-beta.11-Setup-win-x64.exe"><b>Download Windows installer (EXE)</b></a> ·
  <a href="https://github.com/Zntoff/kairos-studio/releases/download/v0.12.0-beta.11/KairosStudio-0.12.0-beta.11-Portable-win-x64.zip"><b>Download portable ZIP</b></a> ·
  <a href="https://github.com/Zntoff/kairos-studio/releases/download/v0.12.0-beta.11/KairosStudio-0.12.0-beta.11-Docker.zip"><b>Download Docker bundle</b></a>
</p>
<!-- downloads:end -->

The downloads above are for the current beta. [Stable releases](https://github.com/Zntoff/kairos-studio/releases/latest) are also available.

<p align="center">
  <a href="https://github.com/Zntoff/kairos-studio/releases/latest"><img src="https://img.shields.io/github/v/release/Zntoff/kairos-studio?style=flat-square&color=0A0A0A&label=release" alt="Latest release"></a>
  <a href="https://github.com/Zntoff/kairos-studio/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/Zntoff/kairos-studio/ci.yml?branch=main&style=flat-square&label=CI" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache%202.0-0A0A0A?style=flat-square" alt="Apache 2.0 license"></a>
  <img src="https://img.shields.io/badge/Windows%2011-desktop-0A0A0A?style=flat-square" alt="Windows 11 desktop">
  <img src="https://img.shields.io/badge/Docker-web-0A0A0A?style=flat-square" alt="Docker web">
</p>

<p align="center">
  <a href="#install"><b>Install</b></a> ·
  <a href="#what-it-does"><b>What it does</b></a> ·
  <a href="#works-with-your-ai"><b>AI providers</b></a> ·
  <a href="docs/architecture.md"><b>Architecture</b></a> ·
  <a href="CONTRIBUTING.md"><b>Contributing</b></a>
</p>

<br>

## What it does

Open **Home > Guided demo** to try the Shorts workflow with a sample project and download a vertical MP4.

Kairos Studio turns streams, podcasts, interviews, classes, talks, tutorials, vlogs and gameplay into vertical
shorts for **YouTube Shorts, TikTok and Instagram Reels**. You add a video (a file, or a YouTube, Twitch or Kick
link), the AI proposes the moments worth a clip, and every short comes out at 1080×1920, ready to post.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/readme/steps-dark.png">
  <img src="docs/readme/steps-light.png" width="100%" alt="Four steps: finds the moments, edits each clip, captions and sound, you approve.">
</picture>

- **Finds the moments.** It transcribes the video and reads the voice, the loudness and the scene cuts. Every
  proposal comes with a score and a reason, and you choose which ones to generate.
- **Edits each clip.** A hook and your own commentary on screen, so the clip adds something instead of being a plain
  cut, plus framing, zooms, color looks and animated titles.
- **Captions and sound.** Word-by-word captions in 8 styles with key words highlighted and translation (English and
  Spanish), sound effects on the beat and music ducked under the voice, normalized to −14 LUFS.
- **Writes the post.** Title, description and hashtags per platform and per language.
- **You stay in control.** A timeline editor with a live preview lets you fine-tune everything, and no clip posts on
  its own: each one waits in an **approval queue** until a person decides.
- **Private by design.** Your videos, transcripts and clips never leave your computer. No servers of ours and no
  telemetry.

<img src="docs/readme/app-editor.png" width="100%" alt="The Kairos Studio timeline editor with caption styles, a 9:16 live preview and tracks for titles, hook, captions, video, effects and audio.">

<table>
  <tr>
    <td width="50%"><img src="docs/readme/app-select.png" alt="Choose the moments: AI-proposed moments with scores and the generation options for framing, caption style and languages."></td>
    <td width="50%"><img src="docs/readme/app-clips.png" alt="The clips view with vertical shorts, their hooks and captions, waiting for review."></td>
  </tr>
  <tr>
    <td align="center"><sub><b>Choose the moments</b> · scores, framing, caption style and languages</sub></td>
    <td align="center"><sub><b>Clips</b> · every short waits for your approval</sub></td>
  </tr>
</table>

<sub>Screenshots use synthetic footage and the app's offline mode, which runs the whole pipeline without an AI
provider.</sub>

## Works with your AI

Everyone uses their own API key and pays their provider directly. Claude is set up by default; for other
providers, the app loads the provider's model list and you choose a model for each tier (fast, standard, superior)
in **Settings → AI provider**.

| Provider | Where to get a key | Notes |
|---|---|---|
| **Anthropic · Claude** (default) | [console.anthropic.com](https://console.anthropic.com/settings/keys) | Haiku, Sonnet and Opus already configured |
| **OpenAI** | [platform.openai.com](https://platform.openai.com/api-keys) | |
| **Google Gemini** | [aistudio.google.com](https://aistudio.google.com/apikey) | |
| **OpenRouter** | [openrouter.ai](https://openrouter.ai/keys) | One key for models from many providers |
| **Mistral · DeepSeek · Groq** | Each provider's console | Check that the model accepts images |
| **Ollama · LM Studio · vLLM** | No key needed | Free and private on your own GPU; lower quality than cloud models |

The prompts were written and tested with Claude, so analysis and editing quality may vary with other models.
**Settings** also shows what you have spent and how much is left of the budget you set.

> [!IMPORTANT]
> **Rights come first.** Before processing, Kairos asks for the rights status of each video (`own`,
> `written_permission`, `official_press` or `cc_licensed`) and some evidence. Downloading a video does not give you
> the right to publish it.

<br>

## Install

Two versions with the same interface: a **desktop app for Windows 11** and a **Docker app** that runs in your
browser on Linux, macOS, Windows or a server.

<p align="center">
  <a href="#windows-installer"><picture><source media="(prefers-color-scheme: dark)" srcset="docs/readme/installer-dark.png"><img src="docs/readme/installer-light.png" width="32%" alt="Windows installer"></picture></a>
  <a href="#portable-zip"><picture><source media="(prefers-color-scheme: dark)" srcset="docs/readme/portable-dark.png"><img src="docs/readme/portable-light.png" width="32%" alt="Portable ZIP"></picture></a>
  <a href="#docker"><picture><source media="(prefers-color-scheme: dark)" srcset="docs/readme/docker-dark.png"><img src="docs/readme/docker-light.png" width="32%" alt="Docker"></picture></a>
</p>

|  | **Desktop** (Windows) | **Docker** (web) |
|---|---|---|
| Runs on | Windows 11, nothing else to install | Linux, macOS, Windows or your own server |
| Used from | A regular Windows window | The browser, at `http://localhost:8000` |
| Render | NVIDIA (NVENC), AMD (AMF), Intel (Quick Sync) or CPU | CPU, or NVIDIA when the host exposes it |
| Extras | One-click tool installer | Command line, AI reviewer, Postgres |

### Windows installer

1. Download **`KairosStudio-<version>-Setup-win-x64.exe`** from the
   [latest release](https://github.com/Zntoff/kairos-studio/releases/latest).
2. Run it. It installs for your user only (no administrator rights), adds a Start menu entry and installs WebView2
   if it's missing.
3. Open **Kairos Studio** from the Start menu.

Updates install over the same version and keep your library. .NET, FFmpeg and the fonts are included.

### Portable ZIP

1. Download **`KairosStudio-<version>-Portable-win-x64.zip`** from the
   [latest release](https://github.com/Zntoff/kairos-studio/releases/latest).
2. Extract the **whole** ZIP to a folder you can write to, outside Program Files.
3. Open **`KairosStudio.exe`**.

This copy keeps its library and settings in a `data` folder beside the executable. To update it, replace the app
files and keep `data` and `portable.flag`.

> [!NOTE]
> Compare the downloads with `SHA256SUMS.txt` from the release. Until the builds are code-signed, Windows
> SmartScreen may warn about an unknown publisher; see [SECURITY.md](SECURITY.md#windows-signing-and-distribution).

#### First run on Windows

1. The **first-run setup** asks for the folder where generated videos go, your AI provider and your API key. You
   can skip both and set them later in **Settings**.
2. In **Settings → Tools**, install **transcription** (whisper.cpp and a multilingual model of about 200 MB).
3. Click **Create clip**, or add a video with its rights and click **Process**. Choose the moments, generate them
   and approve them in the queue.

Without an API key the app still works as a manual editor. **Settings → Performance and hardware** chooses between
automatic, CPU or a detected GPU; every encoder is tested before it's offered, and a failed GPU render retries on
the CPU. Your library lives in `%LOCALAPPDATA%\ClipperStudio` and API keys are encrypted for your Windows user
(DPAPI). Uninstalling keeps your data.

### Docker

You need [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Windows, macOS) or Docker Engine
(Linux). Python, FFmpeg and fonts are included in the image.

**Prebuilt image.** The quickest way: no build needed. Your library is saved in `./kairos-data`.

```bash
docker run -d --name kairos -p 127.0.0.1:8000:8000 -v "$PWD/kairos-data:/data" ghcr.io/zntoff/kairos-studio:latest
```

Update with `docker pull ghcr.io/zntoff/kairos-studio:latest`, then remove and start the container again; your data
stays in `./kairos-data`.

**From a release.** Download **`KairosStudio-<version>-Docker.zip`** from the
[latest release](https://github.com/Zntoff/kairos-studio/releases/latest), extract it and run the start script
(`Start-Kairos.ps1` on Windows, `start-kairos.sh` on Linux and macOS), or:

```bash
docker compose -f compose.yaml up -d --build
```

**From source.**

```bash
git clone https://github.com/Zntoff/kairos-studio.git
cd kairos-studio
cp .env.example .env          # Windows (PowerShell): Copy-Item .env.example .env
docker compose up --build
```

Open **http://localhost:8000**; the first build takes a few minutes.

1. In **Settings → AI provider**, choose the provider and paste your API key (or use the variables in `.env`, such
   as `ANTHROPIC_API_KEY` or `CLIPPER_AI_PROVIDER`). For Ollama on your computer, use
   `http://host.docker.internal:11434/v1`.
2. In **Videos**, upload a video (or put large files in `./input` and pick them there) and fill in its rights.
3. Click **Process**, choose the moments in **Jobs**, then review the clips in the **approval queue**.

Results are saved in `./data/out/`: for each clip and language, `clip.mp4`, `captions.srt` and `metadata.json`.

```bash
docker compose up -d                       # run in the background
docker compose logs -f                     # follow the log
docker compose down                        # stop (data stays in ./data)
git pull && docker compose up --build -d   # update
docker compose run --rm app --help         # command line
```

To render with an NVIDIA GPU on a host with Docker GPU support:
`docker compose -f docker-compose.yml -f docker-compose.gpu.yml up -d --build`.
Every other option (languages, number of clips, durations, Whisper model, Postgres) is documented in
[`.env.example`](.env.example).

> [!WARNING]
> The panel has no login. `docker-compose.yml` publishes it only on your computer (`127.0.0.1`). Don't expose it to
> the internet or a shared network without a reverse proxy with authentication.

<details>
<summary><b>Without Docker (Python)</b></summary>

You need Python 3.11 or later and FFmpeg with libass on the PATH.

```bash
python -m venv .venv && source .venv/bin/activate     # Windows: .venv\Scripts\activate
pip install -e .                                      # add [postgres] to use Postgres
cp .env.example .env                                  # then set your API key
clipper web                                           # panel at http://localhost:8000
```
</details>

<details>
<summary><b>Command line</b></summary>

```bash
clipper source add video.mp4 --rights own --evidence "my own recording" --language en
clipper analyze src_xxxx --analysis-model fast                       # propose moments
clipper generate job_xxxx --pick cand00,cand02 --model standard      # generate the chosen ones
clipper queue                                                        # approval queue
clipper approve <clip_id> --by <your-name>
clipper reject  <clip_id> --by <your-name> --note "needs context"
clipper run src_xxxx --offline        # the whole pipeline without AI, for testing
```

In Docker, prefix with `docker compose run --rm app` (for example, `docker compose run --rm app queue`).
</details>

<details>
<summary><b>Desktop app from source</b></summary>

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/Zntoff/kairos-studio.git
cd kairos-studio
powershell -ExecutionPolicy Bypass -File desktop\setup.ps1     # download FFmpeg and yt-dlp
dotnet run --project desktop\ClipperStudio                     # open the app
```

More in the [desktop development guide](desktop/README.md).
</details>

<br>

## Privacy

Kairos Studio has no servers of its own and no telemetry. It only connects to the AI provider you choose, with your
key, and, when you ask, to the sites you download videos from. More details in [SECURITY.md](SECURITY.md).

## Development

| Branch | Environment | Purpose |
|---|---|---|
| `dev` | Development | Day-to-day work and integration. Changes land here first. |
| `main` | Production | Only receives tested changes from `dev`. Every release is tagged here. |

Releases follow [semantic versioning](https://semver.org) with tags like `v0.10.0`. A tag on `main` builds the
installer, the portable ZIP and the Docker bundle, runs the tests and prepares a draft release; see
[CONTRIBUTING.md](CONTRIBUTING.md#releases). No test calls a real AI provider.

```bash
pip install -e ".[dev]" && pytest                                  # Python app (renders real video)
dotnet run --project desktop\ClipperStudio.Tests                    # desktop integration tests
```

<details>
<summary><b>Project structure</b></summary>

```
clipper/            web app and CLI (Python): subagents, render, panel
  agents/           analysis, editing, captions, metadata, reviewer, orchestrator
  media/            FFmpeg, transcription, ASS captions, audio, render
  prompts/          subagent prompts (shared with desktop)
  web/static/       interface and editor (shared with desktop)
desktop/            desktop app (C#, .NET 10, WPF + WebView2)
  ClipperStudio/    the app: Ai/, Engine/, Server/
  ClipperStudio.Tests/
  installer/        Inno Setup installer
assets/             fonts, sounds, style catalog (shared)
config/             AI providers and spoiler terms
distribution/       release guide, release notes and the Docker download bundle
docs/               architecture and the README graphics
scripts/            utilities: versioning, packaging, sounds, brand, demo video
tests/              Python app tests
release.ps1         builds the three release formats
```

How it works inside (subagents, contracts, EDL, output package): [docs/architecture.md](docs/architecture.md).
</details>

## Contributing

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) and, for security issues,
[SECURITY.md](SECURITY.md). Every version's changes are in [CHANGELOG.md](CHANGELOG.md).

## Support the project

Kairos Studio is free and open source. If it saves you time and you want it to keep growing, you can leave a tip:

<a href="https://ko-fi.com/K7D12AP6M0"><img src="https://ko-fi.com/img/githubbutton_sm.svg" alt="Support Kairos Studio on Ko-fi"></a>

## License

Copyright 2026 [ZNTOff](https://github.com/Zntoff). Released under the [Apache License 2.0](LICENSE): you can use,
modify and redistribute it, including commercially, as long as you keep the license notice and the
[NOTICE](NOTICE) file. The fonts, some sounds and the downloaded tools (FFmpeg, yt-dlp, whisper.cpp) have their own
licenses, also listed in [NOTICE](NOTICE).

Kairos Studio is not affiliated with Anthropic, OpenAI, Google, YouTube, TikTok, Instagram or the owners of the
content you clip. Publishing a clip is still your responsibility: check the rights and the editorial value first.

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="clipper/web/static/brand/mark-white.svg">
    <img src="clipper/web/static/brand/mark-black.svg" width="28" alt="Kairos">
  </picture>
  <br><sub><i>The exact moment.</i></sub>
</p>
