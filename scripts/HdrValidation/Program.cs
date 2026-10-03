using System.Diagnostics;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Safety;

// A diagnostic only: use production encoding/verification helpers, never ReplacementService.
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    foreach (var root in new[] { "/jellyfin", "/usr/lib/jellyfin/bin", "/app/jellyfin" })
    {
        var path = Path.Combine(root, name.Name + ".dll");
        if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
return await Trial.RunAsync(args);

internal static class Trial
{
    private static string stage = "Preflight";
    private static double? percent;
    private static readonly Stopwatch elapsed = Stopwatch.StartNew();

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: HdrValidation /Peliculas/source.mkv NEW_RUN_DIRECTORY TOOLS_DIRECTORY");
            return 2;
        }
        var source = Path.GetFullPath(args[0]);
        var root = Path.GetFullPath(args[1]);
        var tools = Path.GetFullPath(args[2]);
        if (!source.StartsWith("/Peliculas/", StringComparison.Ordinal) || !source.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
            || !root.StartsWith("/config/data/jellyfin-compressor/hdr-validation/", StringComparison.Ordinal)
            || Directory.Exists(root) || File.Exists(root))
        {
            Console.Error.WriteLine("Refusing: source must be a /Peliculas MKV; output must be a NEW validation directory.");
            return 2;
        }
        FolderPolicy.RejectLinks(source);
        FolderPolicy.RejectLinks(root);
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var heartbeat = Heartbeat(root, cancellation.Token);
        try
        {
            await Validate(source, root, tools, cancellation.Token);
            Stage("Technical checks passed; client playback still pending");
            return 0;
        }
        catch (Exception error)
        {
            Stage(error is OperationCanceledException ? "Cancelled; original retained" : "Failed; original retained");
            await File.WriteAllTextAsync(Path.Combine(root, "failure.txt"), error.ToString());
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            await WriteStatus(root);
            cancellation.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
    }

    private static async Task Validate(string source, string root, string tools, CancellationToken token)
    {
        const string ffmpeg = "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        const string ffprobe = "/usr/lib/jellyfin-ffmpeg/ffprobe";
        var sourceFile = new FileInfo(source);
        var bytes = sourceFile.Length;
        if (new DriveInfo(root).AvailableFreeSpace < checked(bytes * 4 + 5L * 1073741824))
            throw new IOException("Insufficient free space: require four times the source size plus 5 GiB.");
        var hdr = Path.Combine(tools, "hdr10plus_tool");
        var mkv = Path.Combine(tools, "MKVToolNix_GUI-102.0-x86_64.AppImage");
        var dovi = Path.Combine(tools, "dovi_tool");
        await VerifyTool(hdr, "7845916b549c36e5d7fe9dbb3d24c124466d7c71ec3442e207551b677949d0be", token);
        await VerifyTool(mkv, "c66345b30d6d5fd640ea982ab5e202a99b9f541a20e42aa19edd90f3ddd5dc9b", token);
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(Path.Combine(tools, "sha256.json"), token))!;
        await VerifyTool(dovi, manifest["dovi_tool"], token);
        Stage("Hashing original");
        var sourceHash = await Hash(source, token);
        var input = await Probe(source, "source", ffprobe, root, token);
        if (!input.IsHdr && !input.IsDolbyVision) throw new IOException("This diagnostic requires an HDR source.");
        Stage("Scanning every source frame for HDR/Dolby metadata");
        var facts = await DynamicHdrDetector.ScanAsync(ffmpeg, source, root, input.DurationSeconds, token, Started);
        Check(DynamicHdrDetector.ApplyFacts(input, facts));
        var profile = CompressionPolicy.EffectiveProfile(new EncodingProfile
        {
            VideoEncoder = "libx265", Crf = 20, ResolutionMode = ResolutionMode.Unchanged
        }, source);
        var config = new PluginConfiguration { EnableExperimentalHdr = true, EnableExperimentalHdr10Plus = true, EnableExperimentalDolbyVision = true };
        Check(CompressionPolicy.EligibilityError(input, profile, config));
        var encoded = Path.Combine(root, "encoded.mkv");
        var output = input.HasHdr10Plus ? Path.Combine(root, "verified-candidate.mkv") : encoded;
        var arguments = CompressionPolicy.BuildArguments(profile, input, source, encoded, input.HasHdr10Plus);
        await File.WriteAllTextAsync(Path.Combine(root, "encoding-arguments.json"), JsonSerializer.Serialize(arguments), token);
        Stage("Encoding full film with libx265 CRF 20, original resolution");
        var result = await FfmpegExecutor.RunAsync(ffmpeg, arguments, input.DurationSeconds,
            value => percent = value, token, Started, encoded, (long)(bytes * .85));
        if (result.ExitCode != 0) throw new IOException(result.StdErrTail);
        percent = null;
        if (input.HasHdr10Plus)
        {
            Stage("Reinjecting HDR10+ and checking timestamps, audio and subtitles");
            await Hdr10PlusToolchain.RunWithToolsAsync(source, encoded, output, ffmpeg, root, "trial", input.DurationSeconds,
                hdr, Path.Combine(tools, "mkvmerge"), Path.Combine(tools, "mkvextract"), true, token, Started);
        }
        var candidate = await Probe(output, "output", ffprobe, root, token);
        Stage("Scanning every output frame for HDR/Dolby metadata");
        var outputFacts = await DynamicHdrDetector.ScanAsync(ffmpeg, output, root, input.DurationSeconds, token, Started);
        Check(DynamicHdrDetector.ApplyFacts(candidate, outputFacts));
        Check(DynamicHdrDetector.PreservationError(facts, outputFacts));
        Check(CompressionPolicy.VerificationError(input, candidate, profile));
        if (input.IsDolbyVision)
        {
            Stage("Comparing all Dolby Vision RPU values");
            await Rpu(source, "source", ffmpeg, dovi, root, input.DurationSeconds, token);
            await Rpu(output, "output", ffmpeg, dovi, root, input.DurationSeconds, token);
            if (!Hdr10PlusToolchain.MetadataMatches(Path.Combine(root, "source.rpu.bin"), Path.Combine(root, "output.rpu.bin")))
                throw new IOException("Full Dolby Vision RPU values differ.");
        }
        Stage("Decoding the entire output");
        var decode = await FfmpegExecutor.RunAsync(ffmpeg, new[] { "-v", "error", "-xerror", "-i", output,
            "-map", "0:V:0", "-an", "-sn", "-f", "null", "-" }, input.DurationSeconds, value => percent = value, token, Started);
        if (decode.ExitCode != 0) throw new IOException(decode.StdErrTail);
        Stage("Verifying original unchanged and calculating savings");
        if (await Hash(source, token) != sourceHash || new FileInfo(source).Length != bytes)
            throw new IOException("Source changed during diagnostic; discard conclusions.");
        var outputBytes = new FileInfo(output).Length;
        var savings = (1 - (double)outputBytes / bytes) * 100;
        await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
        {
            Source = source, Output = output, SourceSha256 = sourceHash, OutputSha256 = await Hash(output, token),
            SourceBytes = bytes, OutputBytes = outputBytes, SavingsPercent = savings, MinimumSavingsMet = savings >= 15,
            SourceFrameFacts = facts, OutputFrameFacts = outputFacts, OriginalUnchanged = true,
            DolbyRpuValuesMatch = input.IsDolbyVision ? (bool?)true : null,
            Hdr10PlusMetadataMatch = input.HasHdr10Plus ? (bool?)true : null,
            TechnicalChecksPassed = true, ClientPlaybackVerified = false,
            PluginDllSha256 = await Hash(typeof(CompressionPolicy).Assembly.Location, token),
            Build = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "build.json"), token)
        }, new JsonSerializerOptions { WriteIndented = true }), token);
        if (savings < 15) throw new IOException($"Technical checks passed, but savings {savings:F2}% are below 15%.");
    }

    private static async Task<MediaProbeInfo> Probe(string path, string name, string ffprobe, string root, CancellationToken token)
    {
        var json = await ProcessRunner.RunAsync(ffprobe, MediaProber.BuildProbeArguments(path), 60000, token);
        await File.WriteAllTextAsync(Path.Combine(root, name + ".probe.json"), json, token);
        return MediaProber.Parse(json, path);
    }

    private static async Task Rpu(string path, string name, string ffmpeg, string dovi, string root, double duration, CancellationToken token)
    {
        var video = Path.Combine(root, name + ".hevc");
        var result = await FfmpegExecutor.RunAsync(ffmpeg, new[] { "-v", "error", "-xerror", "-i", path, "-map", "0:V:0",
            "-c", "copy", "-bsf:v", "hevc_mp4toannexb", "-f", "hevc", video }, duration, null, token, Started);
        if (result.ExitCode != 0) throw new IOException(result.StdErrTail);
        await ProcessRunner.RunAsync(dovi, new[] { "extract-rpu", video, "-o", Path.Combine(root, name + ".rpu.bin") }, 3600000, token);
        File.Delete(video); // This newly generated diagnostic file only; keep RPU evidence and both MKVs.
    }

    private static void Check(string? error) { if (error is not null) throw new IOException(error); }
    private static void Stage(string value) { stage = value; percent = null; Console.WriteLine($"[{elapsed.Elapsed}] {value}"); }
    private static void Started(Process process) => Console.WriteLine($"Process {process.Id}: {process.StartInfo.FileName}");
    private static async Task<string> Hash(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)).ToLowerInvariant();
    }
    private static async Task VerifyTool(string path, string expected, CancellationToken token)
    {
        if (await Hash(path, token) != expected) throw new IOException("Tool checksum mismatch: " + path);
    }
    private static Task WriteStatus(string root) => File.WriteAllTextAsync(Path.Combine(root, "status.json"),
        JsonSerializer.Serialize(new { Stage = stage, Percent = percent, ElapsedSeconds = elapsed.Elapsed.TotalSeconds, UpdatedUtc = DateTime.UtcNow }));
    private static async Task Heartbeat(string root, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(token))
        {
            await WriteStatus(root);
            Console.WriteLine($"[{elapsed.Elapsed}] {stage}" + (percent.HasValue ? $" {percent:F1}%" : ""));
        }
    }
}
