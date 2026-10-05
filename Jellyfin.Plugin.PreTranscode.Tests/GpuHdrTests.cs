using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class GpuHdrTests
{
    [Fact]
    public async Task SilentHdr10MovieCanBeReencodedWithoutComparingVideoAsAudio()
    {
        var toolRoot = Environment.GetEnvironmentVariable("JELLYFIN_GPU_HDR_TOOLS");
        var mkvRoot = Environment.GetEnvironmentVariable("JELLYFIN_GPU_HDR_MKVTOOLS");
        if (string.IsNullOrEmpty(toolRoot) || string.IsNullOrEmpty(mkvRoot)) return;
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg")!;
        var ffprobe = FfmpegTestBinaries.Find("ffprobe")!;
        var dir = Directory.CreateTempSubdirectory("gpu-hdr-silent-").FullName;
        try
        {
            var original = Path.Combine(dir, "source.mkv");
            var work = Path.Combine(dir, "work");
            var encoded = Path.Combine(work, "encoded.mkv");
            var output = Path.Combine(dir, "output.mkv");
            var hdr = "pools=1:frame-threads=1:master-display=G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)L(10000000,50):max-cll=1158,394";
            await ProcessRunner.RunAsync(ffmpeg, new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=128x96:rate=12",
                "-t", "1", "-c:v", "libx265", "-pix_fmt", "yuv420p10le", "-x265-params", hdr,
                "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", original }, 60000, default);
            var prober = new MediaProber(Mock.Of<IMediaEncoder>(e => e.ProbePath == ffprobe && e.EncoderPath == ffmpeg), NullLogger<MediaProber>.Instance);
            var source = (await prober.ProbeAsync(original, default))!;
            Assert.Empty(source.AudioStreams); Assert.Empty(source.SubtitleStreams);
            var facts = await DynamicHdrDetector.ScanAsync(ffmpeg, original, dir, 1, default);
            await GpuHdrToolchain.RunWithToolsAsync(source, facts, encoded, output, ffmpeg,
                (Path.Combine(toolRoot, "hdr10plus_tool.exe"), Path.Combine(mkvRoot, "mkvmerge.exe"),
                Path.Combine(mkvRoot, "mkvextract.exe"), Path.Combine(mkvRoot, "mkvpropedit.exe"), Path.Combine(toolRoot, "dovi_tool.exe")),
                work, async () => await ProcessRunner.RunAsync(ffmpeg, new[] { "-v", "error", "-i", original,
                    "-c:v", "libx265", "-crf", "32", "-pix_fmt", "yuv420p10le", "-fps_mode", "passthrough",
                    "-x265-params", hdr, "-metadata", "JELLYFIN_COMPRESSOR=v1", encoded }, 60000, default),
                _ => { }, default, null, appImage: false);
            Assert.Null(Safety.CompressionPolicy.VerificationError(source, (await prober.ProbeAsync(output, default))!));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public async Task BundledToolsCanBePreparedConcurrentlyWithoutChangingLinks()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("JELLYFIN_HDR_BUNDLED_TEST") != "1") return;
        var dir = Directory.CreateTempSubdirectory("gpu-hdr-tools-").FullName;
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => Hdr10PlusToolchain.ResolveGpuTools(dir))));
            foreach (var tools in results)
            {
                Assert.Equal(results[0], tools);
                Assert.Contains("2.3.4", await Hdr10PlusToolchain.RunTool(tools.Dovi, new[] { "--version" }, default, null));
                Assert.Contains("102.0", await Hdr10PlusToolchain.RunTool(tools.MkvPropedit, new[] { "--version" }, default, null, appImage: true));
            }
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public async Task ValidatedHdrClipPassesProductionReinjectionAndExactPacketChecks()
    {
        var fixture = Environment.GetEnvironmentVariable("JELLYFIN_GPU_HDR_FIXTURE");
        var toolRoot = Environment.GetEnvironmentVariable("JELLYFIN_GPU_HDR_TOOLS");
        var mkvRoot = Environment.GetEnvironmentVariable("JELLYFIN_GPU_HDR_MKVTOOLS");
        if (string.IsNullOrEmpty(fixture) || string.IsNullOrEmpty(toolRoot) || string.IsNullOrEmpty(mkvRoot)) return;
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg")!;
        var ffprobe = FfmpegTestBinaries.Find("ffprobe")!;
        var dir = Directory.CreateTempSubdirectory("gpu-hdr-pipeline-").FullName;
        var id = Guid.NewGuid().ToString("N");
        var work = Path.Combine(dir, id + ".hdr10plus");
        var original = Path.Combine(dir, "source.mkv");
        var encoded = Path.Combine(work, "encoded.mkv");
        var output = Path.Combine(dir, "output.mkv");
        try
        {
            await ProcessRunner.RunAsync(ffmpeg, new[] { "-v", "error", "-i", fixture, "-t", "2", "-map", "0",
                "-c", "copy", "-map_chapters", "-1", original }, 60000, default);
            var before = await Safety.ContentRegistry.IdentifyAsync(original, default);
            var mediaEncoder = Mock.Of<IMediaEncoder>(e => e.ProbePath == ffprobe && e.EncoderPath == ffmpeg);
            var prober = new MediaProber(mediaEncoder, NullLogger<MediaProber>.Instance);
            var source = (await prober.ProbeAsync(original, default))!;
            var facts = await DynamicHdrDetector.ScanAsync(ffmpeg, original, dir, source.DurationSeconds, default);
            Assert.Null(DynamicHdrDetector.ApplyFacts(source, facts));
            var stages = new List<string>();
            await GpuHdrToolchain.RunWithToolsAsync(source, facts, encoded, output, ffmpeg,
                (Path.Combine(toolRoot, "hdr10plus_tool.exe"), Path.Combine(mkvRoot, "mkvmerge.exe"),
                Path.Combine(mkvRoot, "mkvextract.exe"), Path.Combine(mkvRoot, "mkvpropedit.exe"), Path.Combine(toolRoot, "dovi_tool.exe")),
                work, () => { File.Copy(original, encoded); return Task.CompletedTask; }, stages.Add, default, null, appImage: false);
            var result = (await prober.ProbeAsync(output, default))!;
            var outputFacts = await DynamicHdrDetector.ScanAsync(ffmpeg, output, dir, source.DurationSeconds, default);
            Assert.Null(DynamicHdrDetector.ApplyFacts(result, outputFacts));
            Assert.True(result.IsHdr && result.IsDolbyVision && result.HasHdr10Plus);
            Assert.Null(DynamicHdrDetector.PreservationError(facts, outputFacts));
            Assert.Null(Safety.CompressionPolicy.VerificationError(source, result));
            Assert.Equal(before, await Safety.ContentRegistry.IdentifyAsync(original, default));
            Assert.Contains(stages, s => s.Contains("paquetes de audio"));
            Hdr10PlusToolchain.CleanupWork(dir, id);
            Assert.False(Directory.Exists(work));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public async Task FailedHdrChildReportsExitCodeAndCannotModifyTheOriginal()
    {
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg");
        if (ffmpeg is null) return;
        var dir = Directory.CreateTempSubdirectory("hdr-failure-").FullName;
        var original = Path.Combine(dir, "original.mkv");
        try
        {
            await File.WriteAllBytesAsync(original, new byte[] { 11, 22, 33 });
            var error = await Assert.ThrowsAsync<IOException>(() => Hdr10PlusToolchain.RunFfmpeg(ffmpeg,
                new[] { "-v", "error", "-xerror", "-i", original, "-f", "null", "-" }, 1, default, null));
            Assert.Matches(@"FFmpeg.*\(-?[1-9][0-9]*\)", error.Message);
            Assert.Equal(new byte[] { 11, 22, 33 }, await File.ReadAllBytesAsync(original));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void StaticRepairPreservesVideoAndOtherSeiAcrossBlockBoundaries()
    {
        var dir = Directory.CreateTempSubdirectory("hdr-static-").FullName;
        try
        {
            var original = Path.Combine(dir, "original.hevc");
            var encoded = Path.Combine(dir, "encoded.hevc");
            var output = Path.Combine(dir, "output.hevc");
            // A 4-byte Annex B delimiter straddles the 1 MiB input buffer.
            var video = new byte[1048570]; video[0] = 2; video[1] = 1;
            Array.Fill(video, (byte)85, 2, video.Length - 2);
            byte[] Sei(byte mastering, byte light) => new byte[] { 78, 1, 5, 3, 11, 12, 13, 137, 24 }
                .Concat(Enumerable.Repeat(mastering, 24)).Concat(new byte[] { 144, 4, light, light, light, light, 128 }).ToArray();
            byte[] Stream(byte[] sei) => new byte[] { 0, 0, 0, 1 }.Concat(video)
                .Concat(new byte[] { 0, 0, 0, 1 }).Concat(sei).ToArray();
            var expected = Stream(Sei(9, 7));
            File.WriteAllBytes(original, expected); File.WriteAllBytes(encoded, Stream(Sei(8, 6)));
            HevcStaticMetadata.Restore(encoded, output, HevcStaticMetadata.Inspect(original, default), default);
            Assert.Equal(expected, File.ReadAllBytes(output));
            Assert.Throws<IOException>(() => HevcStaticMetadata.Restore(encoded, output,
                HevcStaticMetadata.Inspect(original, default), default));
            var cancelled = new CancellationToken(true);
            Assert.Throws<OperationCanceledException>(() => HevcStaticMetadata.Inspect(original, cancelled));
            File.WriteAllBytes(original, new byte[] { 0, 0, 1, 78, 1, 137, 24, 128 });
            Assert.Throws<IOException>(() => HevcStaticMetadata.Inspect(original, default));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResizeRejectsSpatialDynamicMetadataOrIncompleteFrameCoverage()
    {
        var dir = Directory.CreateTempSubdirectory("hdr-shape-").FullName;
        const string area = """
            {"active_area":{"presets":[{"id":0,"left":0,"right":0,"top":0,"bottom":0}],"edits":{"0-1":0}}}
            """;
        const string hdr = """
            {"JSONInfo":{"HDR10plusProfile":"B"},"SceneInfo":[{"NumberOfWindows":1},{"NumberOfWindows":1}]}
            """;
        var path = Path.Combine(dir, "metadata.json");
        try
        {
            File.WriteAllText(path, area); HdrMetadataShape.ValidateDolby(path, 2, default);
            foreach (var invalid in new[] { area.Replace("\"top\":0", "\"top\":200"),
                area.Replace("0-1", "1-2"), area.Replace("0-1", "0-2"), area.Replace("\"0-1\":0", "\"0-1\":9") })
            {
                File.WriteAllText(path, invalid);
                Assert.Throws<IOException>(() => HdrMetadataShape.ValidateDolby(path, 2, default));
            }
            File.WriteAllText(path, hdr); HdrMetadataShape.ValidateHdr10Plus(path, 2, default);
            foreach (var invalid in new[] { hdr.Replace("\"B\"", "\"A\""), hdr.Replace("Windows\":1", "Windows\":2"),
                hdr.Replace("Windows\":1", "Windows\":1,\"NumberOfWindows\":1"), "{}" })
            {
                File.WriteAllText(path, invalid);
                Assert.Throws<IOException>(() => HdrMetadataShape.ValidateHdr10Plus(path, 2, default));
            }
            File.WriteAllText(path, hdr);
            Assert.Throws<IOException>(() => HdrMetadataShape.ValidateHdr10Plus(path, 3, default));
        }
        finally { Directory.Delete(dir, true); }
    }
}
