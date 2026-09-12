using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MakeRepo;

/// <summary>
///     Writes the Dalamud plugin repository listing from the manifest DalamudPackager produced.
/// </summary>
/// <remarks>
///     The listing has to repeat everything the plugin manifest already says - name, version,
///     API level and so on - and Dalamud offers an update only when the version in the listing
///     is higher than the installed one. Hand-maintaining a second copy of those fields means
///     that sooner or later a release ships with the listing still naming the previous version,
///     and the update silently never appears. So it is derived from the built manifest instead.
/// </remarks>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);

        string manifestPath, outPath, repoUrl;
        try
        {
            manifestPath = options.Require("manifest");
            outPath = options.Require("out");
            repoUrl = options.Require("repo");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                """
                usage: make-repo --manifest <built manifest .json>
                                 --out <repo.json>
                                 --repo <https://github.com/user/project>
                                 [--zip <latest.zip, for the timestamp>]

                Writes the Dalamud repository listing from the manifest that
                DalamudPackager wrote next to latest.zip.
                """);
            return 1;
        }

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"error: '{manifestPath}' does not exist. Build in Release first.");
            return 1;
        }

        if (JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject manifest)
        {
            Console.Error.WriteLine($"error: '{manifestPath}' is not a JSON object.");
            return 1;
        }

        // Releases are published under a tag, but the "latest" alias keeps the listing valid
        // across versions, so the file only changes when the manifest itself does.
        var download = $"{repoUrl.TrimEnd('/')}/releases/latest/download/latest.zip";

        manifest["DownloadLinkInstall"] = download;
        manifest["DownloadLinkUpdate"] = download;
        manifest["DownloadLinkTesting"] = download;
        manifest["IsHide"] = false;
        manifest["IsTestingExclusive"] = false;
        manifest["DownloadCount"] = 0;

        var zip = options.Value("zip");
        manifest["LastUpdate"] = zip is not null && File.Exists(zip)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(zip), TimeSpan.Zero).ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Dalamud expects an array of plugins even when the repository holds only one.
        var listing = new JsonArray(manifest);
        File.WriteAllText(outPath, listing.ToJsonString(Json) + Environment.NewLine);

        Console.WriteLine($"Wrote {outPath}");
        Console.WriteLine($"  {manifest["Name"]} {manifest["AssemblyVersion"]} " +
                          $"(API {manifest["DalamudApiLevel"]})");
        Console.WriteLine($"  download: {download}");
        return 0;
    }

    /// <summary>Minimal <c>--key value</c> parser.</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(string[] args)
        {
            var o = new Options();

            for (var i = 0; i < args.Length - 1; i++)
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                    o._values[args[i][2..]] = args[++i];

            return o;
        }

        public string? Value(string key) => _values.GetValueOrDefault(key);

        public string Require(string key)
            => _values.TryGetValue(key, out var v)
                ? v
                : throw new ArgumentException($"missing required option --{key}");
    }
}
