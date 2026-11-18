using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ImageCompress;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        MaxConcurrency = Math.Clamp(Environment.ProcessorCount, 1, 64);
    }

    public int MaxConcurrency { get; set; }
    public long MetadataImagesBytes { get; set; }
    public long TotalConvertedImages { get; set; }
    public long TotalSavedBytes { get; set; }
    public long LastRunConvertedImages { get; set; }
    public long LastRunSavedBytes { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
}