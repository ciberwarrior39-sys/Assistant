using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Assistant.Core.Actions;
using Assistant.Core.Diagnostics;

namespace Assistant.App;

/// <summary>
/// "My internet isn't working": checks step by step, the way a technician would, and says where it breaks.
/// 1. Is the PC on a network at all (Wi-Fi or cable), and how strong is the Wi-Fi?
/// 2. Did the network give it an address, and does the router answer?
/// 3. Does the internet beyond the router answer (Cloudflare's and Google's public addresses)?
/// 4. Are website names turned into addresses (DNS)?
/// 5. Do websites load, or does a login page get in the way (hotel or café Wi-Fi)? This is the same small test Windows
///    itself makes (msftconnecttest.com).
/// Nothing needs administrator rights. The speed test (download and upload with Cloudflare) runs only when asked.
/// </summary>
static partial class InternetCheck
{
    const string ConnectTestUrl = "http://www.msftconnecttest.com/connecttest.txt";
    const string ConnectTestText = "Microsoft Connect Test";
    const string LoginPageUrl = "http://www.msftconnecttest.com/redirect";
    const string DownloadUrl = "https://speed.cloudflare.com/__down?bytes=50000000";
    const string UploadUrl = "https://speed.cloudflare.com/__up";
    const int SpeedStreams = 6;                                        // parallel transfers, like speed test sites
    static readonly TimeSpan SpeedWarmUp = TimeSpan.FromSeconds(1.5);  // connections speed up first: not counted
    static readonly TimeSpan SpeedMaxTime = TimeSpan.FromSeconds(8);
    const long DownloadMaxBytes = 300_000_000;                         // stops early on a fast line
    const long UploadMaxBytes = 150_000_000;
    const int UploadChunkBytes = 10_000_000;                           // one upload request
    const int WeakSignal = 40; // percent

