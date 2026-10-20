#pragma once

// 探针驱动与测试程序之间的控制面 ABI。C# 侧在 tools/wfprobe/Protocol.cs 里逐字节镜像，
// 两侧都有布局断言。任何字段调整都必须同时提升 probe_abi_version。
//
// 与 src/common/redirect_context.h 的分工：那个头定义的是“用途数据”（随连接走、
// 用户态用 SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT 读回）；本头定义的是“控制面”
// （IOCTL 下发的配置、回灌的计数与事件）。两者都不含指针。

#include <cstddef>
#include <cstdint>

namespace winforward::wfp::probe
{

inline constexpr std::uint32_t probe_abi_version = 1;

// CTL_CODE(FILE_DEVICE_UNKNOWN, function, METHOD_BUFFERED, FILE_ANY_ACCESS)。
[[nodiscard]] constexpr std::uint32_t make_ioctl(std::uint32_t function) noexcept
{
    constexpr std::uint32_t file_device_unknown = 0x22;
    constexpr std::uint32_t method_buffered = 0;
    constexpr std::uint32_t file_any_access = 0;
    return (file_device_unknown << 16) | (file_any_access << 14) | (function << 2) | method_buffered;
}

inline constexpr std::uint32_t ioctl_set_config = make_ioctl(0x800);
inline constexpr std::uint32_t ioctl_get_config = make_ioctl(0x801);
inline constexpr std::uint32_t ioctl_get_stats = make_ioctl(0x802);
inline constexpr std::uint32_t ioctl_reset_stats = make_ioctl(0x803);
inline constexpr std::uint32_t ioctl_get_events = make_ioctl(0x804);

// documented_branch 复刻官方文档的分支：连接的原 local 地址是 ANY 时指向 loopback，
// 否则指向该 local 地址。force_* 用于把两条分支单独钉死做对照。
enum class redirect_mode : std::uint32_t
{
    documented_branch = 0,
    force_loopback = 1,
    force_local = 2,
};

enum class probe_decision : std::uint8_t
{
    permitted_disabled = 0,
    permitted_wrong_process = 1,
    permitted_listener_port = 2,
    permitted_redirect_state = 3,
    permitted_already_redirected = 4,
    permitted_no_write_right = 5,
    permitted_non_tcp = 6,
    redirected = 7,
    redirect_failed = 8,
    context_alloc_failed = 9,
};

enum class redirect_branch : std::uint8_t
{
    none = 0,
    any_to_loopback = 1,
    local_address = 2,
};

struct probe_config
{
    std::uint32_t abi_version;
    std::uint32_t enabled;
    redirect_mode mode;
    std::uint32_t target_process_id; // 0 表示不按 PID 过滤
    std::uint32_t listener_process_id; // 写进 localRedirectTargetPID
    std::uint32_t local_address_override; // in_addr.S_un.S_addr 的原始值，仅 force_local 使用
    std::uint16_t listener_port;
    std::uint16_t reserved;
};

static_assert(sizeof(probe_config) == 28, "probe_config 是对外 ABI");
static_assert(offsetof(probe_config, enabled) == 4);
static_assert(offsetof(probe_config, mode) == 8);
static_assert(offsetof(probe_config, listener_port) == 24);

struct probe_stats
{
    std::uint64_t classify_calls;
    std::uint64_t redirected;
    std::uint64_t contexts_allocated;
    std::uint64_t context_alloc_failures;
    std::uint64_t apply_failures;
    std::uint64_t permitted_disabled;
    std::uint64_t permitted_wrong_process;
    std::uint64_t permitted_listener_port;
    std::uint64_t permitted_redirect_state;
    std::uint64_t permitted_already_redirected;
    std::uint64_t permitted_no_write_right;
    std::uint64_t permitted_non_tcp;
    std::uint32_t last_status;
    std::uint32_t abi_version;
};

static_assert(sizeof(probe_stats) == 104, "probe_stats 是对外 ABI");

// 环形事件日志。时间戳是 KeQueryInterruptTime() 的 100ns 计数，仅用于排序与相对比较；
// 地址按网络字节序原样保存，端口为主机字节序。
struct probe_event
{
    std::uint64_t timestamp_100ns;
    std::uint32_t sequence;
    std::uint32_t process_id;
    std::uint8_t original_address[4];
    std::uint8_t local_address[4];
    std::uint16_t original_port;
    std::uint16_t local_port;
    std::uint16_t redirect_port;
    std::uint8_t decision;
    std::uint8_t mode;
    std::uint8_t branch;
    std::uint8_t address_family;
};

static_assert(sizeof(probe_event) == 40, "probe_event 是对外 ABI");
static_assert(offsetof(probe_event, original_address) == 16);
static_assert(offsetof(probe_event, original_port) == 24);

} // namespace winforward::wfp::probe
