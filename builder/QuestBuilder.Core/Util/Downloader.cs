using System.Net;
using System.Net.Http.Headers;

namespace LCQuest.Builder;

public readonly record struct DownloadProgress(long Done, long? Total);

/// <summary>HTTP downloads that resume after a broken connection and check the SHA-256 pinned in kit.json</summary>
public static class Downloader
{
    public static HttpClient Http { get; } = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LCQuestBuilder", Platform.BuilderVersion));
        return client;
    }

    /// <summary>
    /// How long a download may go without a byte before the connection is dropped and retried: some networks let the
    /// connection open and then stall it for good (Cloudflare-hosted servers from Russia), and without this limit the
    /// build waits forever
    /// </summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>The pause before the first retry of a broken download; the next ones wait longer</summary>
    public static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    public static Task<string> DownloadAsync(string url, string target, string? sha256,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
        DownloadAsync([url], target, sha256, progress, ct);

    /// <summary>
    /// Downloads the file to <paramref name="target"/> unless a file with the expected hash is already there, from the
    /// first of <paramref name="urls"/> that works (the rest are mirrors of the same file). A partial download (.part)
    /// is resumed. A file whose SHA-256 does not match is deleted and reported.
    /// </summary>
    public static async Task<string> DownloadAsync(IReadOnlyList<string> urls, string target, string? sha256,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (File.Exists(target) && (sha256 is null || Hashing.Sha256File(target) == sha256))
            return target;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var part = target + ".part";

        for (var source = 0; ; source++)
        {
            var url = urls[source];
            Log.Info($"downloading {url}");
            try
            {
                await DownloadWithRetriesAsync(url, part, progress, ct);
                break;
            }
            catch (Exception e) when (IsNetworkFailure(e) && source + 1 < urls.Count && !ct.IsCancellationRequested)
            {
                Log.Warn($"could not download {url} ({e.Message}), trying a mirror");
            }
            catch (Exception e) when (IsNetworkFailure(e) && !ct.IsCancellationRequested)
            {
                throw new BuildException($"Could not download {url}: {e.Message}. If your network blocks it, download " +
                                         $"the file in a browser (or with a VPN) and save it as {target}", inner: e)
                    { Code = "download" };
            }
        }

        if (sha256 is not null)
        {
            var actual = Hashing.Sha256File(part);
            if (actual != sha256)
            {
                File.Delete(part);
                throw new BuildException($"{Path.GetFileName(target)} is not the expected file (SHA-256 {actual}, " +
                                         $"expected {sha256})", urls[0]) { Code = "download-hash" };
            }
        }
        File.Move(part, target, overwrite: true);
        return target;
    }

    private static bool IsNetworkFailure(Exception e) => e is HttpRequestException or IOException or TimeoutException;

    private static async Task DownloadWithRetriesAsync(string url, string part, IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, part, progress, ct);
                return;
            }
            catch (Exception e) when (IsNetworkFailure(e) && attempt < 4 && !ct.IsCancellationRequested)
            {
                Log.Warn($"download interrupted ({e.Message}), retrying ({attempt}/3)");
                await Task.Delay(RetryDelay * attempt, ct);
            }
        }
    }

    private static async Task DownloadOnceAsync(string url, string part, IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        try
        {
            await DownloadOnceAsync(url, part, progress, ct, StallTimeout);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no data for {StallTimeout.TotalSeconds:0} s");
        }
    }

    private static async Task DownloadOnceAsync(string url, string part, IProgress<DownloadProgress>? progress,
        CancellationToken ct, TimeSpan stallTimeout)
    {
        // Restarted before every wait on the server: cancels the request when nothing arrives for stallTimeout
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(stallTimeout);
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(part);  // the server's file changed or the part is complete garbage: start over
            throw new IOException("range not satisfiable");
        }
        response.EnsureSuccessStatusCode();

        var resumed = response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
            existing = 0;
        var total = response.Content.Headers.ContentLength is { } length ? existing + length : (long?)null;

        await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
        await using var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write,
            FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        var done = existing;
        var lastReport = DateTime.MinValue;
        int read;
        while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
        {
            stall.CancelAfter(stallTimeout);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
            {
                progress?.Report(new DownloadProgress(done, total));
                lastReport = DateTime.UtcNow;
            }
        }
        progress?.Report(new DownloadProgress(done, total));
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        try
        {
            return await Http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException e)
        {
            throw new BuildException($"Could not download {url}: {e.Message}", inner: e) { Code = "download" };
        }
    }
}
