using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PreTranscode.Media;

internal static class GpuHdrToolchain
{
    internal static async Task RunAsync(MediaProbeInfo source, DynamicHdrFacts facts, string encoded, string output,
        string ffmpeg, string dataRoot, string work, Func<Task> encode, Action<string> stage,
        CancellationToken token, Action<Process>? processStarted)
    {
        var tools = Hdr10PlusToolchain.ResolveGpuTools(dataRoot);
        Directory.CreateDirectory(work);
        Safety.FolderPolicy.RejectLinks(work);
        stage("Comprobando NVIDIA NVENC de 10 bits");
        var preflight = Path.Combine(work, "gpu-preflight.mkv");
        await Hdr10PlusToolchain.RunFfmpeg(ffmpeg, new[] { "-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=24",
            "-t", "1", "-c:v", "hevc_nvenc", "-profile:v", "main10", "-pix_fmt", "p010le", "-preset", "p5", preflight }, 1,
            token, processStarted).ConfigureAwait(false);
        File.Delete(preflight);
        await RunWithToolsAsync(source, facts, encoded, output, ffmpeg, tools, work, encode, stage, token,
            processStarted).ConfigureAwait(false);
    }

    internal static async Task RunWithToolsAsync(MediaProbeInfo source, DynamicHdrFacts facts, string encoded, string output,
        string ffmpeg, (string Hdr, string MkvMerge, string MkvExtract, string MkvPropedit, string Dovi) tools,
        string work, Func<Task> encode, Action<string> stage, CancellationToken token, Action<Process>? processStarted,
        bool appImage = true)
    {
        Directory.CreateDirectory(work);
        Safety.FolderPolicy.RejectLinks(work);
        string W(string name) => Path.Combine(work, name);
        Task<string> Tool(string executable, string[] args) => Hdr10PlusToolchain.RunTool(executable, args, token,
            processStarted, appImage && (executable == tools.MkvMerge || executable == tools.MkvExtract || executable == tools.MkvPropedit));
        Task Ffmpeg(string[] args) => Hdr10PlusToolchain.RunFfmpeg(ffmpeg, args, source.DurationSeconds, token, processStarted);
        Task ExtractVideo(string input, string target) => Ffmpeg(new[] { "-y", "-v", "error", "-xerror", "-i", input,
            "-map", "0:V:0", "-c", "copy", "-bsf:v", "hevc_mp4toannexb", "-f", "hevc", target });
        stage("Extrayendo y comprobando metadatos HDR del original");
        await ExtractVideo(source.Path, W("source.hevc")).ConfigureAwait(false);
        var staticMetadata = HevcStaticMetadata.Inspect(W("source.hevc"), token);
        if (source.IsDolbyVision)
        {
            await Tool(tools.Dovi, new[] { "extract-rpu", W("source.hevc"), "-o", W("source.rpu.bin") }).ConfigureAwait(false);
            await Tool(tools.Dovi, new[] { "export", "-i", W("source.rpu.bin"), "-d", "level5=" + W("source.level5.json") }).ConfigureAwait(false);
            HdrMetadataShape.ValidateDolby(W("source.level5.json"), facts.TotalFrames, token);
        }
        if (source.HasHdr10Plus)
        {
            await Tool(tools.Hdr, new[] { "extract", W("source.hevc"), "-o", W("source.json") }).ConfigureAwait(false);
            HdrMetadataShape.ValidateHdr10Plus(W("source.json"), facts.TotalFrames, token);
        }
        File.Delete(W("source.hevc"));
        var originalIdentify = await Tool(tools.MkvMerge, new[] { "-J", source.Path }).ConfigureAwait(false);
        var frameDuration = FrameDuration(originalIdentify);
        await Tool(tools.MkvExtract, new[] { source.Path, "timestamps_v2", "0:" + W("source.timestamps.txt") }).ConfigureAwait(false);
        stage("Comprimiendo");
        await encode().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        stage("Comprobando los tiempos del vídeo comprimido");
        await Tool(tools.MkvExtract, new[] { encoded, "timestamps_v2", "0:" + W("timestamps.txt") }).ConfigureAwait(false);
        RequireTimestamps(W("source.timestamps.txt"), W("timestamps.txt"));
        var identify = await Tool(tools.MkvMerge, new[] { "-J", encoded }).ConfigureAwait(false);
        await ExtractVideo(encoded, W("encoded.hevc")).ConfigureAwait(false);
        stage("Restaurando los metadatos HDR10 originales");
        HevcStaticMetadata.Restore(W("encoded.hevc"), W("static-restored.hevc"), staticMetadata, token);
        File.Delete(W("encoded.hevc"));
        var video = W("static-restored.hevc");
        if (source.IsDolbyVision)
        {
            stage("Reinsertando Dolby Vision 8.1");
            await Tool(tools.Dovi, new[] { "inject-rpu", "-i", video, "--rpu-in", W("source.rpu.bin"), "-o", W("dovi-injected.hevc") }).ConfigureAwait(false);
            File.Delete(video); video = W("dovi-injected.hevc");
        }
        if (source.HasHdr10Plus)
        {
            stage("Reinsertando HDR10+");
            await Tool(tools.Hdr, new[] { "inject", "-i", video, "-j", W("source.json"), "-o", W("injected.hevc") }).ConfigureAwait(false);
            File.Delete(video); video = W("injected.hevc");
        }
        var restored = HevcStaticMetadata.Inspect(video, token);
        if (staticMetadata.Any(pair => !restored[pair.Key].AsSpan().SequenceEqual(pair.Value)))
            throw new IOException("La salida ha cambiado los metadatos HDR10.");
        stage("Montando la película con sus pistas y capítulos");
        await Hdr10PlusToolchain.RunTool(tools.MkvMerge, Hdr10PlusToolchain.BuildMuxArguments(identify, video, encoded,
            output, W("timestamps.txt")), token, processStarted, appImage).ConfigureAwait(false);
        await Tool(tools.MkvPropedit, new[] { output, "--edit", "track:v1", "--set",
            "default-duration=" + frameDuration.ToString(CultureInfo.InvariantCulture) }).ConfigureAwait(false);
        if (FrameDuration(await Tool(tools.MkvMerge, new[] { "-J", output }).ConfigureAwait(false)) != frameDuration)
            throw new IOException("La duración nominal de los fotogramas ha cambiado.");
        await Tool(tools.MkvExtract, new[] { output, "timestamps_v2", "0:" + W("output.timestamps.txt") }).ConfigureAwait(false);
        RequireTimestamps(W("source.timestamps.txt"), W("output.timestamps.txt"));
        File.Delete(video);
        stage("Verificando los metadatos HDR y Dolby completos");
        if (source.IsDolbyVision)
        {
            await Tool(tools.Dovi, new[] { "extract-rpu", output, "-o", W("output.rpu.bin") }).ConfigureAwait(false);
            RequireMetadata(W("source.rpu.bin"), W("output.rpu.bin"), "Dolby Vision");
        }
        if (source.HasHdr10Plus)
        {
            await Tool(tools.Hdr, new[] { "extract", output, "-o", W("output.json") }).ConfigureAwait(false);
            RequireMetadata(W("source.json"), W("output.json"), "HDR10+");
        }
        stage("Verificando todos los paquetes de audio y subtítulos");
        var hasNonVideo = source.AudioStreams.Count > 0 || source.SubtitleStreams.Count > 0;
        await Hdr10PlusToolchain.NonVideoFrameCrc(ffmpeg, source.Path, W("source.framecrc"), source.DurationSeconds, token, processStarted, hasNonVideo).ConfigureAwait(false);
        await Hdr10PlusToolchain.NonVideoFrameCrc(ffmpeg, output, W("output.framecrc"), source.DurationSeconds, token, processStarted, hasNonVideo).ConfigureAwait(false);
        RequireMetadata(W("source.framecrc"), W("output.framecrc"), "audio o subtítulos");
    }

    private static void RequireMetadata(string source, string output, string kind)
    {
        if (!Hdr10PlusToolchain.MetadataMatches(source, output)) throw new IOException("La salida ha cambiado " + kind + ".");
    }
    private static void RequireTimestamps(string source, string output)
    {
        if (!Hdr10PlusToolchain.TimestampsMatch(source, output)) throw new IOException("La salida ha cambiado los tiempos de los fotogramas.");
    }
    internal static long FrameDuration(string identify)
    {
        using var doc = JsonDocument.Parse(Hdr10PlusToolchain.IdentificationJson(identify));
        var video = doc.RootElement.GetProperty("tracks").EnumerateArray().Single(t => t.GetProperty("type").GetString() == "video");
        if (video.GetProperty("id").GetInt32() != 0) throw new IOException("HDR GPU requiere que la única pista de vídeo tenga ID 0.");
        var duration = video.GetProperty("properties").GetProperty("default_duration").GetInt64();
        if (duration <= 0) throw new IOException("HDR GPU requiere una duración nominal de fotograma verificable.");
        return duration;
    }
}
