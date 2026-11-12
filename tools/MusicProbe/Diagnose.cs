using System.Net.Http;
using System.Runtime.InteropServices;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using ManagedBass;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tools.MusicProbe;

/// <summary>
/// BASS 建流诊断：逐个尝试不同的自定义头与缓冲组合，定位 URL 流失败的原因。
/// </summary>
internal static class Diagnose
{
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken ct)
    {
        Console.WriteLine("== BASS 建流诊断 ==");
        string url;
        if (options.Url is not null)
        {
            url = options.Url.ToString();
            Console.WriteLine($"地址：{url}");
        }
        else
        {
            var handler = new HttpClientHandler();
            using var service = new MusicService(
                handler,
                new FakeAudioEngine(),
                new FakeCredentialStore(),
                NullLogger<MusicService>.Instance,
                TimeSpan.FromMinutes(5));

            var info = await service.GetPlaybackUrlAsync(options.Bvs[0], ct);
            url = info.Url.ToString();
            Console.WriteLine($"BV：{info.Track.Bvid}");
            Console.WriteLine($"地址：{url[..Math.Min(120, url.Length)]}…（{url.Length} 字符）");
        }

        if (!Bass.Init())
        {
            Console.WriteLine($"BASS 初始化失败：{Bass.LastError}");
            return 2;
        }

        Bass.NetTimeOut = 8000;
        Bass.NetReadTimeOut = 15000;
        Bass.NetPreBuffer = 5;
        try
        {
            var request = BassAudioEngine.BuildUrlRequest(url);
            var variants = new (string Name, string Request)[]
            {
                ("URL only", url),
                ("Referer only", url + "\r\nReferer: https://www.bilibili.com\r\n"),
                ("Referer + UA", request),
                ("Referer + UA + blank", request + "\r\n"),
                ("Referer slash + UA", url + "\r\nReferer: https://www.bilibili.com/\r\nUser-Agent: " + BassAudioEngine.UserAgent + "\r\n"),
            };

            var flagSets = new (string Name, BassFlags Flags)[]
            {
                ("Default", BassFlags.Default),
                ("Blocks", BassFlags.StreamDownloadBlocks),
                ("Prescan", BassFlags.Prescan),
                ("Prescan+Blocks", BassFlags.Prescan | BassFlags.StreamDownloadBlocks),
                ("Restrict", BassFlags.RestrictDownloadRate),
                ("Restrict+Blocks", BassFlags.RestrictDownloadRate | BassFlags.StreamDownloadBlocks),
            };

            foreach (var (name, value) in variants)
            {
                foreach (var (flagName, flags) in flagSets)
                {
                    Try(name, value, flags, flagName);
                }
            }

            Bass.NetAgent = BassAudioEngine.UserAgent;
            Try("NetAgent + Referer", url + "\r\nReferer: https://www.bilibili.com\r\n", BassFlags.Default, "Default");
            Bass.NetAgent = "BASS/2.4";

            TryAddOn("AAC MP4 add-on", () => AacNative.Mp4Url(request, 0, BassFlags.Default, IntPtr.Zero, IntPtr.Zero));
            TryAddOn("AAC add-on", () => AacNative.AacUrl(request, 0, BassFlags.Default, IntPtr.Zero, IntPtr.Zero));

            Console.WriteLine("下载到本地后用文件流验证解码…");
            using var http = new HttpClient();
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
            requestMessage.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
            requestMessage.Headers.TryAddWithoutValidation("User-Agent", BassAudioEngine.UserAgent);
            using var response = await http.SendAsync(requestMessage, ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var label = options.Url is null ? options.Bvs[0] : "local";
            var path = Path.Combine(Path.GetTempPath(), "bili-music-probe-" + label + ".m4s");
            await File.WriteAllBytesAsync(path, bytes, ct);
            Console.WriteLine($"本地文件：{path}（{bytes.Length:N0} 字节，HTTP {(int)response.StatusCode}）");
            TryFile("local file", path);
        }
        finally
        {
            Bass.Free();
        }

        return 0;

        static void Try(string name, string value, BassFlags flags, string flagName)
        {
            var handle = Bass.CreateStream(value, 0, flags, null);
            if (handle == 0)
            {
                Console.WriteLine($"   FAIL  {name,-28} {flagName,-18} {Bass.LastError}");
                return;
            }

            Bass.ChannelGetInfo(handle, out var channelInfo);
            var length = Bass.ChannelGetLength(handle);
            Console.WriteLine(
                $"   PASS  {name,-28} {flagName,-18} {channelInfo.Frequency}Hz/{channelInfo.Channels}ch length={length}");
            Bass.StreamFree(handle);
        }

        static void TryAddOn(string name, Func<int> create)
        {
            try
            {
                var handle = create();
                if (handle == 0)
                {
                    Console.WriteLine($"   FAIL  {name,-28} {Bass.LastError}");
                    return;
                }

                Bass.ChannelGetInfo(handle, out var channelInfo);
                var length = Bass.ChannelGetLength(handle);
                Console.WriteLine(
                    $"   PASS  {name,-28} {channelInfo.Frequency}Hz/{channelInfo.Channels}ch length={length}");
                Bass.StreamFree(handle);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   FAIL  {name,-28} {ex.GetType().Name}: {ex.Message}");
            }
        }

        static void TryFile(string name, string path)
        {
            var handle = Bass.CreateStream(path, 0, 0, BassFlags.Default);
            if (handle == 0)
            {
                Console.WriteLine($"   FAIL  {name,-28} {Bass.LastError}");
                return;
            }

            Bass.ChannelGetInfo(handle, out var channelInfo);
            Console.WriteLine($"   PASS  {name,-28} {channelInfo.Frequency}Hz/{channelInfo.Channels}ch");
            Bass.StreamFree(handle);
        }
    }

    private static class AacNative
    {
        [DllImport("bass_aac", CharSet = CharSet.Unicode, EntryPoint = "BASS_MP4_StreamCreateURL")]
        public static extern int Mp4Url(string url, int offset, BassFlags flags, IntPtr procedure, IntPtr user);

        [DllImport("bass_aac", CharSet = CharSet.Unicode, EntryPoint = "BASS_AAC_StreamCreateURL")]
        public static extern int AacUrl(string url, int offset, BassFlags flags, IntPtr procedure, IntPtr user);
    }
}
