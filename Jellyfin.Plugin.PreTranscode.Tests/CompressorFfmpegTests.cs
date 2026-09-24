using System.Diagnostics;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Safety;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public sealed class CompressorFfmpegTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("compressor-ffmpeg-").FullName;
    private readonly string ffmpeg = FfmpegTestBinaries.Find("ffmpeg") ?? throw new InvalidOperationException("FFmpeg is required for these integration tests.");
    private readonly string ffprobe = FfmpegTestBinaries.Find("ffprobe") ?? throw new InvalidOperationException("ffprobe is required for these integration tests.");
    private async Task Run(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        await stdout;
        Assert.True(process.ExitCode == 0, await error);
    }
    private async Task<MediaProbeInfo> Probe(string path) => MediaProber.Parse(await ProcessRunner.RunAsync(ffprobe, MediaProber.BuildProbeArguments(path), 60000, default), path);

    [Theory]
    [InlineData("960x540", 480)]
    [InlineData("640x360", 720)]
    public async Task ResolutionLimitDownscalesOnlyWhenSourceExceedsIt(string size, int cap)
    {
        var source = Path.Combine(root, "source.mkv");
        var output = Path.Combine(root, "output.mkv");
        await Run(new[] { "-nostdin", "-y", "-hide_banner", "-f", "lavfi", "-i", "testsrc2=size=" + size + ":rate=12",
            "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", source });
        var sourceProbe = await Probe(source);
        var profile = CompressionPolicy.EffectiveProfile(new EncodingProfile { VideoEncoder = "libx265", Crf = 26,
            ResolutionMode = ResolutionMode.CapHeight, MaxHeight = cap }, source);
        await Run(CompressionPolicy.BuildArguments(profile, sourceProbe, source, output));
        var outputProbe = await Probe(output);

        Assert.Null(CompressionPolicy.VerificationError(sourceProbe, outputProbe, profile));
        Assert.True(outputProbe.Width <= sourceProbe.Width && outputProbe.Height <= sourceProbe.Height);
        if (cap == 480) Assert.Equal(480, outputProbe.Height);
        else Assert.Equal((sourceProbe.Width, sourceProbe.Height), (outputProbe.Width, outputProbe.Height));
    }

    [Theory]
    [InlineData(".mkv")]
    [InlineData(".mp4")]
    [InlineData(".m4v")]
    public async Task RealEncodePreservesTracksChaptersDatesAndRestores(string extension)
    {
        var library = Directory.CreateDirectory(Path.Combine(root, "Library")).FullName;
        var source = Path.Combine(library, "Film with spaces ' quote" + extension);
        var output = Path.Combine(root, "result" + extension);
        var srt = Path.Combine(root, "subtitles.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:02,500\nA test subtitle\n");
        var metadata = Path.Combine(root, "metadata.txt");
        await File.WriteAllTextAsync(metadata, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=3000\ntitle=Introduction\n");
        await Run(new[] { "-nostdin", "-y", "-hide_banner", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-i", srt, "-i", metadata, "-t", "3", "-map", "0:v", "-map", "1:a", "-map", "1:a", "-map", "2:s", "-map_metadata", "3", "-map_chapters", "3", "-c:v", "libx264", "-crf", "12",
            "-c:a", "aac", "-c:s", extension == ".mkv" ? "srt" : "mov_text", "-metadata:s:a:0", "language=spa", "-metadata:s:a:1", "language=eng",
            "-metadata:s:s:0", "language=fra", "-disposition:a:0", "0", "-disposition:a:1", "default", "-disposition:s:0", "forced", "-f", extension == ".mkv" ? "matroska" : "mp4", source });
        var sourceProbe = await Probe(source);
        Assert.Null(CompressionPolicy.EligibilityError(sourceProbe));
        var effective = CompressionPolicy.EffectiveProfile(new EncodingProfile { VideoEncoder = "libx265", Crf = 26 }, source);
        await Run(CompressionPolicy.BuildArguments(effective, sourceProbe, source, output));
        var outputProbe = await Probe(output);
        Assert.Null(CompressionPolicy.VerificationError(sourceProbe, outputProbe));
        Assert.NotNull(CompressionPolicy.EligibilityError(outputProbe));
        Assert.Equal(2, outputProbe.AudioStreams.Count);
        Assert.Single(outputProbe.SubtitleStreams);
        Assert.Single(outputProbe.Chapters);
        await Run(new[] { "-v", "error", "-xerror", "-i", output, "-f", "null", "-" });
        var date = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, date);
        var identity = await ContentRegistry.IdentifyAsync(source, default);
        var compressed = await ContentRegistry.IdentifyAsync(output, default);
        var registry = new ContentRegistry(Path.Combine(root, "Registry"));
        var service = new ReplacementService(Path.Combine(root, "Transactions"), registry);
        var id = Guid.NewGuid().ToString("N");
        await service.PublishAsync(new(id, source, output, library, Path.Combine(root, "Originals"), 7, "test", identity, compressed), default);
        Assert.Equal(date, File.GetLastWriteTimeUtc(source));
        Assert.Equal(compressed, await ContentRegistry.IdentifyAsync(source, default));
        await service.RestoreAsync(id, default);
        Assert.Equal(identity, await ContentRegistry.IdentifyAsync(source, default));
    }
    public void Dispose() => Directory.Delete(root, true);
}
