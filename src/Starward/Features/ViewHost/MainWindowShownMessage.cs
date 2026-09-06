namespace Starward.Features.ViewHost;

/// <summary>
/// 主窗口从隐藏状态（系统托盘）重新显示时发送。
/// <para>
/// 与 <see cref="MainWindowStateChangedMessage"/> 的 Activate 不同：后者在每次窗口激活时都会触发
/// （Alt+Tab、最小化恢复、点击窗口），本消息只在窗口真正从不可见变为可见时发送一次，
/// 用于「重新打开主界面」这一语义（例如随机模式换一张背景壁纸）。
/// </para>
/// </summary>
internal class MainWindowShownMessage
{

}
