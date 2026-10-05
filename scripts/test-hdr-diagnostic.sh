#!/usr/bin/env bash
set -euo pipefail
repo=$(cd -- "$(dirname -- "$0")/.." && pwd)
fixture=$(mktemp -d "$repo/artifacts/hdr-extractor-check-XXXXXX")
dotnet new console --framework net9.0 --output "$fixture/check" --no-restore >/dev/null
cat >"$fixture/failed-tool.sh" <<'TOOL'
#!/bin/sh
printf 'matching but incomplete RPU' > "$4"
exit 23
TOOL
chmod u+x "$fixture/failed-tool.sh"
cat >"$fixture/check/Program.cs" <<'CHECK'
using System.Reflection;
using System.Runtime.Loader;
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(args[0]);
var method = assembly.GetType("Trial", true)!.GetMethod("ExtractRpu", BindingFlags.NonPublic | BindingFlags.Static)!;
try
{
    await (Task)method.Invoke(null, new object[] { args[1], "unused-input.hevc", args[2], CancellationToken.None })!;
    throw new Exception("Failed extraction was accepted despite its partial RPU output.");
}
catch (IOException error) when (error.Message.Contains("exited 23"))
{
    if (!File.Exists(args[2])) throw new Exception("The fake tool did not leave a partial output; regression not exercised.");
    Console.WriteLine("PASS: failed RPU extraction cannot be accepted even with a nonempty output.");
}
CHECK
dotnet run --project "$fixture/check" -- "$repo/artifacts/hdr-validation/app/Jellyfin.Compressor.HdrValidation.dll" \
    "$fixture/failed-tool.sh" "$fixture/partial.rpu.bin"
