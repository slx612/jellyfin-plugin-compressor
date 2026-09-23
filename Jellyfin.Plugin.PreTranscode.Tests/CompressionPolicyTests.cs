using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Safety;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class CompressionPolicyTests
{
    [Fact]
    public void AutomaticGateDoesNotBlockManualWork()
    {
        var config = new PluginConfiguration();
        Assert.False(CompressionPolicy.CanRun(new TranscodeJob { Automatic = true }, config));
        Assert.True(CompressionPolicy.CanRun(new TranscodeJob { Automatic = false }, config));
        config.AutomaticCompressionEnabled = true;
        Assert.True(CompressionPolicy.CanRun(new TranscodeJob { Automatic = true }, config));
        config.Enabled = false;
        Assert.False(CompressionPolicy.CanRun(new TranscodeJob(), config));
    }

    [Fact]
    public void SnapshotKeepsContainerAndCannotInheritLossyProfileOptions()
    {
        var profile = new EncodingProfile { VideoEncoder = "libx265", Crf = 24, AudioCodec = "aac", MaxWidth = 640,
            ResolutionMode = ResolutionMode.CapWidth, ExtraOutputArgs = "-sn", Container = "matroska" };
        var effective = CompressionPolicy.EffectiveProfile(profile, "movie.mp4");
        profile.Crf = 40;
        Assert.Equal(24, effective.Crf);
        Assert.Equal("mp4", effective.Container);
        Assert.Equal("copy", effective.AudioCodec);
        Assert.Equal(ResolutionMode.Unchanged, effective.ResolutionMode);
        Assert.Empty(effective.ExtraOutputArgs);
        Assert.Throws<InvalidOperationException>(() => CompressionPolicy.EffectiveProfile(profile, "movie.avi"));
    }

    [Fact]
    public void ValidationRejectsMissingTracksAndChangedChapterOrLanguage()
    {
        var source = new MediaProbeInfo { Width = 1920, Height = 1080, DurationSeconds = 120,
            PreservedStreams = new[] { "audio|aac|spa|default", "subtitle|subrip|eng|forced" },
            Chapters = new[] { new ChapterInfo(0, 120, "Start") } };
        var output = new MediaProbeInfo { Width = 1920, Height = 1080, DurationSeconds = 120, VideoCodec = "hevc",
            PreservedStreams = source.PreservedStreams, Chapters = source.Chapters, CompressorMarker = true };
        Assert.Null(CompressionPolicy.VerificationError(source, output));
        output.PreservedStreams = new[] { "audio|aac|spa|default" };
        Assert.NotNull(CompressionPolicy.VerificationError(source, output));
        output.PreservedStreams = source.PreservedStreams;
        output.Chapters = new[] { new ChapterInfo(0, 120, "Wrong title") };
        Assert.NotNull(CompressionPolicy.VerificationError(source, output));
    }

    [Fact]
    public void ProbeReadsMarkerStreamDispositionsAndChapters()
    {
        var info = MediaProber.Parse("""
        {"format":{"tags":{"JELLYFIN_COMPRESSOR":"v1"}},
         "streams":[{"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"spa"},"disposition":{"default":1,"forced":0}}],
         "chapters":[{"start_time":"0","end_time":"10.000","tags":{"title":"Intro"}}]}
        """, "movie.mkv");
        Assert.True(info.CompressorMarker);
        Assert.Contains("spa", Assert.Single(info.PreservedStreams));
        Assert.Equal("Intro", Assert.Single(info.Chapters).Title);
    }
}
