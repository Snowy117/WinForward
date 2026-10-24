namespace WinForward.Userland.Policy;

/// <summary>
/// 路径型进程选择器在内核里实际会看到的表示：设备路径 + 解析结果是不是目录。
/// </summary>
/// <remarks>
/// <see cref="DevicePath"/> 与 <c>ALE_APP_ID</c> 同形（<c>\device\harddiskvolumeN\...</c>），
/// 但还没有做 ASCII 小写折叠 —— 折叠由 <see cref="IProcessPathNormalizer"/> 的实现统一完成。
/// </remarks>
public sealed class ProcessPathResolution
{
    public ProcessPathResolution(string devicePath, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(devicePath);
        DevicePath = devicePath;
        IsDirectory = isDirectory;
    }

    public string DevicePath { get; }

    /// <summary>只影响 block 表达边界与顺序校验；matcher 对路径型选择器始终带前缀语义。</summary>
    public bool IsDirectory { get; }
}
