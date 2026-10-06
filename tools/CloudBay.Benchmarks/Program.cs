using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CloudBay.Application;
using CloudBay.Benchmarks;
using CloudBay.Core;
using CloudBay.Core.B2;
using CloudBay.Core.OneDrive;
using CloudBay.Core.Transfers;

var workspace = Path.GetFullPath(Argument("--workspace") ?? Environment.CurrentDirectory);
var mode = Argument("--mode") ?? "scheduler";
if (mode == "self-test") return await TraceProbeSelfTest.RunAsync();
var direction = Argument("--direction") ?? "simulated";
var workload = Argument("--workload") ?? "tiny";
var label = Argument("--label") ?? "current";
var workers = int.Parse(Argument("--workers") ?? "4");
var payloadWorkers = int.Parse(Argument("--payload-workers") ?? workers.ToString());
var tinyFiles = int.Parse(Argument("--tiny-files") ?? (workload == "mixed" ? "16" : "24"));
if (tinyFiles is < 1 or > 64) throw new ArgumentException("Choose between 1 and 64 tiny fixtures.");
var run = Guid.NewGuid().ToString("N");
var artifacts = Path.Combine(workspace, "artifacts", "validation", "throughput-" + label + "-" + run);
Directory.CreateDirectory(artifacts);
var trace = new TransferTrace();
using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15));
var token = lifetime.Token;
var sizes = workload switch
{
    "tiny" => Enumerable.Repeat(4096L, tinyFiles).ToArray(),
    "large" => new[] { 12L * 1024 * 1024 + 123 },
    "mixed" => Enumerable.Repeat(4096L, tinyFiles).Concat(new[] { 256L * 1024, 256L * 1024, 8L * 1024 * 1024 + 123 }).ToArray(),
    _ => throw new ArgumentException("Workload must be tiny, large or mixed.")
};
var hashes = new Dictionary<long, string>();
foreach (var size in sizes.Distinct()) hashes[size] = await GeneratedSource.HashAsync(size, token);
var fixtures = sizes.Select((size, index) => new TransferEntry("generated:" + index, $"fixture-{index:D3}.bin", "fixture-v1", size, GeneratedSource.Modified, hashes[size])).ToArray();
var cleanup = new List<(ITransferEndpoint Endpoint, TransferEntry Entry)>();
var disposable = new List<IDisposable>();
try
{
    ITransferEndpoint source; ITransferEndpoint destination;
    if (mode == "scheduler")
    {
        source = new SchedulerEndpoint(new("b2", "benchmark", "generated", "", "source/", "Generated delayed source"), fixtures, trace, false);
        destination = new SchedulerEndpoint(new("b2", "benchmark", "generated", "", "destination/", "Generated delayed destination"), [], trace, true);
    }
    else if (mode == "live")
    {
        var bandwidth = new TransferBandwidthBudget();
        bandwidth.Configure(0, 0);
        var parts = direction.Split('-');
        if (parts.Length != 2 || parts.Any(value => value is not ("local" or "b2" or "onedrive")) || parts[0] == parts[1])
            throw new ArgumentException("Choose local-b2, b2-local, local-onedrive, onedrive-local, onedrive-b2 or b2-onedrive.");
        B2CloudStore? b2 = null; OneDriveClient? graph = null; TransferLocation? b2Source = null, graphSource = null;
        if (parts.Contains("b2"))
        {
            var credentialStorage = new ClientStorage(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation"));
            var handler = new TracedHttpHandler(trace); b2 = new B2CloudStore(handler, bandwidthBudget: bandwidth); disposable.Add(b2);
            b2.Configure(0, 0, payloadWorkers);
            var account = await b2.ConnectAsync(credentialStorage.LoadCredentials() ?? throw new InvalidDataException("Restricted B2 validation credentials are missing."), token);
            var buckets = await b2.ListBucketsAsync(token);
            if (buckets.Count != 1 || account.AllowedBucketId != buckets[0].Id) throw new InvalidDataException("Use a single restricted validation bucket.");
            b2Source = new("b2", account.AccountId, buckets[0].Id, "", (account.AllowedNamePrefix ?? "") + "CloudBayThroughput/" + run + "/source/", "Owned throughput B2 source");
        }
        if (parts.Contains("onedrive"))
        {
            var accountId = Argument("--onedrive-account") ?? throw new ArgumentException("Select the reviewed --onedrive-account identity.");
            var driveId = Argument("--onedrive-drive") ?? throw new ArgumentException("Select the reviewed --onedrive-drive identity.");
            var storage = new ClientStorage(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudBay", "Validation", "OneDrive"));
            var saved = storage.LoadOneDriveConnections().Single(value => value.Id == accountId);
            var handler = new TracedHttpHandler(trace); var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) }; disposable.Add(http);
            var authGate = new SemaphoreSlim(1, 1); disposable.Add(authGate);
            graph = new(new(saved.ClientId, saved.Tenant, saved.Tokens, async (tokens, ct) =>
            {
                await authGate.WaitAsync(ct);
                try { storage.SaveOneDriveConnections(storage.LoadOneDriveConnections().Select(value => value.Id == accountId ? value with { Tokens = tokens } : value).ToArray()); }
                finally { authGate.Release(); }
            }, http), http, bandwidth);
            graph.Configure(0, 0, payloadWorkers, payloadWorkers);
            var drives = await graph.ListDrivesAsync(token);
            if (drives.Count == 0 || drives[0].Id != driveId) throw new InvalidDataException("This benchmark requires the reviewed account's primary drive.");
            var primary = await graph.GetRootAsync(driveId, token);
            var folder = await graph.EnsureFolderAsync(driveId, primary.Id, "CloudBay throughput " + run, token);
            graphSource = new("onedrive", accountId, driveId, folder.Id, "", "Owned throughput OneDrive folder");
        }
        ITransferEndpoint Endpoint(string provider, bool target) => provider switch
        {
            "b2" => new B2TransferEndpoint(b2!, b2Source! with { Path = target ? b2Source!.Path[..^"source/".Length] + "destination/" : b2Source!.Path }),
            "onedrive" => new OneDriveTransferEndpoint(graph!, graphSource!),
            "local" => new LocalTransferEndpoint(LocalTransferEndpoint.ForFolder(Path.Combine(artifacts, target ? "local-destination" : "local-source"))),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        source = Endpoint(parts[0], false); destination = Endpoint(parts[1], true);
        if (parts[0] == "local")
        {
            Directory.CreateDirectory(source.Location.Path);
            foreach (var entry in fixtures)
            {
                await using var generated = await new GeneratedSource(entry.RelativePath, entry.Size, entry.Sha1!).OpenReadAsync(0, entry.Size, token);
                await using var output = new FileStream(Path.Combine(source.Location.Path, entry.RelativePath), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
                await generated.CopyToAsync(output, token);
            }
        }
        else
        {
            var seeds = Path.Combine(artifacts, "seeds"); Directory.CreateDirectory(seeds);
            await Parallel.ForEachAsync(fixtures, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, async (entry, ct) =>
                await source.UploadAsync(new(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(run + "|seed|" + entry.RelativePath))).ToLowerInvariant(), entry.RelativePath, TransferConflictPolicy.Fail), new GeneratedSource(entry.RelativePath, entry.Size, entry.Sha1!), null,
                    async (value, checkpointToken) =>
                    {
                        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value);
                        try { await File.WriteAllBytesAsync(Path.Combine(seeds, entry.RelativePath + ".dpapi"), new Protector().Protect(plaintext), checkpointToken); }
                        finally { CryptographicOperations.ZeroMemory(plaintext); }
                    }, cancellationToken: ct));
        }
    }
    else throw new ArgumentException("Mode must be scheduler or live.");

    var sourceTrace = new TracedEndpoint(source, trace); var destinationTrace = new TracedEndpoint(destination, trace);
    var plan = new TransferJobPlan(Guid.NewGuid().ToString("N"), source.Location, destination.Location, TransferOperation.Copy, TransferConflictPolicy.Fail, [], DateTimeOffset.UtcNow);
    TransferJobSnapshot snapshot;
    using (var journal = new TransferJobJournal(Path.Combine(artifacts, "jobs.sqlite"), new Protector()))
    await using (var engine = new TransferJobEngine(journal, location => location == source.Location ? sourceTrace : destinationTrace, workers))
    {
        engine.Activity += value => { if (value.Completed) trace.Add(value.Path, "complete", trace.Now, trace.Now); };
        await engine.CreateAsync(plan, token);
        trace.Enabled = true; var started = trace.Now;
        await engine.RunAsync(plan.Id, token);
        var finished = trace.Now; trace.Enabled = false;
        snapshot = engine.Snapshots().Single();
        var report = new { run, label, mode, direction, workload, workers, payloadWorkers = mode == "live" ? (int?)payloadWorkers : null, sharedProviderBudget = mode == "live", coreSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(TransferJobEngine).Assembly.Location, token))),
            coreVersion = typeof(TransferJobEngine).Assembly.GetName().Version?.ToString(), result = snapshot.State.ToString(), snapshot.CompletedFiles, snapshot.TransferredBytes,
            measurements = trace.Summary(started, finished, (int)snapshot.CompletedFiles, snapshot.TotalBytes), events = trace.Events,
            failures = snapshot.Items.Where(value => value.Error is not null).Select(value => new { value.RelativePath, value.Error }),
            payloadStorage = mode == "scheduler" || (source.Location.Provider != "local" && destination.Location.Provider != "local") ? "Bounded RAM only; generated source and cloud adapters; durable metadata allowed" : "Explicit local source/destination fixture files, not cloud payload staging" };
        await File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
        Console.WriteLine(JsonSerializer.Serialize(new { report = Path.Combine(artifacts, "report.json"), state = snapshot.State.ToString(), snapshot.CompletedFiles, elapsedSeconds = (finished - started) / 1000 }));
    }
    if (snapshot.State != TransferJobState.Completed || snapshot.CompletedFiles != fixtures.Length || snapshot.TransferredBytes != fixtures.Sum(value => value.Size))
        return 1;
    if (mode == "live" && source.Location.Provider != "local" && destination.Location.Provider != "local")
    {
        var pattern = new byte[512];
        for (var index = 0; index < pattern.Length; index++) pattern[index] = (byte)(index * 17 + 43);
        foreach (var file in Directory.EnumerateFiles(artifacts, "*", SearchOption.AllDirectories))
            if ((await File.ReadAllBytesAsync(file, token)).AsSpan().IndexOf(pattern) >= 0)
                throw new InvalidDataException("The generated cloud fixture's byte pattern appeared in recovery metadata.");
        await File.WriteAllTextAsync(Path.Combine(artifacts, "payload-metadata-check.json"), JsonSerializer.Serialize(new
        { deterministicFixturePatternAbsent = true, scope = "This run's jobs, seeds and report; this is not OS-wide storage tracing", payloadStagingAdapters = "Cloud source/destination only" }), token);
    }
    if (mode == "live")
    {
        foreach (var endpoint in new[] { source, destination })
        {
            string? cursor = null;
            do
            {
                var page = await endpoint.DiscoverAsync(cursor, token);
                foreach (var entry in page.Entries.Where(value => !value.IsFolder)) cleanup.Add((endpoint, entry));
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        if (cleanup.Count != fixtures.Length * 2) throw new InvalidDataException("The generated source/destination fixture identity count differs from the plan.");
        await Parallel.ForEachAsync(cleanup, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, async (item, ct) =>
        {
            var expected = fixtures.Single(value => value.RelativePath == item.Entry.RelativePath);
            if (item.Entry.Size != expected.Size) throw new InvalidDataException("Owned fixture size changed before cleanup.");
            var replay = item.Endpoint.OpenSource(item.Entry); await replay.ValidateAsync(ct);
            await using var input = await replay.OpenReadAsync(0, item.Entry.Size, ct);
            if (!expected.Sha1!.Equals(Convert.ToHexString(await SHA1.HashDataAsync(input, ct)), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Owned fixture content changed before cleanup.");
        });
        await Parallel.ForEachAsync(cleanup, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, async (item, ct) =>
            await item.Endpoint.DeleteSourceAsync(item.Entry with { Sha1 = fixtures.Single(value => value.RelativePath == item.Entry.RelativePath).Sha1 }, ct));
        await File.WriteAllTextAsync(Path.Combine(artifacts, "cleanup.json"), JsonSerializer.Serialize(new { exactIdentities = cleanup.Count, independentlyVerified = true, oneDriveFoldersRetained = true }), token);
    }
    return 0;
}
finally { foreach (var value in disposable) value.Dispose(); }

string? Argument(string name) { var index = Array.IndexOf(args, name); return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing value for " + name); }
sealed class Protector : ITransferCheckpointProtector
{
    private static readonly byte[] Entropy = "CloudBay.Throughput.v1"u8.ToArray();
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}
