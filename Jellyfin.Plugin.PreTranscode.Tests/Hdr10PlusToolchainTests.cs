using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Safety;
using Jellyfin.Plugin.PreTranscode.Configuration;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class Hdr10PlusToolchainTests
{
    [Fact]
    public void MetadataComparisonRequiresExactBytes()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "source.json");
            var same = Path.Combine(dir, "same.json");
            var changed = Path.Combine(dir, "changed.json");
            File.WriteAllText(source, "{\"SceneInfo\":[{\"SceneFrameIndex\":0}]}\n");
            File.Copy(source, same);
            File.WriteAllText(changed, "{\"SceneInfo\":[{\"SceneFrameIndex\":1}]}\n");
            Assert.True(Hdr10PlusToolchain.MetadataMatches(source, same));
            Assert.False(Hdr10PlusToolchain.MetadataMatches(source, changed));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RemuxCopiesVideoLanguageTitleAndFlags()
    {
        const string identify = """
            {"tracks":[{"type":"video","properties":{"language":"eng","track_name":"Movie",
            "default_track":true,"forced_track":false,"enabled_track":true,"flag_original":true}}]}
            """;
        var args = Hdr10PlusToolchain.BuildMuxArguments(identify, "injected.hevc", "encoded.mkv", "final.mkv", "timestamps.txt");
        Assert.Contains("0:eng", args);
        Assert.Contains("0:Movie", args);
        Assert.Contains("--original-flag", args);
        Assert.Equal("--no-video", args[^2]);
        Assert.Equal("encoded.mkv", args[^1]);
    }

    [Fact]
    public async Task TrialClipPreservesHdr10PlusAndNonVideoPackets()
    {
        var trial = Environment.GetEnvironmentVariable("JELLYFIN_HDR10PLUS_TRIAL_DIR");
        var ffmpeg = Environment.GetEnvironmentVariable("JELLYFIN_HDR10PLUS_FFMPEG");
        if (string.IsNullOrEmpty(trial) || string.IsNullOrEmpty(ffmpeg)) return;
        var output = Path.Combine(trial, "plugin-orchestration-test.mkv");
        try
        {
            await Hdr10PlusToolchain.RunWithToolsAsync(
                Path.Combine(trial, "querido-source-all-chapters.mkv"),
                Path.Combine(trial, "querido-encoded-all.mkv"), output, ffmpeg, trial,
                Guid.NewGuid().ToString("N"), 4.1,
                Path.Combine(trial, "hdr10plus_tool.exe"),
                Path.Combine(trial, "mkvtoolnix", "mkvtoolnix", "mkvmerge.exe"),
                Path.Combine(trial, "mkvtoolnix", "mkvtoolnix", "mkvextract.exe"),
                false, default, null);
            Assert.True(new FileInfo(output).Length > 0);
            var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
            var original = Path.Combine(trial, "querido-source-all-chapters.mkv");
            var sourceInfo = MediaProber.Parse(await ProcessRunner.RunAsync(ffprobe,
                MediaProber.BuildProbeArguments(original), 60000, default), original);
            var outputInfo = MediaProber.Parse(await ProcessRunner.RunAsync(ffprobe,
                MediaProber.BuildProbeArguments(output), 60000, default), output);
            Assert.Null(CompressionPolicy.VerificationError(sourceInfo, outputInfo,
                new EncodingProfile { VideoEncoder = "libx265" }));
            var sourceFacts = await DynamicHdrDetector.ScanAsync(ffmpeg, original, trial, 4.1, default);
            var outputFacts = await DynamicHdrDetector.ScanAsync(ffmpeg, output, trial, 4.1, default);
            Assert.Equal(97, outputFacts.TotalFrames);
            Assert.Null(DynamicHdrDetector.PreservationError(sourceFacts, outputFacts));
        }
        finally { File.Delete(output); }
    }
}
