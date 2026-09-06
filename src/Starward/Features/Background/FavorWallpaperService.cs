using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.Blackboard;
using Starward.Features.Database;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.Features.Background;

/// <summary>
/// 绝区零百科「好感壁纸」与「满影画壁纸」：拉取列表 / 词条媒体、按数量持久化、下载与设为背景。
/// </summary>
internal partial class FavorWallpaperService
{

    public const string CacheKey = "FavorWallpaper:nap";

    public const string MindscapeCacheKey = "MindscapeWallpaper:nap";

    /// <summary>绝区零档案频道（含子频道「好感壁纸」）。</summary>
    public const int ArchiveChannelId = 13;

    public const int FavorWallpaperChannelId = 99;

    /// <summary>游戏图鉴「地图」频道，含各角色「密友同行」条目。</summary>
    public const int MapChannelId = 97;

    /// <summary>百科「代理人」频道（<c>/zzz/wiki/channel/map/2/43</c>）。</summary>
    public const int AgentChannelId = 43;

    /// <summary>预告角色角标，满影画列表中排除。</summary>
    public const string PreviewCornerMark = "Pre";

    public const string WikiAppSn = "zzz_wiki";

    public const string WikiApp = "zzz";


    private readonly ILogger<FavorWallpaperService> _logger;

    private readonly BlackboardClient _client;

    private readonly HttpClient _httpClient;


    public FavorWallpaperService(ILogger<FavorWallpaperService> logger, BlackboardClient client, HttpClient httpClient)
    {
        _logger = logger;
        _client = client;
        _httpClient = httpClient;
    }


    /// <summary>
    /// 是否为绝区零（好感壁纸仅该游戏百科提供）。
    /// </summary>
    public static bool IsSupported(GameBiz gameBiz) => gameBiz.Game is GameBiz.nap;


    /// <summary>
    /// 获取好感壁纸列表。先拉频道列表比对数量，仅当本地条数不一致（或强制刷新）时回源词条更新视频链接。
    /// </summary>
    /// <param name="forceRefresh">为 true 时忽略数量判断，补齐缺失词条并重配封面。</param>
    /// <param name="progress">词条拉取进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<FavorWallpaperRecord>> GetWallpapersAsync(
        bool forceRefresh = false,
        IProgress<FavorWallpaperLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        List<FavorWallpaperRecord> local = LoadCache(CacheKey);