    static readonly IPAddress[] PublicAddresses = [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")];

    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = true })
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    public static async Task<ActionReply> CheckAsync(CancellationToken ct)
    {
        var steps = new List<string>();
        var options = new List<ActionOption>();
        string verdict;

        // 1. A network at all.
        List<NetworkInterface> all = ActiveAdapters();
        string? vpn = all.FirstOrDefault(adapter => VpnAdapter().IsMatch(adapter.Description + " " + adapter.Name))?.Name;
        // The real connection: not the VPN's own adapter.
        List<NetworkInterface> up = all.Where(adapter => !VpnAdapter().IsMatch(adapter.Description + " " + adapter.Name)).ToList();
        NetworkInterface? main = up.FirstOrDefault(adapter => adapter.GetIPProperties().GatewayAddresses.Any(IsUsableGateway)) ?? up.FirstOrDefault();
        WifiInfo? wifi = await Task.Run(Wifi, ct);
        if (main is null)
        {
            DiagnosticLog.Info("Internet check-up: not connected to any network");
            return new ActionReply(
                "Your PC isn't connected to any network: Wi-Fi is off or not connected, or the cable is unplugged.\n\n" +
                "- Check that Wi-Fi is on (the network icon at the bottom right) and pick your network.\n" +
                "- Check that Airplane mode is off.\n" +
                "- With a cable: check both ends are clicked in.")
            {
                Options = [new ActionOption("Wi-Fi settings", new OpenSettings("Wi-Fi", "ms-settings:network-wifi")),
                           new ActionOption("Network settings", new OpenSettings("Network", "ms-settings:network-status"))],
            };
        }

        bool onWifi = main.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        string network = onWifi
            ? $"Wi-Fi{(wifi?.Name is { Length: > 0 } name ? $" \"{name}\"" : "")}{(wifi?.Signal is { } signal ? $", signal {signal}%" : "")}"
            : "a cable";
        steps.Add($"✓ Connected to {network}.");
        bool weak = onWifi && wifi?.Signal is < WeakSignal;
        if (weak) steps.Add("! The Wi-Fi signal is weak. Closer to the router, or fewer walls in between, makes a big difference.");

        // 2. An address and the router.
        IPAddress? gateway = main.GetIPProperties().GatewayAddresses.Select(address => address.Address).FirstOrDefault(IsUsable);
        bool selfAssigned = main.GetIPProperties().UnicastAddresses.Any(address =>
            address.Address.AddressFamily == AddressFamily.InterNetwork && address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        if (gateway is null || selfAssigned)
        {
            steps.Add("✗ The network didn't give your PC an address, so it can't reach anything.");
            DiagnosticLog.Info("Internet check-up: no address from the network");
            return new ActionReply(
                "Your PC is connected, but the network didn't let it in properly (no address from the router).\n\n" +
                string.Join("\n", steps) + "\n\n" +
                "What usually fixes it:\n" +
                "- Disconnect from the Wi-Fi and connect again.\n" +
                "- Restart the router: unplug it, wait 30 seconds, plug it back in, and give it two minutes.\n" +
                "- Restart the PC.")
            {
                Options = [new ActionOption("Wi-Fi settings", new OpenSettings("Wi-Fi", "ms-settings:network-wifi"))],
            };
        }

        Task<PingResult> routerPing = PingAsync(gateway, 3);
        Task<PingResult> internetPing = PingAnyAsync(PublicAddresses, 4);
        Task<bool> dns = ResolvesAsync("www.google.com", ct);
        Task<WebResult> web = WebAsync(ct);
        await Task.WhenAll(routerPing, internetPing, dns, web);

        PingResult router = routerPing.Result, internet = internetPing.Result;
        steps.Add(router.Answered
            ? $"✓ The router answers ({Ms(router.Milliseconds)})."
            : "? The router didn't answer pings (some routers never do).");
        steps.Add(internet.Answered
            ? $"✓ The internet answers ({Ms(internet.Milliseconds)}{(internet.LossPercent > 0 ? $", {internet.LossPercent}% of tries lost" : "")})."
            : "✗ The internet beyond the router doesn't answer.");
        steps.Add(dns.Result ? "✓ Website names are found (DNS)." : "✗ Website names aren't found (DNS).");
        steps.Add(web.Result switch
        {
            WebResult.Works => "✓ Websites load.",
            WebResult.LoginPage => "✗ A login page gets in the way of websites.",
            _ => "✗ Websites don't load.",
        });

        bool proxy = ProxyOn();

        if (web.Result == WebResult.LoginPage)
        {
            verdict = "This network wants you to sign in or accept its terms first (common in hotels, cafés and trains). Open the login page and the internet will work.";
            options.Add(new ActionOption("Open the login page", new OpenWebPage(LoginPageUrl)));
        }
        else if (web.Result == WebResult.Works)
        {
            bool slowish = internet.Answered && (internet.Milliseconds > 100 || internet.LossPercent >= 25);
            verdict = slowish
                ? "The internet works, but the line is struggling right now" +
                  (internet.LossPercent >= 25 ? " (some data gets lost on the way)" : $" (it takes {internet.Milliseconds} ms to answer; under 50 is good)") +
                  ". Something else may be using it (a download, an update, streaming on another device), or the provider is busy."
                : "The internet works right now. If it's slow, the speed test shows how fast it really is.";
            if (weak) verdict += " The weak Wi-Fi signal is the most likely reason it feels slow.";
            options.Add(new ActionOption("Test the speed (up to 450 MB of data)", new SpeedTest()));
        }
        else if (internet.Answered && !dns.Result)
        {
            verdict = "The internet is there, but website names aren't being found (a DNS problem). Clearing Windows' list of " +
                      "remembered addresses often fixes it; if not, restarting the router does.";
            options.Add(new ActionOption("Clear the DNS list", new FlushDns()));
        }
        else if (!internet.Answered && router.Answered)
        {
            verdict = "Your PC and the Wi-Fi are fine, and the router answers, but the router has no internet. Restart the router " +
                      "(unplug it, wait 30 seconds, plug it back in, then give it two minutes). If that doesn't help, your provider may have an outage.";
        }
        else if (!internet.Answered)
        {
            verdict = "Nothing beyond your PC answers. Restart the router (unplug it, wait 30 seconds, plug it back in, give it two minutes), " +
                      "then disconnect and reconnect the Wi-Fi.";
        }
        else
        {
            verdict = "The internet answers, but websites don't load. A VPN, proxy or security program may be blocking them. " +
                      "Clearing the DNS list or restarting the browser can also help.";
            options.Add(new ActionOption("Clear the DNS list", new FlushDns()));
        }

        var notes = new List<string>();
        if (vpn is not null) notes.Add($"A VPN is on ({vpn}). If things are slow or don't load, try turning it off for a moment.");
        if (proxy) notes.Add("A proxy is set in Windows (Settings > Network > Proxy). If you didn't set it up, it can block or slow websites.");
        if (onWifi) options.Add(new ActionOption("Wi-Fi settings", new OpenSettings("Wi-Fi", "ms-settings:network-wifi")));
        if (proxy) options.Add(new ActionOption("Proxy settings", new OpenSettings("Proxy", "ms-settings:network-proxy")));

        DiagnosticLog.Info($"Internet check-up: router {router.Answered}, internet {internet.Answered} {internet.Milliseconds} ms, " +
                           $"dns {dns.Result}, web {web.Result}, wifi signal {wifi?.Signal}");
        string text = verdict + "\n\n" + string.Join("\n", steps) + (notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : "");
        return new ActionReply(text) { Options = options };
    }

    /// <summary>Empties the DNS cache (no administrator rights needed), then checks a name resolves again.</summary>
    public static async Task<ActionReply> FlushDnsAsync(CancellationToken ct)
    {
        bool flushed = DnsFlushResolverCache();
        bool works = await ResolvesAsync("www.google.com", ct);
        DiagnosticLog.Info($"DNS cache flushed: {flushed}, resolves now: {works}");
        if (!flushed) return new ActionReply("Windows didn't let me clear the DNS list. Restarting the router or the PC does the same.");
        return new ActionReply(works
            ? "Cleared the DNS list, and website names are found again. Try the website now."
            : "Cleared the DNS list, but names still aren't found. Restart the router (unplug for 30 seconds); if it keeps happening, " +
              "setting the DNS to 1.1.1.1 in the Wi-Fi's settings usually ends it.")
        {
            Options = works ? [] : [new ActionOption("Network settings", new OpenSettings("Network", "ms-settings:network-status"))],
        };
    }

    /// <summary>
    /// Measures the speed the way speed test sites do, against Cloudflare: the delay first (ping), then the download and
    /// the upload, each with six transfers at once (one connection alone can't fill a fast line), the first 1.5 seconds
    /// left out (connections start slow and speed up), then the average over the rest, for at most 8 seconds each, or
    /// 300 MB down and 150 MB up, whichever comes first. Only when asked: Cloudflare sees the requests, like any website.
    /// </summary>
    public static async Task<ActionReply> SpeedTestAsync(CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = SpeedStreams })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11, // separate connections, not one shared HTTP/2 connection
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        PingResult ping = await PingAnyAsync(PublicAddresses, 4);

        var buffer = new byte[81920];
        (double download, long downloaded) = await MeasureAsync(async (count, token) =>
        {
            using HttpResponseMessage response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using Stream body = await response.Content.ReadAsStreamAsync(token);
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, token)) > 0)
                if (!count(read)) break;
        }, DownloadMaxBytes, ct);

        if (downloaded == 0)
            return new ActionReply("The speed test didn't get through, so the connection is either down or very slow right now.");

        // Random bytes, so nothing on the way can shrink them; one block, sent over and over.
        Random.Shared.NextBytes(buffer);
        (double upload, long uploaded) = await MeasureAsync(async (count, token) =>
        {
            using var content = new CountingContent(buffer, UploadChunkBytes, count);
            using HttpResponseMessage response = await http.PostAsync(UploadUrl, content, token);
            response.EnsureSuccessStatusCode();
        }, UploadMaxBytes, ct);

        DiagnosticLog.Info($"Speed test: down {download:0} Mbps, up {upload:0} Mbps, ping {ping.Milliseconds} ms " +
                           $"({(downloaded + uploaded) / 1_000_000} MB, {SpeedStreams} streams)");

        string downMeaning = download switch
        {
            < 5 => "slow: web pages will crawl and video will stutter",
            < 25 => "fine for browsing and one HD video, but tight for several people or big downloads",
            < 100 => "good: enough for streaming and gaming",
            _ => "fast",
        };
        string upMeaning = uploaded == 0 ? ""
            : upload switch
            {
                < 3 => " Upload is slow: video calls may break up, and sending big files takes a while.",
                < 20 => " Upload is fine for video calls.",
                _ => " Upload is good: video calls, streaming and sending big files are no problem.",
            };
        string pingText = ping.Answered ? $"\nPing: {Ms(ping.Milliseconds)}{(ping.Milliseconds > 60 ? " (a bit slow for online games)" : "")}" : "";
        string uploadText = uploaded == 0 ? "\nUpload: couldn't be measured this time." : $"\nUpload: about {upload:0} Mbps";

        return new ActionReply($"Download: about {download:0} Mbps{uploadText}{pingText}\n\n" +
                               $"The download is {downMeaning}.{upMeaning}\n\n" +
                               $"(Measured with Cloudflare over {SpeedStreams} connections, using {(downloaded + uploaded) / 1_000_000:0} MB of data. " +
                               "Speed test sites can differ a little, depending on which server they use. Compare it with what your " +
                               "plan promises: much less, again and again, is worth a call to the provider; on Wi-Fi, try it next to the router too.)");
    }

    /// <summary>
    /// Runs <paramref name="transfer"/> over and over on <see cref="SpeedStreams"/> streams at once and measures the
    /// speed after the warm-up. The transfer reports each block through its counter, which says false once the cap is
    /// reached. Returns megabits per second and the bytes moved (0 when nothing got through).
    /// </summary>
    static async Task<(double Mbps, long Bytes)> MeasureAsync(Func<Func<int, bool>, CancellationToken, Task> transfer, long maxBytes, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var counter = new long[1]; // shared by the streams; an array so the lambdas can add to it
        int failures = 0;

        bool Count(int bytes) => Interlocked.Add(ref counter[0], bytes) < maxBytes;

        async Task StreamAsync()
        {
            while (!stop.IsCancellationRequested && Interlocked.Read(ref counter[0]) < maxBytes)
            {
                try
                {
                    await transfer(Count, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    if (Interlocked.Increment(ref failures) >= SpeedStreams * 2) return;
                }
            }
        }

        var timer = Stopwatch.StartNew();
        Task all = Task.WhenAll(Enumerable.Range(0, SpeedStreams).Select(_ => Task.Run(StreamAsync)));

        // Leave out the start, then count what moves until the time or the cap is reached.
        await Task.WhenAny(all, Task.Delay(SpeedWarmUp, ct));
        long startBytes = Interlocked.Read(ref counter[0]);
        TimeSpan startTime = timer.Elapsed;
        await Task.WhenAny(all, Task.Delay(SpeedMaxTime - SpeedWarmUp, ct));
        long endBytes = Interlocked.Read(ref counter[0]);
        TimeSpan endTime = timer.Elapsed;
        stop.Cancel();
        try
        {
            await all;
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose.
        }

        if (endBytes == 0) return (0, 0);
        // A line so fast that the cap came before the warm-up ended: measure the whole run instead.
        (long bytes, double seconds) = endTime - startTime > TimeSpan.FromSeconds(0.5) && endBytes > startBytes
            ? (endBytes - startBytes, (endTime - startTime).TotalSeconds)
            : (endBytes, endTime.TotalSeconds);
        return (bytes * 8 / Math.Max(0.1, seconds) / 1_000_000, endBytes);
    }

    /// <summary>An upload body of <c>length</c> bytes, made of one block repeated, that counts what it hands to the network.</summary>
    sealed class CountingContent(byte[] block, long length, Func<int, bool> count) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            long left = length;
            while (left > 0)
            {
                int size = (int)Math.Min(block.Length, left);
                await stream.WriteAsync(block.AsMemory(0, size), ct);
                left -= size;
                count(size);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }

    static string Ms(long milliseconds) => milliseconds < 1 ? "under 1 ms" : $"{milliseconds} ms";

    // ---- The steps ----------------------------------------------------------------------------------------------

    // Network adapters that are up: not loopback or the virtual ones of VMs and WSL (VPNs stay, to be named).
    static List<NetworkInterface> ActiveAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                                  && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                  && !Virtual().IsMatch(adapter.Description + " " + adapter.Name))
                .OrderByDescending(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    static bool IsUsableGateway(GatewayIPAddressInformation gateway) => IsUsable(gateway.Address);

    static bool IsUsable(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any);

    // Internet Options' proxy for this user (most apps and browsers use it).
    static bool ProxyOn()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return key?.GetValue("ProxyEnable") is int enabled && enabled != 0;
    }

    readonly record struct PingResult(bool Answered, long Milliseconds, int LossPercent);

    static async Task<PingResult> PingAsync(IPAddress address, int tries)
    {
        using var ping = new Ping();
        var times = new List<long>();
        for (int i = 0; i < tries; i++)
        {
            try
            {
                PingReply reply = await ping.SendPingAsync(address, 1000);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch (PingException)
            {
                // Counts as lost.
            }
        }
        return times.Count == 0
            ? new PingResult(false, 0, 100)
            : new PingResult(true, (long)times.Average(), (tries - times.Count) * 100 / tries);
    }

    // The best of a few public addresses, so one being down doesn't look like the whole internet.
    static async Task<PingResult> PingAnyAsync(IEnumerable<IPAddress> addresses, int tries)
    {
        PingResult[] results = await Task.WhenAll(addresses.Select(address => PingAsync(address, tries)));
        return results.Where(result => result.Answered).OrderBy(result => result.LossPercent).ThenBy(result => result.Milliseconds)
            .FirstOrDefault(new PingResult(false, 0, 100));
    }

    static async Task<bool> ResolvesAsync(string host, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            return (await Dns.GetHostAddressesAsync(host, timeout.Token)).Length > 0;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    enum WebResult { Works, LoginPage, Fails }

    // Windows' own connection test: the right text means the internet works; a redirect or other text means a login page.
    static async Task<WebResult> WebAsync(CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(ConnectTestUrl, ct);
            if ((int)response.StatusCode is >= 300 and < 400) return WebResult.LoginPage;
            string body = await response.Content.ReadAsStringAsync(ct);
            return body.Trim().StartsWith(ConnectTestText, StringComparison.Ordinal) ? WebResult.Works : WebResult.LoginPage;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return WebResult.Fails;
        }
    }

    sealed record WifiInfo(string? Name, int? Signal);

    // "netsh wlan show interfaces": the network's name and the signal. Labels are translated on Windows in other
    // languages, so the signal is the one value ending in "%", and the name is the line starting with "SSID".
    // Newer Windows may hide the name unless location access is allowed; then only the signal shows.
    static WifiInfo? Wifi()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (process is null) return null;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            string? name = null;
            int? signal = null;
            foreach (string line in output.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string label = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
                if (label.Equals("SSID", StringComparison.OrdinalIgnoreCase) && value.Length > 0) name ??= value;
                else if (Percent().Match(value) is { Success: true } percent) signal ??= int.Parse(percent.Groups[1].Value);
            }
            return name is null && signal is null ? null : new WifiInfo(name, signal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^(\d{1,3})\s*%$")]
    private static partial Regex Percent();

    [GeneratedRegex(@"virtual|hyper-v|vmware|virtualbox|wsl|vethernet|loopback|bluetooth|pseudo|teredo|isatap", RegexOptions.IgnoreCase)]
    private static partial Regex Virtual();

    [GeneratedRegex(@"\bvpn\b|wireguard|openvpn|\btap-|nordlynx|proton|mullvad|tailscale|zerotier|cisco anyconnect|fortinet|forticlient|globalprotect|wintun", RegexOptions.IgnoreCase)]
    private static partial Regex VpnAdapter();

    [DllImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DnsFlushResolverCache();
}
