using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Library;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Safety;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class CompressorHdrTests
{
    private const string DolbyVisionProbe = """
        {"format":{"duration":"120"},"streams":[{"codec_type":"video","codec_name":"hevc","width":3840,"height":2160,
        "pix_fmt":"yuv420p10le","color_primaries":"bt2020","color_transfer":"smpte2084","color_space":"bt2020nc","color_range":"tv",
        "side_data_list":[{"side_data_type":"Mastering display metadata","red_x":"34000/50000","max_luminance":"10000000/10000"},
        {"side_data_type":"Content light level metadata","max_content":1000,"max_average":400},
        {"side_data_type":"DOVI configuration record","dv_profile":8,"dv_bl_signal_compatibility_id":1,
        "rpu_present_flag":1,"el_present_flag":0,"bl_present_flag":1}]}]}
        """;

    private static readonly EncodingProfile CpuProfile = new() { VideoEncoder = "libx265" };

    [Fact]
    public void VerificationRejectsAnHdrOutputThatBecameSdr()
    {
        var source = new MediaProbeInfo { Width = 1920, Height = 1080, DurationSeconds = 120, BitDepth = 10, IsHdr = true };
        var output = new MediaProbeInfo { Width = 1920, Height = 1080, DurationSeconds = 120, BitDepth = 10,
            VideoCodec = "hevc", CompressorMarker = true, IsHdr = false };
        Assert.NotNull(CompressionPolicy.VerificationError(source, output));
    }

    [Fact]
    public void ProbeReadsDolbyVisionAndHdrMetadataUsedBySafetyChecks()
    {
        var source = MediaProber.Parse(DolbyVisionProbe, "movie.mkv");
        Assert.True(source.IsDolbyVision);
        Assert.Equal("bt2020", source.ColorPrimaries);
        Assert.Equal("smpte2084", source.ColorTransfer);
        Assert.Equal("bt2020nc", source.ColorSpace);
        Assert.Equal(8, source.DolbyVisionProfile);
        Assert.Equal(1, source.DolbyVisionCompatibilityId);
        Assert.True(source.DolbyVisionHasRpu);
        Assert.False(source.DolbyVisionHasEnhancementLayer);
        Assert.NotEmpty(source.MasteringDisplayMetadata);
        Assert.NotEmpty(source.ContentLightMetadata);
    }

    [Fact]
    public void DolbyVisionRequiresManualOptInCpuAndSupportedProfile()
    {
        var source = MediaProber.Parse(DolbyVisionProbe, "movie.mkv");
        var config = new PluginConfiguration { EnableExperimentalDolbyVision = true };
        Assert.Null(CompressionPolicy.EligibilityError(source, CpuProfile, config));
        Assert.NotNull(CompressionPolicy.EligibilityError(source, CpuProfile, config, automatic: true));
        Assert.NotNull(CompressionPolicy.EligibilityError(source, CpuProfile, config, manualSingle: false));
        Assert.NotNull(CompressionPolicy.EligibilityError(source, new EncodingProfile { VideoEncoder = "hevc_nvenc" }, config));
        Assert.NotNull(CompressionPolicy.EligibilityError(source, CpuProfile, new PluginConfiguration()));
        source.DolbyVisionProfile = 7;
        Assert.NotNull(CompressionPolicy.EligibilityError(source, CpuProfile, config));
    }

    [Fact]
    public void Hdr10RequiresManualOptInAndRejectsHdr10Plus()
    {
        var source = MediaProber.Parse(DolbyVisionProbe.Replace("\"side_data_type\":\"DOVI configuration record\"", "\"side_data_type\":\"Other\""), "movie.mkv");
        var config = new PluginConfiguration { EnableExperimentalHdr = true };
        Assert.Null(CompressionPolicy.EligibilityError(source, CpuProfile, config));
        Assert.NotNull(CompressionPolicy.EligibilityError(source, CpuProfile, config, automatic: true));
        source.HasHdr10Plus = true;
        Assert.NotNull(CompressionPolicy.EligibilityError(source, CpuProfile, config));
        Assert.Throws<InvalidOperationException>(() => CompressionPolicy.BuildArguments(CpuProfile, source, "in.mkv", "out.mkv"));
    }

    [Fact]
    public void DolbyVisionCommandRequiresRpuAndPreservesHdrSignal()
    {
        var source = MediaProber.Parse(DolbyVisionProbe, "movie.mkv");
        var profile = CompressionPolicy.EffectiveProfile(CpuProfile, "movie.mkv");
        var args = CompressionPolicy.BuildArguments(profile, source, "in.mkv", "out.mkv").ToArray();
        Assert.Equal("1", args[Array.IndexOf(args, "-dolbyvision") + 1]);
        Assert.Equal("bt2020", args[Array.IndexOf(args, "-color_primaries") + 1]);
        Assert.Equal("smpte2084", args[Array.IndexOf(args, "-color_trc") + 1]);
        Assert.Equal("bt2020nc", args[Array.IndexOf(args, "-colorspace") + 1]);
        Assert.Contains("vbv-maxrate=100000:vbv-bufsize=100000", args[Array.IndexOf(args, "-x265-params") + 1]);
    }

    [Fact]
    public void DolbyVisionDownscalingWaitsForAValidatedRpuPath()
    {
        var source = MediaProber.Parse(DolbyVisionProbe, "movie.mkv");
        var downscale = new EncodingProfile { VideoEncoder = "libx265", ResolutionMode = ResolutionMode.CapHeight,
            MaxWidth = 1920, MaxHeight = 1080 };
        Assert.NotNull(CompressionPolicy.EligibilityError(source, downscale,
            new PluginConfiguration { EnableExperimentalDolbyVision = true }));
        Assert.Throws<InvalidOperationException>(() => CompressionPolicy.BuildArguments(downscale, source, "in.mkv", "out.mkv"));
    }

    [Fact]
    public void VerificationRejectsLostRpuOrChangedStaticHdrMetadata()
    {
        var source = MediaProber.Parse(DolbyVisionProbe, "movie.mkv");
        var output = MediaProber.Parse(DolbyVisionProbe, "output.mkv");
        output.CompressorMarker = true;
        Assert.Null(CompressionPolicy.VerificationError(source, output));
        output.DolbyVisionHasRpu = false;
        Assert.NotNull(CompressionPolicy.VerificationError(source, output));
        output.DolbyVisionHasRpu = true;
        output.MasteringDisplayMetadata = "changed";
        Assert.NotNull(CompressionPolicy.VerificationError(source, output));
    }

    [Fact]
    public void EquivalentMasteringMetadataFractionsAreAccepted()
    {
        var source = MediaProber.Parse(DolbyVisionProbe, "movie.mkv");
        var output = MediaProber.Parse(DolbyVisionProbe.Replace("34000/50000", "17/25")
            .Replace("10000000/10000", "1000/1"), "output.mkv");
        output.CompressorMarker = true;
        Assert.Null(CompressionPolicy.VerificationError(source, output));
    }

    [Fact]
    public void UnspecifiedSourceColorRangeMayBecomeExplicitLimitedRange()
    {
        var source = MediaProber.Parse(DolbyVisionProbe.Replace("\"color_range\":\"tv\",", ""), "movie.mkv");
        var output = MediaProber.Parse(DolbyVisionProbe, "output.mkv");
        output.CompressorMarker = true;
        Assert.Null(CompressionPolicy.VerificationError(source, output));
    }

    [Fact]
    public void JellyfinCatalogHdr10PlusFlagBlocksExperimentalCompression()
    {
        var source = MediaProber.Parse(DolbyVisionProbe, "movie.mkv");
        CompressionCoordinator.MergeLibraryHdrFacts(source, new[]
        {
            new MediaStream { Type = MediaStreamType.Video, Hdr10PlusPresentFlag = true }
        });
        Assert.NotNull(CompressionPolicy.EligibilityError(source, CpuProfile,
            new PluginConfiguration { EnableExperimentalDolbyVision = true }));
    }

    [Fact]
    public void JellyfinCatalogDolbyVisionFlagPreventsSilentHdrOnlyOutput()
    {
        var source = MediaProber.Parse(DolbyVisionProbe.Replace("\"side_data_type\":\"DOVI configuration record\"", "\"side_data_type\":\"Other\""), "movie.mkv");
        CompressionCoordinator.MergeLibraryHdrFacts(source, new[]
        {
            new MediaStream { Type = MediaStreamType.Video, DvProfile = 8 }
        });
        Assert.True(source.IsDolbyVision);
        Assert.NotNull(CompressionPolicy.EligibilityError(source, CpuProfile,
            new PluginConfiguration { EnableExperimentalHdr = true, EnableExperimentalDolbyVision = true }));
    }

    [Fact]
    public void FrameLogDistinguishesARealHdr10PlusFrameFromAnEmptyScan()
    {
        var header = new[] { "#format: frame checksums", "#stream#, dts, pts, duration, size, hash" };
        Assert.Equal(0, DynamicHdrDetector.CountFrames(header));
        Assert.Equal(1, DynamicHdrDetector.CountFrames(header.Append("0, 0, 0, 1, 18478080, abcdef")));
    }

    [Fact]
    public void Hdr10PlusCatalogFlagRejectsEvenWhenColorTagsAreMissing()
    {
        var source = new MediaProbeInfo { VideoStreamCount = 1, Width = 1920, Height = 1080,
            DurationSeconds = 120, PixelFormat = "yuv420p10le", BitDepth = 10, HasHdr10Plus = true };
        Assert.NotNull(CompressionPolicy.EligibilityError(source, CpuProfile));
    }

    [Fact]
    public void FrameOnlyRpuCannotSilentlyTakeTheHdr10Route()
    {
        var source = MediaProber.Parse(DolbyVisionProbe.Replace("\"side_data_type\":\"DOVI configuration record\"", "\"side_data_type\":\"Other\""), "movie.mkv");
        Assert.Null(DynamicHdrDetector.ApplyFacts(source, new DynamicHdrFacts(0, 50, 50, 50)));
        Assert.True(source.IsDolbyVision);
        Assert.NotNull(CompressionPolicy.EligibilityError(source, CpuProfile,
            new PluginConfiguration { EnableExperimentalHdr = true, EnableExperimentalDolbyVision = true }));
    }

    [Fact]
    public void FrameOnlyStaticHdrMetadataMustNotBeLost()
    {
        var source = MediaProber.Parse(DolbyVisionProbe.Replace("\"side_data_type\":\"Mastering display metadata\"", "\"side_data_type\":\"Other\""), "movie.mkv");
        Assert.NotNull(DynamicHdrDetector.ApplyFacts(source, new DynamicHdrFacts(0, 50, 50, 50)));
    }

    [Fact]
    public void StaticFrameHdrWithoutColorSignalIsRejected()
    {
        var source = new MediaProbeInfo { BitDepth = 10, MasteringDisplayMetadata = "present" };
        Assert.NotNull(DynamicHdrDetector.ApplyFacts(source, new DynamicHdrFacts(0, 0, 50, 0)));
    }

    [Fact]
    public void StaticStreamHdrWithoutColorSignalIsRejected()
    {
        var source = new MediaProbeInfo { VideoStreamCount = 1, Width = 1920, Height = 1080,
            DurationSeconds = 120, PixelFormat = "yuv420p10le", BitDepth = 10,
            MasteringDisplayMetadata = "present" };
        Assert.NotNull(CompressionPolicy.EligibilityError(source, CpuProfile));
    }

    [Fact]
    public void OutputMustRetainFrameByFrameRpuAndStaticHdrCounts()
    {
        var source = new DynamicHdrFacts(0, 50, 50, 50);
        Assert.Null(DynamicHdrDetector.PreservationError(source, new DynamicHdrFacts(0, 50, 50, 50)));
        Assert.NotNull(DynamicHdrDetector.PreservationError(source, new DynamicHdrFacts(0, 49, 50, 50)));
        Assert.NotNull(DynamicHdrDetector.PreservationError(source, new DynamicHdrFacts(0, 50, 49, 50)));
    }

    [Fact]
    public async Task RealFrameScanFinishesWithNoDynamicMetadataOnSyntheticVideo()
    {
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg");
        if (ffmpeg is null) return;
        var directory = Directory.CreateTempSubdirectory("compressor-hdr-scan-").FullName;
        var source = Path.Combine(directory, "source.mkv");
        try
        {
            await ProcessRunner.RunAsync(ffmpeg, new[] { "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=12", "-t", "1", "-c:v", "libx265",
                "-preset", "ultrafast", "-pix_fmt", "yuv420p10le", source }, 60_000, default);
            Assert.True(File.Exists(source));
            var facts = await DynamicHdrDetector.ScanAsync(ffmpeg, source, directory, 1, default);
            Assert.Equal(new DynamicHdrFacts(0, 0, 0, 0), facts);
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            Directory.Delete(directory);
        }
    }
}
