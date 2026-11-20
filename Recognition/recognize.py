import asyncio
import json
import sys

from aiohttp import ClientError
from aiohttp_retry import ExponentialRetry
from shazamio import HTTPClient, Shazam


async def recognize(audio):
    async with HTTPClient(retry_options=ExponentialRetry(attempts=2)) as client:
        shazam = Shazam(http_client=client, endpoint_country="TR")
        result = await shazam.recognize(audio)

    track = result.get("track")
    if not track:
        return None

    metadata = next(
        (section.get("metadata", []) for section in track.get("sections", []) if section.get("type") == "SONG"),
        [],
    )
    album = next((item.get("text") for item in metadata if item.get("title") == "Album"), None)
    apple_music = None
    for option in track.get("hub", {}).get("options", []):
        for action in option.get("actions", []):
            uri = action.get("uri") or ""
            if uri.startswith("https://music.apple.com/"):
                apple_music = uri
                break

    return {
        "title": track.get("title"),
        "artist": track.get("subtitle"),
        "album": album,
        "url": track.get("url"),
        "appleMusic": apple_music,
        "cover": track.get("images", {}).get("coverart"),
    }


def main():
    try:
        audio = sys.stdin.buffer.read()
        track = asyncio.run(asyncio.wait_for(recognize(audio), timeout=15))
        print(json.dumps({"track": track}))
    except (ClientError, TimeoutError, OSError):
        print('{"error":"network"}')
    except (ValueError, RuntimeError):
        print('{"error":"audio"}')


if __name__ == "__main__":
    main()
