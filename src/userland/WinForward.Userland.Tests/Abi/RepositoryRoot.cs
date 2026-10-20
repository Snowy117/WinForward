namespace WinForward.Userland.Tests.Abi;

/// <summary>
/// 找到仓库根：从测试程序所在目录往上找 <c>src/driver/probe_ioctl.h</c>。
/// ABI 一致性测试要读 C++ 头与测试夹具的源码，环境变量 <c>WF_ABI_SOURCE_ROOT</c>
/// 可以指向一份临时副本（变异验证用：改副本里的头，确认测试会变红）。
/// </summary>
internal static class RepositoryRoot
{
    internal const string OverrideVariable = "WF_ABI_SOURCE_ROOT";

    internal static string Locate()
    {
        string? overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            string full = Path.GetFullPath(overridden);
            if (!File.Exists(Path.Combine(full, "src", "driver", "probe_ioctl.h")))
            {
                throw new InvalidOperationException($"{OverrideVariable}={full} 下找不到 src/driver/probe_ioctl.h");
            }

            return full;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "driver", "probe_ioctl.h")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"找不到仓库根（含 src/driver/probe_ioctl.h）；用 {OverrideVariable} 指定");
    }
}
