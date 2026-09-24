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
    public void MinimumMovieSizeRequiresMoreThanConfiguredGibibytes()
    {
        const long tenGb = 10L * 1073741824;
        Assert.False(CompressionPolicy.MeetsMinimumMovieSize(5L * 1073741824, 10));
        Assert.False(CompressionPolicy.MeetsMinimumMovieSize(tenGb, 10));
        Assert.True(CompressionPolicy.MeetsMinimumMovieSize(tenGb + 1, 10));
        Assert.True(CompressionPolicy.MeetsMinimumMovieSize(1, 0));
    }

    [Fact]
    public void SnapshotKeepsContainerAndCannotInheritLossyProfileOptions()
    {
        var profile = new EncodingProfile { VideoEncoder = "libx265", Crf = 24, AudioCodec = "aac", MaxWidth = 640,
            ResolutionMode = ResolutionMode.Unchanged, ExtraOutputArgs = "-sn", Container = "matroska" };
        var effective = CompressionPolicy.EffectiveProfile(profile, "movie.mp4");
        profile.Crf = 40;
        Assert.Equal(24, effective.Crf);
        Assert.Equal("mp4", effective.Container);
        Assert.Equal("copy", effective.AudioCodec);
        Assert.Equal(ResolutionMode.Unchanged, effective.ResolutionMode);
        Assert.Empty(effective.ExtraOutputArgs);
        Assert.Throws<InvalidOperationException>(() => CompressionPolicy.EffectiveProfile(profile, "movie.avi"));
        profile.ResolutionMode = ResolutionMode.CapWidth;
        Assert.Throws<InvalidOperationException>(() => CompressionPolicy.EffectiveProfile(profile, "movie.mp4"));
    }

    [Fact]
    public void ResolutionLimitDownscales4kButNeverUpscales720p()
    {
        var profile = CompressionPolicy.EffectiveProfile(new EncodingProfile
        {
            VideoEncoder = "libx265", Crf = 23, ResolutionMode = ResolutionMode.CapHeight, MaxHeight = 1080
        }, "movie.mkv");
        var fourK = new MediaProbeInfo { Width = 3840, Height = 2160, PixelFormat = "yuv420p" };
        var hd = new MediaProbeInfo { Width = 1280, Height = 720, PixelFormat = "yuv420p" };

        Assert.Equal(1080, profile.MaxHeight);
        Assert.Equal(1920, profile.MaxWidth);
        Assert.Contains("scale=w=1920:h=1080:force_original_aspect_ratio=decrease:force_divisible_by=2",
            CompressionPolicy.BuildArguments(profile, fourK, "in.mkv", "out.mkv"));
        Assert.DoesNotContain("-vf", CompressionPolicy.BuildArguments(profile, hd, "in.mkv", "out.mkv"));
    }

    [Fact]
    public void VerificationAcceptsOnlyTheConfiguredDownscale()
    {
        var profile = CompressionPolicy.EffectiveProfile(new EncodingProfile
        {
            VideoEncoder = "libx265", Crf = 23, ResolutionMode = ResolutionMode.CapHeight, MaxHeight = 1080
        }, "movie.mkv");
        var source = new MediaProbeInfo { Width = 3840, Height = 2160, DurationSeconds = 120, BitDepth = 8 };
        var output = new MediaProbeInfo { Width = 1920, Height = 1080, DurationSeconds = 120, BitDepth = 8,
            VideoCodec = "hevc", CompressorMarker = true };
        Assert.Null(CompressionPolicy.VerificationError(source, output, profile));
        output.Width = 1280;
        output.Height = 720;
        Assert.NotNull(CompressionPolicy.VerificationError(source, output, profile));
        output.Width = 3840;
        output.Height = 2160;
        Assert.NotNull(CompressionPolicy.VerificationError(source, output, profile));
        output.Width = 1922;
        output.Height = 1080;
        Assert.NotNull(CompressionPolicy.VerificationError(source, output, profile));

        source.Width = 1280;
        source.Height = 720;
        output.Width = 1280;
        output.Height = 720;
        Assert.Null(CompressionPolicy.VerificationError(source, output, profile));
        output.Width = 1920;
        output.Height = 1080;
        Assert.NotNull(CompressionPolicy.VerificationError(source, output, profile));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(4320)]
    public void ResolutionLimitRejectsUnsupportedHeights(int height)
    {
        Assert.Throws<InvalidOperationException>(() => CompressionPolicy.EffectiveProfile(new EncodingProfile
        {
            VideoEncoder = "libx265", Crf = 23, ResolutionMode = ResolutionMode.CapHeight, MaxHeight = height
        }, "movie.mkv"));
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
