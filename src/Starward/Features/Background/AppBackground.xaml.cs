using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starward.Core.HoYoPlay;
using Starward.Features.Codec;
using Starward.Codec.ICC;
using Starward.Features.ViewHost;
using Starward.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using WinRT;


namespace Starward.Features.Background;

[INotifyPropertyChanged]
public sealed partial class AppBackground : UserControl
{
    /// <summary>
    /// 当前 AppBackground 实例（全局单例访问点）。
    /// </summary>
    public static AppBackground Current { get; private set; }


    private readonly ILogger<AppBackground> _logger = AppConfig.GetLogger<AppBackground>();

    private readonly BackgroundService _backgroundService = AppConfig.GetService<BackgroundService>();

    private readonly FavorWallpaperService _favorWallpaperService = AppConfig.GetService<FavorWallpaperService>();


    public AppBackground()
    {
        Current = this;
        this.InitializeComponent();
        // 通过 Messenger 监听背景变更、主窗口状态变化、视频音量变化
        WeakReferenceMessenger.Default.Register<BackgroundChangedMessage>(this, OnBackgroundChanged);
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, OnMainWindowStateChanged);
        WeakReferenceMessenger.Default.Register<VideoBgVolumeChangedMessage>(this, OnVideoBgVolumeChanged);
        this.Loaded += AppBackground_Loaded;
        this.Unloaded += AppBackground_Unloaded;
    }


    private void AppBackground_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        this.XamlRoot.Changed -= XamlRoot_Changed;
        this.XamlRoot.Changed += XamlRoot_Changed;
    }


    private void AppBackground_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // 控件卸载时必须释放视频资源，否则 MediaPlayer 和 Win2D 资源会泄漏
        DisposeVideoResource();
        this.XamlRoot?.Changed -= XamlRoot_Changed;
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    /// <summary>
    /// XamlRoot 变化（例如 DPI 缩放改变、窗口移动到不同显示器）时重新加载背景，以匹配新分辨率。
    /// </summary>
    private void XamlRoot_Changed(Microsoft.UI.Xaml.XamlRoot sender, Microsoft.UI.Xaml.XamlRootChangedEventArgs args)
    {
        if (_lastScale != sender.RasterizationScale)
        {
            _ = UpdateBackgroundAsync();
        }
    }



    /// <summary>
    /// 当前关联的游戏 ID。设置时会触发背景初始化或更新。
    /// </summary>
    public GameId CurrentGameId
    {
        get; set
        {
            // 必须在 InitializeBackgroundImage 之前：它直接读 bg_ / custom_bg_ 贴出第一帧，
            // 随机结果晚一步就会先闪一下上次的壁纸。
            if (value is not null)
            {
                _favorWallpaperService.TryShuffleOnStartup(value.GameBiz);
            }
            if (field is null)
            {
                field = value;
                InitializeBackgroundImage();
            }
            field = value;
            _ = UpdateBackgroundAsync();
        }
    }


    public ImageSource? PlacehoderImageSource { get; set => SetProperty(ref field, value); }

    public ImageSource? BackgroundImageSource
    {
        get; set
        {
            if (value is null && field is not null)
            {
                PlacehoderImageSource = field;
            }
            SetProperty(ref field, value);
        }
    }

    public bool IsUpdateBackgroundRunning { get; set => SetProperty(ref field, value); }

    public GameBackground? CurrentGameBackground { get; private set; }

    /// <summary>上一次成功显示的背景文件路径，用于避免重复加载。</summary>
    private string? _lastBackgroundFile;

    private double _lastScale = 1;

    /// <summary>标记是否因为缺少 VP9 扩展而触发过失败提示。</summary>
    private bool _needToInstallVp9VideoExtension;


    /// <summary>
    /// 初始化背景图片（首次设置 CurrentGameId 时调用）。
    /// 仅处理非视频的缓存背景；视频背景延迟到 UpdateBackgroundAsync 处理。
    /// </summary>
    private void InitializeBackgroundImage()
    {
        try
        {
            var file = BackgroundService.GetCachedBackgroundFile(CurrentGameId);
            if (file != null)
            {
                if (!BackgroundService.FileIsSupportedVideo(file))
                {
                    BackgroundImageSource = new BitmapImage(new Uri(file));
                }
                try
                {
                    string? hex = AppConfig.AccentColor;
                    if (!string.IsNullOrWhiteSpace(hex))
                    {
                        Color color = ColorHelper.ToColor(hex);
                        AccentColorHelper.ChangeAppAccentColor(color);
                    }
                }
                catch { }
            }
            else
            {
                BackgroundImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Image/UI_CutScene_1130320101A.png"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize background image");
        }

    }



    /// <summary>用于取消正在进行的背景更新任务。</summary>
    private CancellationTokenSource? updateBackgroundCts;


    /// <summary>
    /// 更新当前游戏的背景（图片或视频）。
    /// 支持传入指定 background 用于外部直接指定；内部实现两轮尝试（快速超时 + 完整下载）。
    /// </summary>
    /// <param name="background">可选的指定背景。传入时优先使用，否则通过 BackgroundService 获取推荐背景。</param>
    public async Task UpdateBackgroundAsync(GameBackground? background = null)
    {
        string? imageFilePath = null;
        try
        {
            IsUpdateBackgroundRunning = true;

            updateBackgroundCts?.Cancel();
            updateBackgroundCts = new();
            CancellationToken cancellationToken = updateBackgroundCts.Token;

            if (CurrentGameId is null)
            {
                DisposeVideoResource();
                BackgroundImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Image/UI_CutScene_1130320101A.png"));
                CurrentGameBackground = null;
                WeakReferenceMessenger.Default.Send(new BackgroundDisplayedMessage(null));
                return;
            }

            // 两轮策略：第一轮对网络请求设置短超时（快速回退），第二轮允许完整等待
            for (int i = 0; i < 2; i++)
            {
                bool apiCancelled = false;
                string? filePath = null;
                GameBackground? gameBackground = null;
                try
                {
                    bool timeout = i == 0 && background is null;
                    CancellationToken apiCancellationToken = timeout ? new CancellationTokenSource(1000).Token : CancellationToken.None;
                    CancellationToken downloadCancellationToken = timeout ? new CancellationTokenSource(3000).Token : CancellationToken.None;
                    gameBackground = background ?? await _backgroundService.GetSuggestedGameBackgroundAsync(CurrentGameId, apiCancellationToken);
                    if (gameBackground is null)
                    {
                        filePath = BackgroundService.GetFallbackBackgroundImage(CurrentGameId);
                    }
                    else if (gameBackground.Type is GameBackground.BACKGROUND_TYPE_CUSTOM)
                    {
                        filePath = gameBackground.Background.Url;
                    }
                    else if (gameBackground.Type is GameBackground.BACKGROUND_TYPE_VIDEO && !gameBackground.StopVideo)
                    {
                        filePath = await _backgroundService.GetBackgroundFileAsync(gameBackground.Video.Url, downloadCancellationToken);
                    }
                    else
                    {
                        filePath = await _backgroundService.GetBackgroundFileAsync(gameBackground.Background.Url, downloadCancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    apiCancelled = true;
                    filePath = BackgroundService.GetFallbackBackgroundImage(CurrentGameId);
                }
                catch (Exception ex)
                {
                    filePath = BackgroundService.GetFallbackBackgroundImage(CurrentGameId);
                    _logger.LogError(ex, "Update background image");
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                if (filePath == _lastBackgroundFile)
                {
                    if (BackgroundService.FileIsSupportedVideo(filePath))
                    {
                        continue;
                    }
                    if (_lastScale == this.XamlRoot.GetUIScaleFactor())
                    {
                        continue;
                    }
                }
                DisposeVideoResource();
                BackgroundImageSource = null;
                if (filePath != null)
                {
                    if (gameBackground?.Type is GameBackground.BACKGROUND_TYPE_VIDEO)
                    {
                        await SetVideoBackgroundAsync(gameBackground, filePath, cancellationToken);
                    }
                    else if (BackgroundService.FileIsSupportedVideo(filePath))
                    {
                        await StartMediaPlayerAsync(filePath, cancellationToken);
                    }
                    else
                    {
                        imageFilePath = filePath;
                        await ChangeBackgroundImageAsync(filePath, cancellationToken);
                    }
                    _lastBackgroundFile = filePath;
                    _lastScale = this.XamlRoot.GetUIScaleFactor();
                    CurrentGameBackground = gameBackground;
                    if (!apiCancelled && gameBackground is not null)
                    {
                        // 记录最后使用的背景（包括自定义背景），用于下次启动、切换游戏或移动显示器时恢复。
                        AppConfig.SetBg(CurrentGameId.GameBiz, Path.GetFileName(filePath));
                        // 记录上次是否使用官方版本海报：背景列表更新后据此恢复海报（见 GetSuggestedGameBackgroundAsync）。
                        AppConfig.SetUseVersionPoster(CurrentGameId.GameBiz, gameBackground.Type is GameBackground.BACKGROUND_TYPE_POSTER);
                        // 官方视频：记忆播放/暂停，列表更新后仍按偏好恢复（见 GetSuggestedGameBackgroundAsync）。
                        if (gameBackground.Type is GameBackground.BACKGROUND_TYPE_VIDEO)
                        {
                            AppConfig.SetStopOfficialVideo(CurrentGameId.GameBiz, gameBackground.StopVideo);
                        }
                        if (gameBackground.Type is not GameBackground.BACKGROUND_TYPE_CUSTOM)
                        {
                            var list = await _backgroundService.GetGameBackgroundsAsync(CurrentGameId);
                            AppConfig.SetGameBackgroundIds(CurrentGameId.GameBiz, string.Join(',', list.Select(x => x.Id)));
                        }
                    }
                }
            }
            // 通知启动器页面当前实际显示的背景，使播放/暂停按钮状态与之保持一致。
            WeakReferenceMessenger.Default.Send(new BackgroundDisplayedMessage(CurrentGameBackground));
        }
        catch (OperationCanceledException) { }
        catch (COMException ex) when (ex.HResult == -2003292277)
        {
            // 0x88982F8B：WebP 解码失败（常见于未安装 WebP 图像扩展）
            if (Path.GetExtension(imageFilePath)?.Equals(".webp", StringComparison.OrdinalIgnoreCase) ?? false)
            {
                InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning,
                                                      Lang.AppBackground_ImageDecodingFailed,
                                                      Lang.AppBackground_PleaseInstallTheWebPImageExtension,
                                                      Lang.Common_Download,
                                                      async () => await Launcher.LaunchUriAsync(new("https://apps.microsoft.com/detail/9pg2dk419drg")));
            }
            else
            {
                InAppToast.MainWindow?.Warning(Lang.AppBackground_ImageDecodingFailed);
            }
            _logger.LogError(ex, "Cannot decode image: '{path}'", imageFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update background image");
        }
        finally
        {
            IsUpdateBackgroundRunning = false;
        }
    }


    /// <summary>
    /// 加载并显示静态背景图片，同时提取强调色。
    /// 根据窗口大小决定是否缩放解码，避免内存浪费。
    /// </summary>
    /// <param name="file">图片文件完整路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ChangeBackgroundImageAsync(string file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var fs = File.OpenRead(file);
        var decoder = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());

        double scale = this.XamlRoot.GetUIScaleFactor();
        int decodeWidth = 0, decodeHeight = 0;
        double windowWidth = ActualWidth * scale, windowHeight = ActualHeight * scale;

        if (decoder.PixelWidth <= windowWidth || decoder.PixelHeight <= windowHeight)
        {
            // 原图小于等于窗口尺寸，直接加载
            decodeWidth = (int)decoder.PixelWidth;
            decodeHeight = (int)decoder.PixelHeight;
            var writeableBitmap = new WriteableBitmap(decodeWidth, decodeHeight);
            fs.Position = 0;
            await writeableBitmap.SetSourceAsync(fs.AsRandomAccessStream());
            cancellationToken.ThrowIfCancellationRequested();
            Color? color = AccentColorHelper.GetAccentColor(writeableBitmap.PixelBuffer, decodeWidth, decodeHeight);
            AccentColorHelper.ChangeAppAccentColor(color);
            AppConfig.AccentColor = color?.ToHex() ?? null;
            BackgroundImageSource = writeableBitmap;
        }
        else
        {
            // 按窗口比例缩放解码（使用 Fant 插值）
            if (windowWidth * decoder.PixelHeight > windowHeight * decoder.PixelWidth)
            {
                decodeWidth = (int)windowWidth;
                decodeHeight = (int)(windowWidth * decoder.PixelHeight / decoder.PixelWidth);
            }
            else
            {
                decodeHeight = (int)windowHeight;
                decodeWidth = (int)(windowHeight * decoder.PixelWidth / decoder.PixelHeight);
            }
            using var soft = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                                                                  BitmapAlphaMode.Premultiplied,
                                                                  new BitmapTransform
                                                                  {
                                                                      ScaledWidth = (uint)decodeWidth,
                                                                      ScaledHeight = (uint)decodeHeight,
                                                                      InterpolationMode = BitmapInterpolationMode.Fant
                                                                  },
                                                                  ExifOrientationMode.IgnoreExifOrientation,
                                                                  ColorManagementMode.DoNotColorManage);
            var softwareBitmapSource = new SoftwareBitmapSource();
            await softwareBitmapSource.SetBitmapAsync(soft);

            cancellationToken.ThrowIfCancellationRequested();

            // 从 SoftwareBitmap 直接读取原始像素用于提取强调色（避免二次拷贝）
            using BitmapBuffer bitmapBuffer = soft.LockBuffer(BitmapBufferAccessMode.Read);
            using IMemoryBufferReference memoryBufferReference = bitmapBuffer.CreateReference();
            memoryBufferReference.As<AccentColorHelper.IMemoryBufferByteAccess>().GetBuffer(out nint bufferPtr, out uint capacity);
            Color? color = AccentColorHelper.GetAccentColor(bufferPtr, capacity, decodeWidth, decodeHeight);
            AccentColorHelper.ChangeAppAccentColor(color);
            AppConfig.AccentColor = color?.ToHex() ?? null;
            BackgroundImageSource = softwareBitmapSource;
        }
    }



    #region Video

    /// <summary>不超过此大小的背景视频会读入内存循环播放，避免每次循环重新读盘。</summary>
    private const long InMemoryVideoBackgroundMaxBytes = 32L * 1024 * 1024;

    /// <summary>当前正在播放视频背景的 MediaPlayer 实例（帧服务器模式）。</summary>
    private MediaPlayer? _mediaPlayer;

    /// <summary>当前视频源；内存播放时需与流一起保持存活。</summary>
    private MediaSource? _mediaSource;

    /// <summary>内存播放时持有的视频数据；MediaPlayer 释放前不可关掉。</summary>
    private InMemoryRandomAccessStream? _mediaStream;

    /// <summary>用于取消尚未完成的内存读入 / 启动，避免切换背景后旧任务再创建播放器。</summary>
    private CancellationTokenSource? _startMediaPlayerCts;

    /// <summary>用于接收视频帧的 Win2D 渲染目标。</summary>
    private CanvasRenderTarget? _videoSurface;

    /// <summary>视频背景上层叠加的主题图片（CanvasBitmap）。</summary>
    private CanvasBitmap? _videoOverlayImage;

    /// <summary>最终作为背景显示的 CanvasImageSource（每帧更新）。</summary>
    private CanvasImageSource? _videoImageSource;

    /// <summary>用于限制同时处理视频帧的信号量，避免 Win2D 绘制冲突。</summary>
    private SemaphoreSlim _videoSemaphore = new SemaphoreSlim(1, 1);


    /// <summary>
    /// 计算背景视频实际使用的音量（0-100）。
    /// 规则：仅当启用“自定义背景”时才使用按游戏保存的音量值，否则强制为 0（静音）。
    /// </summary>
    private int GetEffectiveVideoVolume()
    {
        if (CurrentGameId is null)
        {
            return 0;
        }
        var biz = CurrentGameId.GameBiz;
        return AppConfig.GetEnableCustomBg(biz) ? AppConfig.GetVideoBgVolume(biz) : 0;
    }


    /// <summary>
    /// 为指定的视频文件启动 MediaPlayer（使用帧服务器模式）。
    /// 文件不超过 <see cref="InMemoryVideoBackgroundMaxBytes"/> 时读入内存循环播放；更大或失败则从文件流式播放。
    /// .webm 文件会根据检测结果注册 VP9/Vorbis 本地解码器。
    /// </summary>
    /// <param name="file">视频文件完整路径（支持 mp4/mkv/webm）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task StartMediaPlayerAsync(string file, CancellationToken cancellationToken = default)
    {
        if (Path.GetExtension(file).Equals(".webm", StringComparison.OrdinalIgnoreCase))
        {
            bool decoderInstalled = VP9Helper.IsVP9DecoderInstalled();
            bool vp8 = VP9Helper.IsVP8VideoFile(file);
            if (vp8)
            {
                if (!decoderInstalled)
                {
                    _needToInstallVp9VideoExtension = true;
                }
            }
            else
            {
                bool highProfileOrRgb = VP9Helper.IsVP9HighProfileOrRGB(file);
                // 高 Profile 或 RGB 格式官方扩展不支持，必须使用我们提供的 libvpx 软件解码器
                if (!decoderInstalled || highProfileOrRgb)
                {
                    VP9Helper.RegisterVP9Decoder(true);
                }
                if (!decoderInstalled && !highProfileOrRgb)
                {
                    SuggestToInstallVP9Decoder();
                }
            }
        }
        // 无论是否 webm，只要是视频背景都注册 Vorbis（部分 mkv/webm 可能包含 Vorbis 音频）
        VP9Helper.RegisterVorbisDecoder();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousCts = _startMediaPlayerCts;
        _startMediaPlayerCts = cts;
        previousCts?.Cancel();

        MediaSource? source = null;
        InMemoryRandomAccessStream? memoryStream = null;
        try
        {
            (source, memoryStream) = await CreateVideoMediaSourceAsync(file, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_startMediaPlayerCts, cts))
            {
                source.Dispose();
                memoryStream?.Dispose();
                return;
            }

            DisposeMediaPlayback();
            _mediaSource = source;
            _mediaStream = memoryStream;
            source = null;
            memoryStream = null;

            _mediaPlayer = new MediaPlayer
            {
                IsLoopingEnabled = true,
                Volume = GetEffectiveVideoVolume() / 100.0,
                IsMuted = false,
                // 关键：启用帧服务器模式，后续通过 VideoFrameAvailable + CopyFrameToVideoSurface 手动获取帧
                IsVideoFrameServerEnabled = true,
                Source = _mediaSource
            };
            _mediaPlayer.CommandManager.IsEnabled = false;
            _mediaPlayer.SystemMediaTransportControls.IsEnabled = false;
            _mediaPlayer.VideoFrameAvailable += MediaPlayer_VideoFrameAvailable;
            _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
            _mediaPlayer.Play();
        }
        catch (OperationCanceledException)
        {
            source?.Dispose();
            memoryStream?.Dispose();
        }
        catch (Exception ex)
        {
            source?.Dispose();
            memoryStream?.Dispose();
            _logger.LogError(ex, "Start media player");
        }
        finally
        {
            if (ReferenceEquals(_startMediaPlayerCts, cts))
            {
                _startMediaPlayerCts = null;
            }
            cts.Dispose();
        }
    }


    /// <summary>
    /// 创建视频背景的 MediaSource。
    /// 小文件拷入 <see cref="InMemoryRandomAccessStream"/> 供循环播放；否则或失败时回退到文件 URI。
    /// </summary>
    /// <param name="file">视频文件完整路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>媒体源，以及内存播放时需要一直拿着的流（文件 URI 时为 null）。</returns>
    private async Task<(MediaSource Source, InMemoryRandomAccessStream? Stream)> CreateVideoMediaSourceAsync(string file, CancellationToken cancellationToken)
    {
        string? contentType = GetVideoContentType(file);
        long length = 0;
        try
        {
            length = new FileInfo(file).Length;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Get video background file size failed, fallback to file");
        }

        if (contentType is not null && length > 0 && length <= InMemoryVideoBackgroundMaxBytes)
        {
            InMemoryRandomAccessStream? memory = null;
            try
            {
                memory = new InMemoryRandomAccessStream();
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await RandomAccessStream.CopyAsync(fs.AsRandomAccessStream(), memory).AsTask(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                memory.Seek(0);
                MediaSource source = MediaSource.CreateFromStream(memory, contentType);
                InMemoryRandomAccessStream stream = memory;
                memory = null;
                return (source, stream);
            }
            catch (OperationCanceledException)
            {
                memory?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                memory?.Dispose();
                _logger.LogWarning(ex, "Load video background into memory failed, fallback to file");
            }
        }

        return (MediaSource.CreateFromUri(new Uri(file)), null);
    }


    /// <summary>
    /// 按扩展名返回 Media Foundation 识别的视频 MIME。无法识别时返回 null。
    /// </summary>
    /// <param name="file">视频文件路径或文件名。</param>
    private static string? GetVideoContentType(string file)
    {
        string ext = Path.GetExtension(file);
        if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return "video/mp4";
        }
        if (ext.Equals(".webm", StringComparison.OrdinalIgnoreCase))
        {
            return "video/webm";
        }
        if (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            return "video/x-matroska";
        }
        return null;
    }


    /// <summary>
    /// 设置官方视频类型背景（GameBackground.BACKGROUND_TYPE_VIDEO）。
    /// 启动播放器后异步加载主题叠加层（overlay）和强调色来源图片。
    /// </summary>
    /// <param name="gameBackground">包含视频和主题 overlay 信息的背景对象。</param>
    /// <param name="filePath">已下载的视频文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task SetVideoBackgroundAsync(GameBackground gameBackground, string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BackgroundService.FileIsSupportedVideo(filePath))
        {
            await StartMediaPlayerAsync(filePath, cancellationToken);
            // overlay 和强调色可以异步加载，不阻塞主流程
            _ = PrepareVideoOverlayImageAsync(gameBackground.Theme.Url, cancellationToken);
            _ = ChangeAccentColorToImageFileAsync(gameBackground.Background.Url, cancellationToken);
        }
        else
        {
            // 极少数情况官方返回的“视频背景”实际是图片，降级处理
            string overlayPath = await _backgroundService.GetBackgroundFileAsync(gameBackground.Theme.Url, cancellationToken);
            using var fs1 = File.OpenRead(filePath);
            using var bitmap = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), fs1.AsRandomAccessStream(), 96);
            using var fs2 = File.OpenRead(overlayPath);
            using var overlay = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), fs2.AsRandomAccessStream(), 96);
            var imageSource = new CanvasImageSource(CanvasDevice.GetSharedDevice(), bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height, 96);
            using (var ds = imageSource.CreateDrawingSession(Microsoft.UI.Colors.Transparent))
            {
                ds.DrawImage(bitmap);
                Rect source = new Rect(0, 0, overlay.SizeInPixels.Width, overlay.SizeInPixels.Height);
                Rect dest = new Rect(0, 0, imageSource.SizeInPixels.Width, imageSource.SizeInPixels.Height);
                ds.DrawImage(overlay, dest, source, 1, CanvasImageInterpolation.HighQualityCubic);
            }
            BackgroundImageSource = imageSource;
            if (bitmap.Format is Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized)
            {
                try
                {
                    Color? color = await Task.Run(() =>
                    {
                        Color? color = AccentColorHelper.GetAccentColor(bitmap.GetPixelBytes(), (int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
                        return color;
                    });
                    if (color is not null)
                    {
                        AccentColorHelper.ChangeAppAccentColor(color);
                        AppConfig.AccentColor = color?.ToHex() ?? null;
                    }
                }
                catch { }
            }

        }
    }


    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _logger.LogError(args.ExtendedErrorCode, "Media player failed.");
        if (_needToInstallVp9VideoExtension)
        {
            InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning, null, Lang.AppBackground_VideoDecodingFailedPleaseInstallTheVP9VideoExtensions, Lang.Common_Download, async () => await Launcher.LaunchUriAsync(new("https://apps.microsoft.com/detail/9n4d0msmp0pt")));
            _needToInstallVp9VideoExtension = false;
        }
        else
        {
            InAppToast.MainWindow?.Warning(Lang.AppBackground_VideoDecodingFailed);
        }
    }


    /// <summary>
    /// 视频帧可用回调（帧服务器模式）。
    /// 把解码后的帧拷贝到 Win2D 表面，再合成 overlay 后作为背景源。
    /// 注意：必须通过 DispatcherQueue 切回 UI 线程操作 Win2D 对象。
    /// </summary>
    private void MediaPlayer_VideoFrameAvailable(MediaPlayer sender, object args)
    {
        // 避免同一时刻多个帧重叠处理
        if (_videoSemaphore.CurrentCount == 0)
        {
            return;
        }
        _videoSemaphore.Wait();
        DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                if (_videoSurface is null || _videoImageSource is null)
                {
                    _videoSurface?.Dispose();
                    int width = (int)sender.PlaybackSession.NaturalVideoWidth;
                    int height = (int)sender.PlaybackSession.NaturalVideoHeight;
                    _videoSurface = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
                    _videoImageSource = new CanvasImageSource(CanvasDevice.GetSharedDevice(), width, height, 96);
                    BackgroundImageSource = _videoImageSource;
                }
                // 将 MF 解码帧拷贝到 Direct2D 表面
                sender.CopyFrameToVideoSurface(_videoSurface);
                using var ds = _videoImageSource.CreateDrawingSession(Microsoft.UI.Colors.Transparent);
                ds.DrawImage(_videoSurface);
                if (_videoOverlayImage is not null)
                {
                    Rect source = new Rect(0, 0, _videoOverlayImage.SizeInPixels.Width, _videoOverlayImage.SizeInPixels.Height);
                    Rect dest = new Rect(0, 0, _videoImageSource.SizeInPixels.Width, _videoImageSource.SizeInPixels.Height);
                    ds.DrawImage(_videoOverlayImage, dest, source, 1, CanvasImageInterpolation.HighQualityCubic);
                }
            }
            catch { }
            finally
            {
                _videoSemaphore.Release();
            }
        });
    }


    /// <summary>
    /// 异步准备视频背景的主题叠加图片（通常是半透明主题图层）。
    /// </summary>
    /// <param name="url">叠加图片的网络地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task PrepareVideoOverlayImageAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            string filePath = await _backgroundService.GetBackgroundFileAsync(url, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            _videoOverlayImage?.Dispose();
            using var fs = File.OpenRead(filePath);
            _videoOverlayImage = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), fs.AsRandomAccessStream(), 96);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prepare video overlay image");
        }
    }


    /// <summary>
    /// 从视频背景对应的强调色来源图片中提取并应用强调色（异步后台线程读取像素）。
    /// </summary>
    private async Task ChangeAccentColorToImageFileAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            Color? color = await Task.Run(async () =>
            {
                string filePath = await _backgroundService.GetBackgroundFileAsync(url, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
                using var fs = File.OpenRead(filePath);
                var decoder = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());
                var pixelData = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                Color? color = AccentColorHelper.GetAccentColor(pixelData.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
                return color;
            });
            if (color is not null)
            {
                AccentColorHelper.ChangeAppAccentColor(color);
                AppConfig.AccentColor = color?.ToHex() ?? null;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prepare video overlay image");
        }
    }


    /// <summary>
    /// 释放所有视频相关资源（MediaPlayer、内存视频流、Win2D 表面、叠加图、已注册的解码器 MFT）。
    /// 必须在切换背景、窗口隐藏过久、控件卸载时调用。
    /// </summary>
    private void DisposeVideoResource()
    {
        _startMediaPlayerCts?.Cancel();
        DisposeMediaPlayback();
        _videoSurface?.Dispose();
        _videoSurface = null;
        _videoImageSource = null;
        _videoOverlayImage?.Dispose();
        _videoOverlayImage = null;
        // 主动注销我们注册的本地解码器
        VP9Helper.UnregisterVP9Decoder(true);
        VP9Helper.UnregisterVorbisDecoder();
    }


    /// <summary>
    /// 释放播放器及其媒体源、内存流，不碰 Win2D 表面与解码器。
    /// </summary>
    private void DisposeMediaPlayback()
    {
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        _mediaSource?.Dispose();
        _mediaSource = null;
        _mediaStream?.Dispose();
        _mediaStream = null;
    }

    /// <summary>
    /// 捕获当前实际渲染的背景快照（用于抽卡分享图等）。
    /// 静态图片直接返回原始文件路径。
    /// 视频背景：
    ///   - 官方背景视频 (CurrentGameBackground.Type == BACKGROUND_TYPE_VIDEO)：仅抓取纯视频帧，**移除 theme overlay 叠加图片**。
    ///   - 自定义视频：正常抓取（通常无 overlay）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可被 ImageLoader / CanvasBitmap 加载的本地图片路径；失败或无背景时返回 null。</returns>
    public async Task<string?> CaptureCurrentBackgroundSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_lastBackgroundFile) || !File.Exists(_lastBackgroundFile))
            {
                return null;
            }

            if (!BackgroundService.FileIsSupportedVideo(_lastBackgroundFile))
            {
                // 静态图片直接复用原始文件（与渲染器中“按实际样子”一致）
                return _lastBackgroundFile;
            }

            // 视频：需要抓取当前合成帧，必须在 UI 线程操作 Win2D 资源
            if (DispatcherQueue is null || _videoSurface is null)
            {
                return null;
            }

            string folder = Path.Combine(AppConfig.CacheFolder, "cache", "share");
            Directory.CreateDirectory(folder);
            string tempPath = Path.Combine(folder, $"bg_snapshot_{DateTime.Now:yyyyMMddHHmmssfff}.png");

            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            // 匹配现有帧处理风格：Win2D 绘制必须在 Dispatcher (UI) 线程执行。
            // 仅将文件保存（IO）放到后台线程。
            bool dispatched = DispatcherQueue.TryEnqueue(() =>
            {
                CanvasRenderTarget? snapshot = null;
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetResult(null);
                        return;
                    }

                    snapshot = new CanvasRenderTarget(
                        CanvasDevice.GetSharedDevice(),
                        _videoSurface.SizeInPixels.Width,
                        _videoSurface.SizeInPixels.Height,
                        96);

                    using (var ds = snapshot.CreateDrawingSession())
                    {
                        ds.Clear(Microsoft.UI.Colors.Transparent);
                        ds.DrawImage(_videoSurface);

                        // 仅当不是官方背景视频时才叠加 overlay
                        // 官方视频 (Type == BACKGROUND_TYPE_VIDEO) 需要移除主题叠加图片
                        bool isOfficialVideoBg = CurrentGameBackground?.Type == GameBackground.BACKGROUND_TYPE_VIDEO;
                        if (_videoOverlayImage is not null && !isOfficialVideoBg)
                        {
                            Rect src = new Rect(0, 0, _videoOverlayImage.SizeInPixels.Width, _videoOverlayImage.SizeInPixels.Height);
                            Rect dst = new Rect(0, 0, snapshot.SizeInPixels.Width, snapshot.SizeInPixels.Height);
                            ds.DrawImage(_videoOverlayImage, dst, src, 1f, CanvasImageInterpolation.HighQualityCubic);
                        }
                    }

                    // 将保存放到后台，snapshot 所有权转移到任务
                    var snapForSave = snapshot;
                    snapshot = null; // 防止外层 finally 误释放
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await using var fs = File.Create(tempPath);
                            await ImageSaver.SaveAsPngAsync(snapForSave, fs, ColorPrimaries.BT709);
                            tcs.TrySetResult(tempPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Save captured background snapshot");
                            tcs.TrySetResult(null);
                        }
                        finally
                        {
                            snapForSave?.Dispose();
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Capture video background snapshot frame");
                    snapshot?.Dispose();
                    tcs.TrySetResult(null);
                }
            });

            if (!dispatched)
            {
                tcs.TrySetResult(null);
            }

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CaptureCurrentBackgroundSnapshotAsync");
            return null;
        }
    }






    #endregion



    private void OnBackgroundChanged(object _, BackgroundChangedMessage message)
    {
        _ = UpdateBackgroundAsync(message.GameBackground);
    }


    private void OnMainWindowStateChanged(object _, MainWindowStateChangedMessage message)
    {
        try
        {
            if (message.Hide || message.SessionLock)
            {
                // 窗口隐藏、最小化或锁屏时仅暂停视频播放（不释放解码器与渲染资源），
                // 停止解码以降低占用；恢复显示时直接续播，避免重建资源导致背景闪烁。
                _mediaPlayer?.Pause();
            }
            else if (message.Activate)
            {
                // 关键：正在切换背景（UpdateBackgroundAsync 运行中）时不得在此兜底重启视频。
                // 从视频切换到图片时，DisposeVideoResource 已把 _mediaPlayer 置空，而 _lastBackgroundFile
                // 要等 await ChangeBackgroundImageAsync 完成后才更新为新图片；此空窗期内 _lastBackgroundFile 仍指向
                // 旧视频。若此时收到窗口激活消息（如从「图库」窗口把图片拖到首页，落点激活主窗口），
                // 兜底分支会用这个过期路径把刚释放的视频重新拉起，其帧回调再覆盖掉刚设好的图片，表现为「更换失败」。
                if (!IsUpdateBackgroundRunning && _mediaPlayer is null && _startMediaPlayerCts is null && BackgroundService.FileIsSupportedVideo(_lastBackgroundFile))
                {
                    // 兜底：若播放器曾在其他路径被释放，则重新开始解码渲染背景视频
                    _ = StartMediaPlayerAsync(_lastBackgroundFile!);
                    if (CurrentGameBackground?.Type is GameBackground.BACKGROUND_TYPE_VIDEO && CurrentGameBackground.Theme?.Url is string url && !string.IsNullOrEmpty(url))
                    {
                        _ = PrepareVideoOverlayImageAsync(url);
                    }
                }
                else if (_mediaPlayer is not null)
                {
                    var state = _mediaPlayer.PlaybackSession.PlaybackState;
                    if (state is not MediaPlaybackState.Playing)
                    {
                        _mediaPlayer.Play();
                    }
                }
            }
        }
        catch { }
    }


    /// <summary>
    /// 响应用户修改视频背景音量设置。
    /// </summary>
    private void OnVideoBgVolumeChanged(object _, VideoBgVolumeChangedMessage message)
    {
        try
        {
            if (_mediaPlayer is not null)
            {
                _mediaPlayer.Volume = message.Volume / 100d;
            }
        }
        catch { }
    }



    private bool _vp9DecoderSuggested;


    /// <summary>
    /// 向用户建议安装微软官方 VP9 扩展（仅提示一次）。
    /// 该扩展可提供更优的解码性能（降低 CPU 占用）。
    /// </summary>
    private void SuggestToInstallVP9Decoder()
    {
        if (!_vp9DecoderSuggested)
        {
            InAppToast.MainWindow?.ShowWithButton(InfoBarSeverity.Warning, null, Lang.ItIsRecommendedToInstallTheVP9VideoExtensionsToReduceCPUUsage, Lang.Common_Download, async () => await Launcher.LaunchUriAsync(new("https://apps.microsoft.com/detail/9n4d0msmp0pt")));
            _vp9DecoderSuggested = true;
        }
    }


}
