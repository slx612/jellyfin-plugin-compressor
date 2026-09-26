using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Safety;

namespace Jellyfin.Plugin.PreTranscode.Media;

internal static class Hdr10PlusToolchain
{
    private const string MkvHash = "C66345B30D6D5FD640EA982AB5E202A99B9F541A20E42AA19EDD90F3DDD5DC9B";
    private const string HdrHash = "7845916B549C36E5D7FE9DBB3D24C124466D7C71EC3442E207551B677949D0BE";
    private static readonly string[] WorkNames = { "encoded.mkv", "source.json", "encoded.hevc", "injected.hevc", "timestamps.txt",
        "output.json", "source.framecrc", "output.framecrc" };

    internal static bool MetadataMatches(string source, string output)
    {
        if (new FileInfo(source).Length == 0 || new FileInfo(output).Length == 0) return false;
        using var a = File.OpenRead(source);
        using var b = File.OpenRead(output);
        return a.Length == b.Length && SHA256.HashData(a).SequenceEqual(SHA256.HashData(b));
    }

    internal static void EnsureAvailable(string dataRoot) => _ = ResolveTools(dataRoot);

    internal static async Task RunAsync(string source, string encoded, string output, string ffmpeg,
        string dataRoot, string tempRoot, string jobId, double duration, CancellationToken token,
        Action<Process>? onProcessStarted)
    {
        var (hdr, mkvmerge, mkvextract) = ResolveTools(dataRoot);
        await RunWithToolsAsync(source, encoded, output, ffmpeg, tempRoot, jobId, duration,
            hdr, mkvmerge, mkvextract, true, token, onProcessStarted).ConfigureAwait(false);
    }

    internal static async Task RunWithToolsAsync(string source, string encoded, string output, string ffmpeg,
        string tempRoot, string jobId, double duration, string hdr, string mkvmerge, string mkvextract,
        bool appImage, CancellationToken token, Action<Process>? onProcessStarted)
    {
        var work = Path.Combine(tempRoot, jobId + ".hdr10plus");
        Directory.CreateDirectory(work);
        FolderPolicy.RejectLinks(work);
        string W(string name) => Path.Combine(work, name);
        try
        {
            await RunTool(hdr, new[] { "extract", source, "-o", W("source.json") }, token, onProcessStarted).ConfigureAwait(false);
            if (!File.Exists(W("source.json")) || new FileInfo(W("source.json")).Length == 0)
                throw new IOException("HDR10+: no se pudieron extraer metadatos del original.");
            await RunFfmpeg(ffmpeg, new[] { "-y", "-v", "error", "-xerror", "-i", encoded, "-map", "0:V:0",
                "-c", "copy", "-bsf:v", "hevc_mp4toannexb", "-f", "hevc", W("encoded.hevc") }, duration,
                token, onProcessStarted).ConfigureAwait(false);
            await RunTool(hdr, new[] { "inject", "-i", W("encoded.hevc"), "-j", W("source.json"),
                "-o", W("injected.hevc") }, token, onProcessStarted).ConfigureAwait(false);
            await RunTool(mkvextract, new[] { encoded, "timestamps_v2", "0:" + W("timestamps.txt") },
                token, onProcessStarted, appImage).ConfigureAwait(false);
            var identify = await RunTool(mkvmerge, new[] { "-J", encoded }, token, onProcessStarted,
                appImage).ConfigureAwait(false);
            var muxArgs = BuildMuxArguments(identify, W("injected.hevc"), encoded, output, W("timestamps.txt"));
            await RunTool(mkvmerge, muxArgs, token, onProcessStarted, appImage).ConfigureAwait(false);
            await RunTool(hdr, new[] { "extract", output, "-o", W("output.json") }, token, onProcessStarted).ConfigureAwait(false);
            if (!File.Exists(W("output.json")) || !MetadataMatches(W("source.json"), W("output.json")))
                throw new IOException("HDR10+: los metadatos dinámicos de la salida no coinciden con el original.");
            await NonVideoFrameCrc(ffmpeg, source, W("source.framecrc"), duration, token, onProcessStarted).ConfigureAwait(false);
            await NonVideoFrameCrc(ffmpeg, output, W("output.framecrc"), duration, token, onProcessStarted).ConfigureAwait(false);
            if (!MetadataMatches(W("source.framecrc"), W("output.framecrc")))
                throw new IOException("HDR10+: audio o subtítulos cambiaron al remontar el vídeo.");
        }
        finally { CleanupWork(tempRoot, jobId); }
    }

    internal static void CleanupWork(string tempRoot, string jobId)
    {
        var work = Path.Combine(tempRoot, jobId + ".hdr10plus");
        if (!Directory.Exists(work)) return;
        FolderPolicy.RejectLinks(work);
        foreach (var name in WorkNames)
        {
            var file = Path.Combine(work, name);
            FolderPolicy.RejectLinks(file);
            File.Delete(file);
        }
        Directory.Delete(work);
    }

