namespace WinForward.Userland.Relay;

/// <summary>SOCKS5 建连或握手失败。消息里带上游、目标与（有的话）<see cref="Rep"/> 码。</summary>
/// <remarks>
/// 具名类型是给会话级 catch 用的：<see cref="RelayServer"/> 要把它记成一条 <c>relay error</c>，
/// 而不是让它落进某个「连接失败就算了」的兜底分支。
/// </remarks>
public sealed class Socks5Exception : Exception
{
    public Socks5Exception(string message, byte? rep = null, Exception? innerException = null)
        : base(message, innerException) => Rep = rep;

    /// <summary>SOCKS5 CONNECT 回复码；连上游或握手之前就失败时为 null。</summary>
    public byte? Rep { get; }
}
