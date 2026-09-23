using Jellyfin.Plugin.PreTranscode.Configuration;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class CompressorDefaultsTests
{
    [Fact]
    public void InstallationRequiresExplicitSelectionAndRetention()
    {
        var config = new PluginConfiguration();
        ConfigurationInitializer.Normalize(config);
        Assert.False(config.AutomaticCompressionEnabled);
        Assert.Empty(config.IncludedFolders);
        Assert.Equal(0, config.RetentionDays);
        Assert.Equal(0, config.QuarantineMaxBytes);
        Assert.Empty(config.QuarantineDirectory);
        Assert.Equal(1, config.MaxConcurrentJobs);
        var profile = Assert.Single(config.Profiles);
        Assert.Equal("hevc", profile.VideoCodec);
        Assert.Equal("copy", profile.AudioCodec);
        Assert.Equal("matroska", profile.Container);
    }
}
