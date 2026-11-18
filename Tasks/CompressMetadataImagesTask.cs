using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Jellyfin.Plugin.ImageCompress.Tasks;

public sealed class CompressMetadataImagesTask : IScheduledTask
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff"
    };

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<CompressMetadataImagesTask> _logger;

    public CompressMetadataImagesTask(
        IApplicationPaths applicationPaths,
        ILibraryManager libraryManager,
        ILogger<CompressMetadataImagesTask> logger)
    {
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "Compress metadata images";
    public string Key => "ImageCompressMetadataImages";
    public string Description => "Resizes oversized movie, series, season and person images.";
    public string Category => Plugin.Instance?.Name ?? "Image Compress";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var maxConcurrency = Math.Clamp(config.MaxConcurrency, 1, 64);
        var imagePaths = CollectImagePaths(cancellationToken);

        long converted = 0;
        long savedBytes = 0;
        long completed = 0;
        long errors = 0;

        await Parallel.ForEachAsync(
            imagePaths,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cancellationToken
            },
            async (entry, token) =>
            {
                try
                {
                    var saved = await ProcessImageAsync(entry.Path, entry.IsPerson, token).ConfigureAwait(false);
                    if (saved > 0)
                    {
                        Interlocked.Increment(ref converted);
                        Interlocked.Add(ref savedBytes, saved);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errors);
                    _logger.LogDebug(ex, "Could not process metadata image {ImagePath}", entry.Path);
                }

                var current = Interlocked.Increment(ref completed);
                if (current % 25 == 0 || current == imagePaths.Count)
                {
                    progress.Report(imagePaths.Count == 0 ? 100 : current * 100d / imagePaths.Count);
                }
            }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var currentSize = imagePaths.Sum(entry =>
        {
            try
            {
                return File.Exists(entry.Path) ? new FileInfo(entry.Path).Length : 0L;
            }
            catch
            {
                return 0L;
            }
        });

        config.MetadataImagesBytes = currentSize;
        config.LastRunConvertedImages = converted;
        config.LastRunSavedBytes = savedBytes;
        config.TotalConvertedImages += converted;
        config.TotalSavedBytes += savedBytes;
        config.LastRunAtUtc = DateTime.UtcNow;

        Plugin.Instance?.SaveConfiguration();
        progress.Report(100);

        _logger.LogInformation(
            "Image Compress finished: {Converted} images converted, {SavedBytes} bytes saved, {Errors} errors.",
            converted,
            savedBytes,
            errors);
    }

    private List<ImagePath> CollectImagePaths(CancellationToken cancellationToken)
    {
        var paths = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var metadataPath = Path.Combine(_applicationPaths.DataPath, "metadata");
        AddDirectory(metadataPath, paths, cancellationToken);
        AddDirectory(Path.Combine(metadataPath, "People"), paths, cancellationToken, isPerson: true);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes =
            [
                BaseItemKind.Movie,
                BaseItemKind.Series,
                BaseItemKind.Season,
                BaseItemKind.Person
            ]
        });

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isPerson = item is Person;
            foreach (var image in item.ImageInfos)
            {
                if (!string.IsNullOrWhiteSpace(image.Path) &&
                    SupportedExtensions.Contains(Path.GetExtension(image.Path)))
                {
                    AddPath(paths, image.Path, isPerson);
                }
            }
        }

        return paths.Select(pair => new ImagePath(pair.Key, pair.Value)).ToList();
    }

    private static void AddDirectory(
        string? root,
        ConcurrentDictionary<string, bool> paths,
        CancellationToken cancellationToken,
        bool isPerson = false)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                root,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SupportedExtensions.Contains(Path.GetExtension(path)))
                {
                    AddPath(paths, path, isPerson || IsPeoplePath(path));
                }
            }
        }
        catch (IOException)
        {
            // Inaccessible paths are skipped; registered library image paths are collected separately.
        }
        catch (UnauthorizedAccessException)
        {
            // Inaccessible paths are skipped.
        }
    }

    private static void AddPath(ConcurrentDictionary<string, bool> paths, string path, bool isPerson)
    {
        if (paths.TryAdd(path, isPerson) || !isPerson)
        {
            return;
        }

        paths[path] = true;
    }

    private static bool IsPeoplePath(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("People", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<long> ProcessImageAsync(string path, bool isPerson, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var originalLength = new FileInfo(path).Length;
        var info = await Image.IdentifyAsync(path, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return 0;
        }

        var maxWidth = isPerson ? 400 : int.MaxValue;
        var maxHeight = isPerson ? int.MaxValue : 900;

        // Never re-encode images already within their applicable dimensions.
        if (info.Width <= maxWidth && info.Height <= maxHeight)
        {
            return 0;
        }

        var scale = Math.Min(
            Math.Min((double)maxWidth / info.Width, (double)maxHeight / info.Height),
            1d);

        var targetWidth = Math.Max(1, (int)Math.Round(info.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(info.Height * scale));
        var format = info.Metadata.DecodedImageFormat;
        if (format is null)
        {
            return 0;
        }

        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using var image = await Image.LoadAsync(path, cancellationToken).ConfigureAwait(false);
            image.Mutate(operation => operation.Resize(targetWidth, targetHeight));

            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 64,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (format.Name.Equals("JPEG", StringComparison.OrdinalIgnoreCase))
                {
                    await image.SaveAsync(output, new JpegEncoder { Quality = 85 }, cancellationToken).ConfigureAwait(false);
                }
                else if (format.Name.Equals("WEBP", StringComparison.OrdinalIgnoreCase))
                {
                    await image.SaveAsync(output, new WebpEncoder { Quality = 85 }, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await image.SaveAsync(output, format, cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var compressedLength = new FileInfo(temporaryPath).Length;
            if (compressedLength >= originalLength)
            {
                return 0;
            }

            // The temporary file is in the same directory, so replacement is an atomic rename.
            File.Move(temporaryPath, path, true);
            return originalLength - compressedLength;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record ImagePath(string Path, bool IsPerson);
}