        BlackboardContentListData list;
        try
        {
            list = await _client.GetHomeContentListAsync(WikiAppSn, ArchiveChannelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (local.Count > 0)
        {
            _logger.LogWarning(ex, "Load favor wallpaper list failed, using cached {Count} items", local.Count);
            progress?.Report(new FavorWallpaperLoadProgress(local.Count, local.Count, FromCache: true));
            return local;
        }

        IReadOnlyList<BlackboardContentItem> miyouItems = [];
        try
        {
            BlackboardContentListData map = await _client.GetHomeContentListAsync(WikiAppSn, MapChannelId, cancellationToken).ConfigureAwait(false);
            miyouItems = EnumerateMiyouItems(map);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load 密友同行 list failed, covers will fall back to wallpaper icons");
        }

        List<BlackboardContentItem> remoteItems = list.FindChannel(FavorWallpaperChannelId)?.List ?? [];

        if (!forceRefresh && local.Count > 0 && local.Count == remoteItems.Count)
        {
            // 视频地址不回源；封面只认密友同行，否则用壁纸图标。
            if (RematchCovers(local, miyouItems))
            {
                SaveCache(CacheKey, local);
            }
            progress?.Report(new FavorWallpaperLoadProgress(local.Count, local.Count, FromCache: true));
            return local;
        }

        Dictionary<int, FavorWallpaperRecord> localById = local.ToDictionary(x => x.ContentId);
        var result = new List<FavorWallpaperRecord>(remoteItems.Count);
        var toFetch = new List<BlackboardContentItem>();

        foreach (BlackboardContentItem item in remoteItems)
        {
            if (localById.TryGetValue(item.ContentId, out FavorWallpaperRecord? cached) && !string.IsNullOrWhiteSpace(cached.VideoUrl))
            {
                cached.Title = item.Title;
                cached.CharacterName = ExtractCharacterName(item.Title);
                cached.IconUrl = item.Icon;
                cached.CoverUrl = FindMiyouCover(cached.CharacterName, miyouItems) ?? item.Icon ?? cached.CoverUrl;
                result.Add(cached);
            }
            else
            {
                toFetch.Add(item);
            }
        }

        int done = result.Count;
        int total = remoteItems.Count;
        progress?.Report(new FavorWallpaperLoadProgress(done, total, FromCache: false));

        if (toFetch.Count > 0)
        {
            var fetched = new ConcurrentBag<FavorWallpaperRecord>();
            await Parallel.ForEachAsync(
                toFetch,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (item, token) =>
                {
                    try
                    {
                        WikiEntryPageData page = await _client.GetEntryPageAsync(WikiApp, WikiAppSn, item.ContentId, cancellationToken: token).ConfigureAwait(false);
                        string? video = WikiEntryVideo.ExtractMp4Url(page.Page);
                        if (string.IsNullOrWhiteSpace(video))
                        {
                            _logger.LogWarning("Favor wallpaper {Id} ({Title}) has no mp4", item.ContentId, item.Title);
                            return;
                        }
                        string name = ExtractCharacterName(item.Title);
                        fetched.Add(new FavorWallpaperRecord
                        {
                            ContentId = item.ContentId,
                            Title = item.Title,
                            CharacterName = name,
                            VideoUrl = video,
                            IconUrl = item.Icon,
                            CoverUrl = FindMiyouCover(name, miyouItems) ?? item.Icon ?? "",
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Fetch favor wallpaper entry {Id} failed", item.ContentId);
                    }
                    finally
                    {
                        int current = Interlocked.Increment(ref done);
                        progress?.Report(new FavorWallpaperLoadProgress(current, total, FromCache: false));
                    }
                }).ConfigureAwait(false);

            result.AddRange(fetched);
        }

        Dictionary<int, int> order = remoteItems
            .Select((item, index) => (item.ContentId, index))
            .ToDictionary(x => x.ContentId, x => x.index);
        result = result.OrderBy(x => order.GetValueOrDefault(x.ContentId, int.MaxValue)).ToList();

        SaveCache(CacheKey, result);
        return result;
    }


    /// <summary>
    /// 获取满影画静态壁纸列表。来源为百科代理人频道，排除预告角色；
    /// 无「影画展示3」的角色（如铃、哲）不进入画廊。封面优先密友同行，否则用好感壁纸图标。
    /// </summary>
    /// <param name="forceRefresh">为 true 时忽略数量判断，补齐缺失词条并重配封面。</param>
    /// <param name="progress">词条拉取进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<FavorWallpaperRecord>> GetMindscapeWallpapersAsync(
        bool forceRefresh = false,
        IProgress<FavorWallpaperLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        List<FavorWallpaperRecord> local = LoadCache(MindscapeCacheKey);

        BlackboardContentListData list;
        try
        {
            list = await _client.GetHomeContentListAsync(WikiAppSn, AgentChannelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (local.Count > 0)
        {
            _logger.LogWarning(ex, "Load mindscape wallpaper list failed, using cached {Count} items", local.Count);
            List<FavorWallpaperRecord> cachedVisible = VisibleMindscape(local);
            progress?.Report(new FavorWallpaperLoadProgress(cachedVisible.Count, cachedVisible.Count, FromCache: true));
            return cachedVisible;
        }

        IReadOnlyList<BlackboardContentItem> miyouItems = [];
        try
        {
            BlackboardContentListData map = await _client.GetHomeContentListAsync(WikiAppSn, MapChannelId, cancellationToken).ConfigureAwait(false);
            miyouItems = EnumerateMiyouItems(map);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load 密友同行 list failed, covers will fall back to 好感壁纸 icons");
        }

        IReadOnlyList<BlackboardContentItem> favorItems = [];
        try
        {
            BlackboardContentListData archive = await _client.GetHomeContentListAsync(WikiAppSn, ArchiveChannelId, cancellationToken).ConfigureAwait(false);
            favorItems = archive.FindChannel(FavorWallpaperChannelId)?.List ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load 好感壁纸 list failed, covers without 密友同行 will use agent icons");
        }

        List<BlackboardContentItem> remoteItems = (list.FindChannel(AgentChannelId)?.List ?? [])
            .Where(static x => !IsPreviewAgent(x))
            .ToList();

        if (!forceRefresh && local.Count > 0 && local.Count == remoteItems.Count)
        {
            if (RematchCovers(local, miyouItems, favorItems))
            {
                SaveCache(MindscapeCacheKey, local);
            }
            List<FavorWallpaperRecord> visible = VisibleMindscape(local);
            progress?.Report(new FavorWallpaperLoadProgress(visible.Count, visible.Count, FromCache: true));
            return visible;
        }

        Dictionary<int, FavorWallpaperRecord> localById = local.ToDictionary(x => x.ContentId);
        var result = new List<FavorWallpaperRecord>(remoteItems.Count);
        var toFetch = new List<BlackboardContentItem>();

        foreach (BlackboardContentItem item in remoteItems)
        {
            string name = AgentCharacterName(item);
            if (localById.TryGetValue(item.ContentId, out FavorWallpaperRecord? cached) && !string.IsNullOrWhiteSpace(cached.ImageUrl))
            {
                cached.Title = item.Title;
                cached.CharacterName = name;
                cached.IconUrl = FindFavorWallpaperIcon(name, favorItems) ?? item.Icon;
                cached.CoverUrl = FindMiyouCover(name, miyouItems) ?? cached.IconUrl ?? cached.CoverUrl;
                cached.IsStatic = true;
                result.Add(cached);
            }
            else
            {
                toFetch.Add(item);
            }
        }

        int done = result.Count;
        int total = remoteItems.Count;
        progress?.Report(new FavorWallpaperLoadProgress(done, total, FromCache: false));

        if (toFetch.Count > 0)
        {
            var fetched = new ConcurrentBag<FavorWallpaperRecord>();
            await Parallel.ForEachAsync(
                toFetch,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (item, token) =>
                {
                    try
                    {
                        WikiEntryPageData page = await _client.GetEntryPageAsync(WikiApp, WikiAppSn, item.ContentId, cancellationToken: token).ConfigureAwait(false);
                        string name = AgentCharacterName(item);
                        string? image = WikiEntryMindscape.ExtractFullCinemaImage(page.Page);
                        string? favorIcon = FindFavorWallpaperIcon(name, favorItems);
                        fetched.Add(new FavorWallpaperRecord
                        {
                            ContentId = item.ContentId,
                            Title = item.Title,
                            CharacterName = name,
                            ImageUrl = image ?? "",
                            IconUrl = favorIcon ?? item.Icon,
                            CoverUrl = FindMiyouCover(name, miyouItems) ?? favorIcon ?? item.Icon ?? "",
                            IsStatic = true,
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Fetch mindscape wallpaper entry {Id} failed", item.ContentId);
                    }
                    finally
                    {
                        int current = Interlocked.Increment(ref done);
                        progress?.Report(new FavorWallpaperLoadProgress(current, total, FromCache: false));
                    }
                }).ConfigureAwait(false);

            result.AddRange(fetched);
        }

        Dictionary<int, int> order = remoteItems
            .Select((item, index) => (item.ContentId, index))
            .ToDictionary(x => x.ContentId, x => x.index);
        result = result.OrderBy(x => order.GetValueOrDefault(x.ContentId, int.MaxValue)).ToList();

        SaveCache(MindscapeCacheKey, result);
        return VisibleMindscape(result);
    }


    /// <summary>
    /// 同步读取本地已缓存的壁纸列表（不访问网络），供打开面板时立即呈现；数量比对与回源在后台异步进行。
    /// </summary>
    /// <param name="mindscape">true 取满影画静态壁纸，false 取好感动态壁纸。</param>
    /// <returns>本地缓存的记录；从未缓存过时为空列表。</returns>
    public IReadOnlyList<FavorWallpaperRecord> GetCachedWallpapers(bool mindscape)
    {
        List<FavorWallpaperRecord> local = LoadCache(mindscape ? MindscapeCacheKey : CacheKey);
        return mindscape ? VisibleMindscape(local) : local;
    }


    /// <summary>
    /// 已下载到本地 bg 目录的壁纸，即随机播放的候选。
    /// </summary>
    /// <param name="mindscape">true 取满影画静态壁纸，false 取好感动态壁纸。</param>
    public IReadOnlyList<FavorWallpaperRecord> GetDownloadedWallpapers(bool mindscape)
    {
        return GetCachedWallpapers(mindscape).Where(IsCached).ToList();
    }


    /// <summary>
    /// 随机播放候选池：按两个开关合并好感 / 满影画中已下载的壁纸。
    /// </summary>
    /// <param name="gameBiz">游戏业务线。</param>
    public List<FavorWallpaperRecord> GetShufflePool(GameBiz gameBiz)
    {
        var pool = new List<FavorWallpaperRecord>();
        if (AppConfig.GetFavorWallpaperShuffle(gameBiz))
        {
            pool.AddRange(GetDownloadedWallpapers(mindscape: false));
        }
        if (AppConfig.GetMindscapeWallpaperShuffle(gameBiz))
        {
            pool.AddRange(GetDownloadedWallpapers(mindscape: true));
        }
        return pool;
    }


    /// <summary>
    /// 从候选池里随机挑一张已下载的壁纸设为当前自定义背景。不联网、不下载。
    /// </summary>
    /// <param name="gameBiz">游戏业务线。</param>
    /// <returns>选中的背景文件名；候选不足（无已下载壁纸，或只有当前这一张）时为 null。</returns>
    public string? ApplyRandomWallpaper(GameBiz gameBiz)
    {
        List<string> names = GetShufflePool(gameBiz)
            .Select(GetCacheFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
        {
            return null;
        }
        // 多于一张时排除当前这张，避免「随机」之后画面没变。
        string? current = AppConfig.GetCustomBg(gameBiz);
        List<string> candidates = names.Count > 1
            ? names.Where(x => !string.Equals(x, current, StringComparison.OrdinalIgnoreCase)).ToList()
            : names;
        if (candidates.Count == 0)
        {
            return null;
        }
        string picked = candidates[Random.Shared.Next(candidates.Count)];
        // 与 SetAsCustomBackgroundAsync 同样三连：bg_ 不跟着改的话，
        // GetSuggestedGameBackgroundAsync 会认为自定义背景已过期而回落到官方背景。
        AppConfig.SetCustomBg(gameBiz, picked);
        AppConfig.SetEnableCustomBg(gameBiz, true);
        AppConfig.SetBg(gameBiz, picked);
        _logger.LogInformation("Random wallpaper {Name} applied for {GameBiz}", picked, gameBiz);
        return picked;
    }


    /// <summary>
    /// 软件启动后首次显示该游戏背景时随机一张壁纸。每个进程每个游戏只随机一次，
    /// 从托盘恢复主窗口、来回切换游戏都不会再换（issue #15）。
    /// </summary>
    /// <param name="gameBiz">游戏业务线。</param>
    /// <returns>是否真的换了背景。</returns>
    public bool TryShuffleOnStartup(GameBiz gameBiz)
    {
        try
        {
            if (!IsSupported(gameBiz))
            {
                return false;
            }
            lock (_shuffledBizs)
            {
                if (!_shuffledBizs.Add(gameBiz))
                {
                    return false;
                }
            }
            // 用户已经切回官方背景，不越俎代庖把自定义背景重新打开。
            if (!AppConfig.GetEnableCustomBg(gameBiz))
            {
                return false;
            }
            return ApplyRandomWallpaper(gameBiz) is not null;
        }
        catch (Exception ex)
        {
            // 随机失败不能影响背景初始化。
            _logger.LogWarning(ex, "Shuffle wallpaper on startup failed for {GameBiz}", gameBiz);
            return false;
        }
    }


    /// <summary>本进程内已随机过背景的游戏。</summary>
    private static readonly HashSet<GameBiz> _shuffledBizs = [];


    /// <summary>
    /// 由媒体 URL 得到缓存文件名（与官方背景相同，落在 CacheFolder/bg）。
    /// </summary>
    public static string GetCacheFileName(FavorWallpaperRecord item)
    {
        string url = GetMediaUrl(item);
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            string name = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        return item.IsStatic ? $"zzz_mindscape_{item.ContentId}.png" : $"zzz_favor_{item.ContentId}.mp4";
    }


    /// <summary>
    /// 由视频 URL 得到缓存文件名（与官方背景相同，落在 CacheFolder/bg）。
    /// </summary>
    public static string GetVideoFileName(FavorWallpaperRecord item) => GetCacheFileName(item);


    /// <summary>
    /// 本地 bg 目录中是否已有该媒体文件。
    /// </summary>
    public static bool IsCached(FavorWallpaperRecord item)
    {
        string path = BackgroundService.GetBgFilePath(GetCacheFileName(item));
        return File.Exists(path);
    }


    /// <summary>
    /// 删除本地 bg 缓存。若文件正被背景播放占用，稍等后重试。
    /// </summary>
    public async Task DeleteLocalCacheAsync(FavorWallpaperRecord item, CancellationToken cancellationToken = default)
    {
        string path = BackgroundService.GetBgFilePath(GetCacheFileName(item));
        if (!File.Exists(path))
        {
            return;
        }
        const int attempts = 5;
        for (int i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (i < attempts - 1)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }
    }


    /// <summary>
    /// 下载到 CacheFolder/bg，返回文件名。已存在则直接返回。
    /// </summary>
    public async Task<string> DownloadToBgFolderAsync(FavorWallpaperRecord item, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string name = GetCacheFileName(item);
        string path = BackgroundService.GetBgFilePath(name);
        if (File.Exists(path))
        {
            progress?.Report(100);
            return name;
        }
        await DownloadToPathAsync(GetMediaUrl(item), path, progress, cancellationToken).ConfigureAwait(false);
        return name;
    }


    /// <summary>
    /// 下载到用户指定路径。若 bg 缓存已存在则复制。
    /// </summary>
    public async Task DownloadToFileAsync(FavorWallpaperRecord item, string destPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string cached = BackgroundService.GetBgFilePath(GetCacheFileName(item));
        if (File.Exists(cached))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(cached, destPath, overwrite: true);
            progress?.Report(100);
            return;
        }
        await DownloadToPathAsync(GetMediaUrl(item), destPath, progress, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// 下载并设为当前游戏的自定义背景（动态视频或满影画静态图）。
    /// </summary>
    public async Task SetAsCustomBackgroundAsync(GameBiz gameBiz, FavorWallpaperRecord item, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string name = await DownloadToBgFolderAsync(item, progress, cancellationToken).ConfigureAwait(false);
        AppConfig.SetCustomBg(gameBiz, name);
        AppConfig.SetEnableCustomBg(gameBiz, true);
        AppConfig.SetBg(gameBiz, name);
    }


    private async Task DownloadToPathAsync(string url, string destPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        string temp = destPath + ".tmp";
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (FileStream output = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    read += n;
                    if (total is > 0)
                    {
                        progress?.Report(read * 100.0 / total.Value);
                    }
                }
            }
            File.Move(temp, destPath, overwrite: true);
            progress?.Report(100);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch { }
            throw;
        }
    }


    private List<FavorWallpaperRecord> LoadCache(string cacheKey)
    {
        if (!DatabaseService.TryGetValue(cacheKey, out string? json, out _) || string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<FavorWallpaperRecord>>(json, AppConfig.JsonSerializerOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Deserialize wallpaper cache {Key} failed", cacheKey);
            return [];
        }
    }


    private void SaveCache(string cacheKey, List<FavorWallpaperRecord> items)
    {
        string json = JsonSerializer.Serialize(items, AppConfig.JsonSerializerOptions);
        DatabaseService.SetValue(cacheKey, json);
    }


    /// <summary>
    /// 好感视频或满影画图片的下载地址。
    /// </summary>
    public static string GetMediaUrl(FavorWallpaperRecord item) => item.IsStatic ? item.ImageUrl : item.VideoUrl;


    /// <summary>
    /// 预告角色（尚未实装）不进入满影画列表。
    /// </summary>
    private static bool IsPreviewAgent(BlackboardContentItem item)
    {
        return string.Equals(item.CornerMark, PreviewCornerMark, StringComparison.OrdinalIgnoreCase);
    }


    /// <summary>
    /// 代理人简称优先，便于和密友同行封面匹配。
    /// </summary>
    private static string AgentCharacterName(BlackboardContentItem item)
    {
        return string.IsNullOrWhiteSpace(item.AliasName) ? item.Title.Trim() : item.AliasName.Trim();
    }


    /// <summary>
    /// 满影画画廊只展示已解析到「影画展示3」的角色。
    /// </summary>
    private static List<FavorWallpaperRecord> VisibleMindscape(List<FavorWallpaperRecord> items)
    {
        List<FavorWallpaperRecord> visible = items.Where(x => !string.IsNullOrWhiteSpace(x.ImageUrl)).ToList();
        foreach (FavorWallpaperRecord item in visible)
        {
            item.IsStatic = true;
        }
        return visible;
    }


    /// <summary>
    /// 从标题去掉「好感壁纸 / 动态壁纸」后缀得到角色名。
    /// </summary>
    internal static string ExtractCharacterName(string title)
    {
        string name = WallpaperSuffixRegex().Replace(title, "");
        return name.Trim();
    }


    /// <summary>
    /// 有密友同行图则用它，否则回退好感壁纸图标（满影画可传入好感频道条目以对齐封面）。
    /// </summary>
    private static bool RematchCovers(
        List<FavorWallpaperRecord> items,
        IReadOnlyList<BlackboardContentItem> miyouItems,
        IReadOnlyList<BlackboardContentItem>? favorItems = null)
    {
        bool changed = false;
        foreach (FavorWallpaperRecord item in items)
        {
            if (favorItems is not null)
            {
                string? favorIcon = FindFavorWallpaperIcon(item.CharacterName, favorItems);
                if (!string.IsNullOrWhiteSpace(favorIcon) && !string.Equals(item.IconUrl, favorIcon, StringComparison.Ordinal))
                {
                    item.IconUrl = favorIcon;
                    changed = true;
                }
            }
            string? cover = FindMiyouCover(item.CharacterName, miyouItems) ?? item.IconUrl;
            if (!string.IsNullOrWhiteSpace(cover) && !string.Equals(item.CoverUrl, cover, StringComparison.Ordinal))
            {
                item.CoverUrl = cover;
                changed = true;
            }
        }
        return changed;
    }


    /// <summary>
    /// 从地图频道里挑出标题含「密友同行」的条目。
    /// </summary>
    private static List<BlackboardContentItem> EnumerateMiyouItems(BlackboardContentListData? map)
    {
        List<BlackboardContentItem> source = map?.FindChannel(MapChannelId)?.List ?? map?.List?.FirstOrDefault()?.List ?? [];
        return source.Where(x => x.Title.Contains("密友同行", StringComparison.Ordinal)).ToList();
    }


    /// <summary>
    /// 按角色名在密友同行列表里找封面；没有则返回 null，由调用方回退 IconUrl。
    /// </summary>
    internal static string? FindMiyouCover(string characterName, IReadOnlyList<BlackboardContentItem> miyouItems)
    {
        string norm = NormalizeName(characterName);
        if (norm.Length == 0)
        {
            return null;
        }

        string? bestIcon = null;
        int bestScore = 0;
        foreach (BlackboardContentItem item in miyouItems)
        {
            if (string.IsNullOrWhiteSpace(item.Icon))
            {
                continue;
            }
            string? extracted = ExtractMiyouCharacterName(item.Title);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                continue;
            }
            int score = ScoreCharacterName(norm, NormalizeName(extracted));
            if (score > bestScore)
            {
                bestScore = score;
                bestIcon = item.Icon;
            }
        }
        return bestIcon;
    }


    /// <summary>
    /// 按角色名在好感壁纸频道里找图标，使无密友同行的满影画封面与好感壁纸一致。
    /// </summary>
    internal static string? FindFavorWallpaperIcon(string characterName, IReadOnlyList<BlackboardContentItem> favorItems)
    {
        string norm = NormalizeName(characterName);
        if (norm.Length == 0 || favorItems.Count == 0)
        {
            return null;
        }

        string? bestIcon = null;
        int bestScore = 0;
        foreach (BlackboardContentItem item in favorItems)
        {
            if (string.IsNullOrWhiteSpace(item.Icon))
            {
                continue;
            }
            string extracted = ExtractCharacterName(item.Title);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                continue;
            }
            int score = ScoreCharacterName(norm, NormalizeName(extracted));
            if (score > bestScore)
            {
                bestScore = score;
                bestIcon = item.Icon;
            }
        }
        return bestIcon;
    }


    /// <summary>
    /// 从「艾莲密友同行」一类标题抽出角色名。
    /// </summary>
    private static string? ExtractMiyouCharacterName(string title)
    {
        Match match = MiyouCharacterNameRegex().Match(title);
        if (!match.Success)
        {
            return null;
        }
        string name = match.Groups[1].Value.Trim().Trim('「', '」', '『', '』', '"', '\'');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }


    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }
        return NamePunctuationRegex().Replace(value, "");
    }


    /// <summary>
    /// 名称分：全等；条目标题更长且以壁纸名为后缀（浅羽悠真 / 悠真）；
    /// 壁纸名以条目标题开头（奥菲丝&amp;鬼火 / 奥菲丝）。
    /// 不把「零号·安比」配到「安比」。
    /// </summary>
    private static int ScoreCharacterName(string wallpaperNorm, string miyouNorm)
    {
        if (wallpaperNorm.Length == 0 || miyouNorm.Length == 0)
        {
            return 0;
        }
        if (wallpaperNorm == miyouNorm)
        {
            return 1000 + wallpaperNorm.Length;
        }
        if (miyouNorm.Length > wallpaperNorm.Length && miyouNorm.EndsWith(wallpaperNorm, StringComparison.Ordinal))
        {
            return 100 + wallpaperNorm.Length;
        }
        if (wallpaperNorm.Length > miyouNorm.Length && wallpaperNorm.StartsWith(miyouNorm, StringComparison.Ordinal))
        {
            return 80 + miyouNorm.Length;
        }
        return 0;
    }


    [GeneratedRegex(@"好感壁纸|动态壁纸")]
    private static partial Regex WallpaperSuffixRegex();


    [GeneratedRegex(@"^[「『""']?(.+?)[」』""']?密友同行")]
    private static partial Regex MiyouCharacterNameRegex();


    [GeneratedRegex(@"[·・\s「」『』""''""''&＆]")]
    private static partial Regex NamePunctuationRegex();

}
