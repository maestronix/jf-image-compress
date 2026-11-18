using System.Collections.Concurrent;
using System.Text.Json;
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
        var processedState = await LoadProcessedStateAsync(cancellationToken).ConfigureAwait(false);

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
                    var fingerprint = GetFingerprint(entry.Path);
                    var alreadyProcessed = fingerprint is not null &&
                        processedState.TryGetValue(entry.Path, out var previousFingerprint) &&
                        string.Equals(fingerprint, previousFingerprint, StringComparison.Ordinal);

                    if (!alreadyProcessed)
                    {
                        var result = await ProcessImageAsync(entry.Path, entry.IsPerson, token).ConfigureAwait(false);
                        if (result.Attempted)
                        {
                            var newFingerprint = GetFingerprint(entry.Path);
                            if (newFingerprint is not null)
                            {
                                processedState[entry.Path] = newFingerprint;
                            }
                        }

                        if (result.SavedBytes > 0)
                        {
                            Interlocked.Increment(ref converted);
                            Interlocked.Add(ref savedBytes, result.SavedBytes);
                        }
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

        await SaveProcessedStateAsync(processedState, cancellationToken).ConfigureAwait(false);

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

    private string ProcessedStatePath => Path.Combine(_applicationPaths.PluginConfigurationsPath, "ImageCompress", "processed-images.json");

    private async Task<ConcurrentDictionary<string, string>> LoadProcessedStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(ProcessedStatePath))
            {
                return new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var json = await File.ReadAllTextAsync(ProcessedStatePath, cancellationToken).ConfigureAwait(false);
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return new ConcurrentDictionary<string, string>(
                stored ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not load Image Compress processing state; images may be checked again.");
            return new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task SaveProcessedStateAsync(
        ConcurrentDictionary<string, string> state,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(ProcessedStatePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = ProcessedStatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(state);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, ProcessedStatePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string? GetFingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ProcessResult> ProcessImageAsync(string path, bool isPerson, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new ProcessResult(0, false);
        }

        var originalLength = new FileInfo(path).Length;
        var info = await Image.IdentifyAsync(path, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return new ProcessResult(0, false);
        }

        var maxWidth = isPerson ? 400 : int.MaxValue;
        var maxHeight = isPerson ? int.MaxValue : 900;

        // Never re-encode images already within their applicable dimensions.
        if (info.Width <= maxWidth && info.Height <= maxHeight)
        {
            return new ProcessResult(0, false);
        }

        var scale = Math.Min(
            Math.Min((double)maxWidth / info.Width, (double)maxHeight / info.Height),
            1d);

        var targetWidth = Math.Max(1, (int)Math.Round(info.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(info.Height * scale));
        var format = info.Metadata.DecodedImageFormat;
        if (format is null)
        {
            return new ProcessResult(0, false);
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
                return new ProcessResult(0, true);
            }

            // The temporary file is in the same directory, so replacement is an atomic rename.
            File.Move(temporaryPath, path, true);
            return new ProcessResult(originalLength - compressedLength, true);
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

    private sealed record ProcessResult(long SavedBytes, bool Attempted);
}