# Jellyfin Image Compress

A Jellyfin 12.4 plugin that reduces oversized metadata artwork while leaving already-small images untouched.

## Processing rules

- Movie, series and season artwork: maximum height **900 px**; aspect ratio is preserved.
- Person/cast artwork: maximum width **400 px**; aspect ratio is preserved.
- JPEG and WebP output use quality **85**; PNG remains lossless. Processing uses Jellyfin's built-in Skia encoder, so no extra image-processing runtime is bundled. Other input formats that Jellyfin cannot encode back into the same format are left untouched.
- The original is replaced only when the new file is smaller.
- The replacement is written to a temporary file in the same directory and then atomically swapped into place.
- Images already within their applicable dimension limit are never re-encoded.
- If an oversized image cannot be made smaller, its file size and modification time are recorded so the same unchanged file is not encoded again on the next daily run. Changed files are eligible again.

The task discovers images in Jellyfin's metadata and people folders and from the image paths registered on movie, series, season and person library items.

## Scheduled task and settings

The task is registered in **Dashboard → Scheduled Tasks** and runs daily at 03:00 by default.

The plugin configuration page exposes maximum concurrency (1–64; default: CPU core count) and displays the current metadata image size, images converted, and storage saved for the last run and in total.

## Build and release

The GitHub Actions CI workflow builds the plugin against Jellyfin 12.4 and validates meta.json and manifest.json.

To publish a release, push a tag matching the version in meta.json, for example:

    git tag v1.0.0.0
    git push origin v1.0.0.0

The release workflow builds the plugin, creates the release ZIP, publishes a GitHub release, and adds the version, download URL and checksum to manifest.json.

Plugin repository manifest URL:

https://raw.githubusercontent.com/maestronix/jf-image-compress/main/manifest.json
