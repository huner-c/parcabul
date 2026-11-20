# ParcaBul

ParcaBul identifies songs playing on your Windows PC, including music in YouTube videos, Instagram Reels, and local MP4 files. It captures the default Windows audio output through WASAPI, fingerprints a short clip locally, and looks up the track through Shazam. No account or API key is required.

## Get started

1. Download `ParcaBul-Windows-x64.zip` from the [latest release](https://github.com/huner-c/parcabul/releases/latest).
2. Extract the ZIP, open `ParcaBul.exe`, and keep the `engine` folder next to it.
3. Play the video and click **Find song** or press **Ctrl + Alt + M**. ParcaBul captures about eight seconds of audio when it has no usable clip ready.
4. Enable **Instant capture** to keep the last ten seconds of audio in memory while the app is open, including when it’s minimized. A search can then use the audio that just played.

The app requires Windows x64, internet access, and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). A match shows the title, artist, and available listening links. If speech or other audio is playing over the music, try a clearer section of the video.

Audio stays in memory. When you start a search, the app sends an audio fingerprint to Shazam. Closing the app stops audio capture. Recognition uses [ShazamIO](https://github.com/shazamio/ShazamIO), an unofficial, community-maintained integration.

## Build from source

On Windows x64, install the .NET 10 SDK and Python with `pip`. Run the publish script from PowerShell.

```powershell
./Publish.ps1
```

The script downloads the embedded Python runtime and the pinned recognition dependencies, then creates `dist/ParcaBul-Windows-x64.zip`.
