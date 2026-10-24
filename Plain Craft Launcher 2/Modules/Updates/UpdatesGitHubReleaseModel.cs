using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

public class UpdatesGitHubReleaseModel : IUpdateSource // GitHub Releases 更新格式
{
    private const string ReleasesUrl = "https://api.github.com/repos/zitzhen/PCL-CE-Unlock/releases?per_page=30";

    private JsonArray? _releases;

    private DateTime _cacheTime;

    public string SourceName { get; set; } = "GitHub Releases";

    public bool IsAvailable()
    {
        return true;
    }

    public bool RefreshCache()
    {
        _releases = Requester.FetchJson(ReleasesUrl, new RequestParam
        {
            Retries = 3,
            UseBrowserUserAgent = true
        }) as JsonArray;
        if (_releases is null)
            throw new Exception("无法获取 GitHub Release 列表");
        _cacheTime = DateTime.Now;
        return true;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var release = GetRelease(channel);
        var asset = GetAsset(release, channel, arch);
        if (asset is null)
            throw new NullReferenceException("未找到与该架构匹配的更新文件");
        var digest = asset["digest"]?.ToString();
        return new VersionDataModel
        {
            VersionName = release["tag_name"]!.ToString().TrimStart('v'),
            VersionCode = 0,
            Sha256 = string.IsNullOrWhiteSpace(digest) ? null!
                : digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : digest,
            Source = SourceName,
            Changelog = release["body"]?.ToString() ?? ""
        };
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        var latestVersion = GetLatestVersion(channel, arch);
        return currentVersion >= SemVer.Parse(latestVersion.VersionName);
    }

    public VersionAnnouncementDataModel GetAnnouncementList()
    {
        return new VersionAnnouncementDataModel { Content = new List<VersionAnnouncementContentModel>() };
    }

    public List<ModLoader.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var loaders = new List<ModLoader.LoaderBase>();
        loaders.Add(new ModLoader.LoaderTask<int, List<DownloadFile>>(Lang.Text("Update.Task.GetDownloadInfo"), load =>
        {
            var asset = GetAsset(GetRelease(channel), channel, arch);
            var url = asset?["browser_download_url"]?.ToString();
            if (string.IsNullOrWhiteSpace(url))
                throw new Exception("GitHub Release 中没有可下载的更新文件");
            load.output = new List<DownloadFile> { new(new[] { url }, output) };
        }));
        loaders.Add(new LoaderDownload(Lang.Text("Update.Task.DownloadUpdateFile"), new List<DownloadFile>()));
        return loaders;
    }

    private void EnsureCache()
    {
        if (_releases is null || (DateTime.Now - _cacheTime).TotalMinutes > 10)
            RefreshCache();
    }

    private JsonObject GetRelease(UpdateChannel channel)
    {
        EnsureCache();
        var releases = _releases!;
        var isBeta = channel == UpdateChannel.beta;
        var release = releases.OfType<JsonObject>()
            .FirstOrDefault(x => IsPublished(x) && x["prerelease"]!.GetValue<bool>() == isBeta);
        // Beta 通道没有 Beta 版本时，回退到最新的正式版
        if (release is null && isBeta)
            release = releases.OfType<JsonObject>()
                .FirstOrDefault(x => IsPublished(x) && !x["prerelease"]!.GetValue<bool>());
        return release ?? throw new NullReferenceException("未找到可用的 Release");
    }

    private static bool IsPublished(JsonObject release)
    {
        return release["draft"] is null || !release["draft"]!.GetValue<bool>();
    }

    private static JsonObject? GetAsset(JsonObject release, UpdateChannel channel, UpdateArch arch)
    {
        if (release["assets"] is not JsonArray assets) return null;
        var archToken = arch == UpdateArch.arm64 ? "_arm64" : "_x64";
        var channelToken = channel == UpdateChannel.beta ? "_Beta_" : "_Release_";
        var exes = assets.OfType<JsonObject>()
            .Where(x => (x["name"]?.ToString() ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return exes.FirstOrDefault(x => MatchName(x, archToken) && MatchName(x, channelToken))
               ?? exes.FirstOrDefault(x => MatchName(x, archToken));
    }

    private static bool MatchName(JsonObject asset, string token)
    {
        return (asset["name"]?.ToString() ?? "").Contains(token, StringComparison.OrdinalIgnoreCase);
    }
}
