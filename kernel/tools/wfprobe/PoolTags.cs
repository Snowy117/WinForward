using System.Runtime.InteropServices;
using System.Text;

namespace WinForward.Probe;

internal readonly record struct PoolTagUsage(string Tag,
                                             uint PagedAllocs,
                                             uint PagedFrees,
                                             ulong PagedUsed,
                                             uint NonPagedAllocs,
                                             uint NonPagedFrees,
                                             ulong NonPagedUsed)
{
    public long NonPagedOutstanding => (long)NonPagedAllocs - (long)NonPagedFrees;

    // 系统池标签表只列出「出现过」的条目：条目在第一次分配时创建，本次启动内即使计数归零
    // 也仍然可读（实测：驱动卸载后 WfPr 仍显示 39/39/0）。因此 Find 返回 null 只说明「本次
    // 启动还没有任何分配」，等价于计数从 0 起算，不是读取失败。
    public static PoolTagUsage Zero(string tag) => new(tag, 0, 0, 0, 0, 0, 0);
}

// SystemPoolTagInformation 的条目布局（x64）：Tag[4]、PagedAllocs、PagedFrees、对齐、PagedUsed、
// NonPagedAllocs、NonPagedFrees、NonPagedUsed，共 40 字节，数组从缓冲区偏移 8 开始。
internal static unsafe class PoolTags
{
    private const int SystemPoolTagInformation = 22;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int EntrySize = 40;
    private const int HeaderSize = 8;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    public static PoolTagUsage? Find(string tag)
    {
        if (tag.Length != 4)
            throw new ArgumentException("pool tag 必须是 4 个字符", nameof(tag));

        byte[] wanted = Encoding.ASCII.GetBytes(tag);
        int size = 1 << 20;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemPoolTagInformation, buffer, size, out int needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + 4096);
                    continue;
                }

                if (status != 0)
                    throw new InvalidOperationException($"NtQuerySystemInformation 失败：0x{status:X8}");

                byte* basePtr = (byte*)buffer;
                int count = *(int*)basePtr;
                for (int index = 0; index < count; index++)
                {
                    byte* entry = basePtr + HeaderSize + index * EntrySize;
                    if (entry[0] != wanted[0] || entry[1] != wanted[1] || entry[2] != wanted[2] || entry[3] != wanted[3])
                        continue;

                    return new PoolTagUsage(tag,
                                            *(uint*)(entry + 4),
                                            *(uint*)(entry + 8),
                                            *(ulong*)(entry + 16),
                                            *(uint*)(entry + 24),
                                            *(uint*)(entry + 28),
                                            *(ulong*)(entry + 32));
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException("pool tag 查询缓冲区反复不足");
    }
}
