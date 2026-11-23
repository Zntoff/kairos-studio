# Changelog

All notable changes to Kairos Studio are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The desktop and Docker apps share the interface,
the prompts and the style catalog, and always carry the same version number.

## [Unreleased]

## [0.12.0-beta.11] - 2026-11-23

- Create and edit full-length video projects with shared Shorts tools, reusable safe areas, an editing assistant and independent timelines and exports.
- Enter the composition duration in seconds, minutes or hours. Adjacent clips transition without black frames in preview and export, while intentional gaps remain visible.
- Improved word timing for transcribed Shorts, with editable subtitles and local alignment.
- Try the guided Shorts demo under Home, from importing a sample through downloading a vertical MP4.
- Windows packaging verifies relative paths and file hashes before distribution. Docker runs on Ubuntu 24.04 LTS with the available libxml2 security update.
- Release builds scan Git history for secrets and audit Python, NuGet and OS dependencies before drafting the release.

- Prominent direct downloads for all three formats on the repository homepage, with version-synchronized links.
- Disable automatic AI co-author and pull request attribution in the project settings.

## [0.10.0]

First public release, from the project's new home at
[Zntoff/kairos-studio](https://github.com/Zntoff/kairos-studio).

- Three formats: Docker, a per-user EXE installer and a portable ZIP for Windows x64.
- Prebuilt Docker image on GitHub Packages: `ghcr.io/zntoff/kairos-studio`.
- Installer in five languages, with shortcuts, upgrades through a stable AppId, and an uninstaller that keeps
  user data.
- Isolated portable mode through `portable.flag`, with the library and profile beside the executable.
- The installer checks for WebView2; prerequisite downloads have their signatures validated.
- A single release process with synchronized versions, hashes, manifests and blocking of private files.
- Tag-triggered workflow that builds the three packages, runs the tests and prepares a draft release.
- `dev` branch for development and `main` for production, with continuous integration on both and on every pull
  request: version check, Python tests and desktop tests.
- Documentation, comments, CLI text and offline-mode placeholders in English, and a new README in the Kairos
  identity.

## [0.9.1]

- Translation review of the whole shared interface: English, Spanish, Portuguese, French and German.
- Translations for video cards, forms, dialogs, the editor, APIs, tools and dynamic states; dates and numbers
  follow the language.
- Project, file and model names, transcripts and video content keep their original language.
- Selector values stay the same when their labels are translated; the language preference persists and syncs
  across tabs.
- Native desktop windows localized with the same catalog; automatic checks for coverage and translation markers.

## [0.9.0]

- Deletion of shorts, projects, videos and resources, with confirmation and recovery of managed files.
- Import next to the search box: YouTube/Twitch, local videos and supporting files.
- Local API protected by an HttpOnly, SameSite Strict session, CSRF and Host/Origin validation.
- Secrets filtered from responses and errors; CSP, anti-framing and disabled private caching.
- Packaging without a compressed self-extracting executable, with integrity hashes and exclusion of private files.
- Authenticode signing with a certificate or Microsoft Artifact Signing; `-RequireSignature` blocks publishing
  without a signature.

## [0.8.1]

- Editor: drag the hook, notes and captions; the position persists in the draft and the render, with undo.
- Safe area centered horizontally, with symmetric side margins on desktop.
- English interface by default, with Spanish, Portuguese, French and German.
- Language selector in the sidebar and in Settings. The preference is local and independent of the languages and
  texts of the videos.

## [0.8.0]

- Jobs with thumbnails, video name, filters and grouping by project.
- Adaptive preview with video, review and separate suggestions for YouTube, Instagram and TikTok.
- Copy titles, descriptions, hashtags and the full content per platform and language.
- APIs and models panel, quick provider switch and a catalog to assign models per tier.
- Live Anthropic model catalog on desktop and in the container.
- New AI edit also available in Docker: reuses the analysis and keeps the original clip.

## [0.7.1]

- Multi-model integration verified and packaged for Windows and Docker.
- Fixed desktop analysis with local providers and no Anthropic key.
- Each run keeps its chosen provider, models and credentials, including while it waits in the queue.
- Models without tool calling: JSON fallback with contract validation and usage logging.
- Costs and grouping separated per provider, even when model names match.
- Fixed overflow in the AI panel and late responses after switching providers.

## 0.6.0 – 0.7.0

- **Multi-model (0.7.0).** Besides Claude, Kairos works with OpenAI, Google Gemini, OpenRouter, Mistral,
  DeepSeek, Groq, Ollama and any server compatible with the OpenAI API (LM Studio, vLLM).
  - Shared catalog in `config/providers.json`; Settings lets you choose provider, key, server address, model
    per tier (loaded from the provider) and prices for the usage panel.
  - One key per provider: encrypted with DPAPI on desktop and owner-only permissions in Docker. The Anthropic key
    stays where it was, so existing installs lose nothing.
  - OpenAI-compatible client in both apps: forced function calling and, depending on what the server accepts,
    unforced, without frames (models without vision) or with JSON written as text. Schemas adapted for Gemini.
  - Usage per provider and model; calls to models without a price are counted but don't add to the spend.
  - Hints for short keys no longer reveal most of the key.
- Home dashboard with logo, project search and shortcuts to define a project, add a video or generate a short.
- Sidebar split into project, navigation and settings/usage; persistent compact mode and a keyboard-friendly
  mobile menu.
- Private supporting files per project (images, audio and documents), with preview and download; 100 MB limit.
- New video imports separated by project, and AI hook context restricted to the project.
- Protection against late responses when switching projects or closing the editor; forms and dialogs keep their
  context.
- Kairos Studio identity: geometric symbol, line wordmark, favicon, Windows icon and splash screen.
- Inter, Instrument Serif and JetBrains Mono bundled locally with their SIL OFL licenses.
- Dark, light and system themes in Settings; persistent preference and logo variants per theme.
- `KairosStudio.exe` executable, version 0.6.1; previous data paths are kept.
- Editor preview resized through ResizeObserver when its available space changes.
- Projects on desktop and in the container, with separate videos, generations and clips; migration of the
  previous library to General and moving videos together with their results.
- Responsive navigation and forms; editor tools and properties accessible on narrow screens.
- Estimated remaining credit in the sidebar, updated from the local usage log.
- GPU support integrated (formerly `desktop/gpu-compat`): DXGI detection, real NVENC/AMF/QSV tests, GPU or CPU
  selection, persistent preferences and CPU fallback when a GPU rejects a render.
- Container: choice between the CPU and the encoders that actually respond inside Docker.
- Apache 2.0 license, `NOTICE` with third-party licenses, contribution guide and security policy.
- General-purpose clipping: the prompts (analysis v2, editing v3, captions v3, metadata v2, reviewer v2, desktop
  editing v2) work for any long video, not just gameplay.

## Desktop 0.4.1

- Face regions keep their aspect ratio instead of being forced into a square; the inset sits at the top and
  leaves room for the hook and the notes.
- Automatic framing uses the main visible region; if the essentials don't fit vertically, it keeps the whole video.
- **New AI edit** asks for the framing again instead of inheriting the previous crop.
- The vertical crop lets you adjust the horizontal and vertical position, and shows how much width is lost.
- Three CC0 sound accents from Freesound (record scratch, ba-dum-tss, air horn), with their provenance in
  `assets/sfx/THIRD_PARTY.md`. Every export includes `sound_licenses.json`.

## Desktop 0.4.0

- Creative editing: the editing subagent receives 12 frames and decides pacing, commentary, sounds, zooms,
  reactions and animated titles for each moment.
- Enlarged single-face inset (fixed region, no tracking), adjustable in the editor.
- **New AI edit** on an already generated clip, keeping the previous version.

## Desktop 0.3

- First-run setup: folder for generated videos and API key (can be skipped and done in Settings).
- Full flow with Claude: transcription with whisper.cpp, analysis, choosing moments, generation, approval queue.
- Jobs survive closing the app: **Resume** and **Generate more** without repeating the analysis.
- Fast or accurate transcription.
- 0.3.1: the analysis tells segment indices from seconds apart; it keeps valid proposals and logs the discarded ones.

## Docker 0.1

- Web panel and CLI: rights registration, analysis, editing, captions, metadata and reviewer with Claude subagents.
- 9:16 render with word-by-word captions, effects, color, sounds and loudness normalization.
- Timeline editor, per-person API key and usage panel.

[Unreleased]: https://github.com/Zntoff/kairos-studio/compare/v0.10.0...dev
[0.10.0]: https://github.com/Zntoff/kairos-studio/releases/tag/v0.10.0