    internal static IReadOnlyList<string> BuildMuxArguments(string identifyJson, string video, string encoded,
        string output, string timestamps)
    {
        using var doc = JsonDocument.Parse(identifyJson);
        var track = doc.RootElement.GetProperty("tracks").EnumerateArray()
            .Single(t => t.GetProperty("type").GetString() == "video");
        var props = track.GetProperty("properties");
        var args = new List<string> { "--output", output, "--timestamps", "0:" + timestamps };
        void TextFlag(string property, string option)
        {
            if (props.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                args.AddRange(new[] { option, "0:" + value.GetString() });
        }
        void BoolFlag(string property, string option)
        {
            if (props.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                args.AddRange(new[] { option, "0:" + (value.GetBoolean() ? "1" : "0") });
        }
        TextFlag("language", "--language");
        TextFlag("track_name", "--track-name");
        BoolFlag("default_track", "--default-track-flag");
        BoolFlag("forced_track", "--forced-display-flag");
        BoolFlag("enabled_track", "--track-enabled-flag");
        BoolFlag("flag_original", "--original-flag");
        args.Add(video);
        args.Add("--no-video");
        args.Add(encoded);
        return args;
    }

    private static async Task NonVideoFrameCrc(string ffmpeg, string input, string output, double duration,
        CancellationToken token, Action<Process>? callback) => await RunFfmpeg(ffmpeg,
        new[] { "-y", "-v", "error", "-xerror", "-i", input, "-map", "0:a?", "-map", "0:s?",
            "-c", "copy", "-f", "framecrc", output }, duration, token, callback).ConfigureAwait(false);

    private static async Task RunFfmpeg(string ffmpeg, IReadOnlyList<string> args, double duration,
        CancellationToken token, Action<Process>? callback)
    {
        var (code, tail) = await FfmpegExecutor.RunAsync(ffmpeg, args, duration, null, token, callback).ConfigureAwait(false);
        if (code != 0) throw new IOException("HDR10+: FFmpeg terminó con error: " + tail);
    }

    private static async Task<string> RunTool(string executable, IReadOnlyList<string> args, CancellationToken token,
        Action<Process>? callback, bool appImage = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        if (appImage) start.Environment["APPIMAGE_EXTRACT_AND_RUN"] = "1";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("HDR10+: no se pudo iniciar " + Path.GetFileName(executable));
        callback?.Invoke(process);
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(6));
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            if (!token.IsCancellationRequested) throw new TimeoutException("HDR10+: la herramienta tardó más de 6 horas.");
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException("HDR10+: " + Path.GetFileName(executable) + " terminó con error: " + error[^Math.Min(error.Length, 3000)..]);
        return output;
    }

    private static (string Hdr, string MkvMerge, string MkvExtract) ResolveTools(string dataRoot)
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new IOException("HDR10+ experimental requiere Linux x86-64 (Docker/Synology).");
        var bundled = Path.Combine(Path.GetDirectoryName(typeof(Hdr10PlusToolchain).Assembly.Location)!, "tools");
        var hdrSource = Path.Combine(bundled, "hdr10plus_tool");
        var mkvSource = Path.Combine(bundled, "mkvtoolnix.AppImage");
        if (!File.Exists(hdrSource) || !File.Exists(mkvSource))
            throw new IOException("HDR10+: faltan herramientas incluidas en el paquete del complemento.");
        if (!HashIs(hdrSource, HdrHash) || !HashIs(mkvSource, MkvHash))
            throw new IOException("HDR10+: la firma SHA-256 de una herramienta no coincide.");
        var target = Path.Combine(dataRoot, "jellyfin-compressor", "hdr10plus-tools");
        Directory.CreateDirectory(target);
        FolderPolicy.RejectLinks(target);
        var hdr = Path.Combine(target, "hdr10plus_tool");
        var mkv = Path.Combine(target, "mkvtoolnix.AppImage");
        CopyVerified(hdrSource, hdr, HdrHash);
        CopyVerified(mkvSource, mkv, MkvHash);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(hdr, mode);
        File.SetUnixFileMode(mkv, mode);
        var merge = Path.Combine(target, "mkvmerge");
        var extract = Path.Combine(target, "mkvextract");
        File.Delete(merge); File.Delete(extract);
        File.CreateSymbolicLink(merge, mkv);
        File.CreateSymbolicLink(extract, mkv);
        return (hdr, merge, extract);
    }

    private static void CopyVerified(string source, string target, string hash)
    {
        FolderPolicy.RejectLinks(target);
        if (File.Exists(target) && HashIs(target, hash)) return;
        File.Copy(source, target, overwrite: true);
        if (!HashIs(target, hash)) throw new IOException("HDR10+: error al preparar la herramienta.");
    }

    private static bool HashIs(string path, string expected)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
