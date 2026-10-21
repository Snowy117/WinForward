#pragma once

// 驱动与测试程序之间的控制面 ABI（v2）。C# 侧在 tools/wfprobe/Protocol.cs 里逐字节镜像，
// 两侧都有布局断言。任何字段调整都必须同时提升 probe_abi_version。
//
// 与 src/common/redirect_context.h 的分工：那个头定义的是“用途数据”（随连接走、用户态用
// SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT 读回）；本头定义的是“控制面”（IOCTL 下发的
// 配置与规则 blob、回灌的计数与事件）。两者都不含指针。

#include <cstddef>
#include <cstdint>

namespace winforward::wfp::probe
{

inline constexpr std::uint32_t probe_abi_version = 2;

// 单块规则 blob 的上限。METHOD_BUFFERED 会替我们复制一份输入，这个上限只用于拒绝明显
// 异常的下发（正常策略是几百字节到几 KB）。
inline constexpr std::uint32_t rule_blob_max_size = 64 * 1024;

// 防循环闸 4 的 self-PID 集合容量。真实部署里代理进程数远小于这个值。
inline constexpr std::uint32_t max_self_process_ids = 8;

// CTL_CODE(FILE_DEVICE_UNKNOWN, function, METHOD_BUFFERED, FILE_ANY_ACCESS)。
// FILE_ANY_ACCESS 不是放宽：设备 DACL（IoCreateDeviceSecure + SDDL_DEVOBJ_SYS_ALL_ADM_ALL）
// 与每个 IOCTL 入口的 caller_is_admin 才是访问控制面。
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
inline constexpr std::uint32_t ioctl_set_rule_blob = make_ioctl(0x805);
inline constexpr std::uint32_t ioctl_get_rule_info = make_ioctl(0x806);
// 诊断（发布前可移除）：把一段 UTF-16LE 当成 ALE_APP_ID，返回内核转换后的 UTF-8 字节。
// 存在的唯一理由：>MAX_PATH 的进程无法用 CreateProcess/FwpmGetAppIdFromFileName0 构造，
// 不给这个入口，collect_process_path 的 511 字节边界就不可验证。
inline constexpr std::uint32_t ioctl_measure_process_path = make_ioctl(0x807);
inline constexpr std::uint32_t process_path_measure_input_max = 2048;

// 一次 classify 的结论。数值是对外 ABI，C# 侧按名字镜像；新增只追加。
enum class probe_decision : std::uint8_t
{
    permitted_disabled = 0,
    permitted_no_write_right = 1,
    permitted_non_tcp = 2,
    permitted_listener_port = 3,
    permitted_condition_flags = 4,
    permitted_redirect_state = 5,
    permitted_already_redirected = 6,
    permitted_self_process = 7,
    permitted_no_rules = 8,
    permitted_unsupported = 9,
    permitted_fallback = 10,
    permitted_rule = 11,
    blocked_rule = 12,
    blocked_fallback = 13,
    redirected = 14,
    redirect_failed = 15,
    context_alloc_failed = 16,
    permitted_deferred_block = 17,
};

// 最终动作，与 src/common/rule_blob.h 的 rule_action 数值一致（编译期断言在 classify 里）。
enum class probe_action : std::uint8_t
{
    permit = 0,
    block = 1,
    redirect = 2,
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
    std::uint32_t listener_process_id; // 写进 localRedirectTargetPID
    std::uint16_t listener_port; // 闸 1 的端口 + 重定向目标端口
    std::uint16_t reserved0;
    std::uint32_t self_process_count; // 防循环闸 4：这些 PID 的出站连接一律放行
    std::uint32_t self_process_ids[max_self_process_ids];
    std::uint32_t reserved[3];
};

static_assert(sizeof(probe_config) == 64, "probe_config 是对外 ABI");
static_assert(offsetof(probe_config, listener_process_id) == 8);
static_assert(offsetof(probe_config, listener_port) == 12);
static_assert(offsetof(probe_config, self_process_count) == 16);
static_assert(offsetof(probe_config, self_process_ids) == 20);

// 计数器。无锁累加（InterlockedIncrement64），IOCTL 回读快照。
//   permitted / blocked / redirected 是最终动作的总数；
//   matched_* / fallback_* 是规则命中维度；
//   permitted_* 是各道闸与早退分支的细分。
struct probe_stats
{
    std::uint64_t classify_calls;
    std::uint64_t permitted;
    std::uint64_t blocked;
    std::uint64_t redirected;
    std::uint64_t rules_replaced;
    std::uint64_t rejected_blobs;
    std::uint64_t contexts_allocated;
    std::uint64_t context_alloc_failures;
    std::uint64_t apply_failures;
    std::uint64_t redirect_failures;
    std::uint64_t permitted_disabled;
    std::uint64_t permitted_no_rules;
    std::uint64_t permitted_no_write_right;
    std::uint64_t permitted_unsupported;
    std::uint64_t permitted_non_tcp;
    std::uint64_t permitted_listener_port;
    std::uint64_t permitted_redirect_state;
    std::uint64_t permitted_condition_flags;
    std::uint64_t permitted_already_redirected;
    std::uint64_t permitted_self_process;
    std::uint64_t matched_permit;
    std::uint64_t matched_block;
    std::uint64_t matched_redirect;
    std::uint64_t fallback_permit;
    std::uint64_t fallback_block;
    std::uint64_t fallback_redirect;
    std::uint64_t deferred_blocks; // redirect 层放行、交给 AUTH_CONNECT block filter 的次数
    std::uint64_t process_path_metadata_seen; // 诊断：redirect 层是否给出 PROCESS_PATH（预期 0）
    std::uint32_t last_status;
    std::uint32_t abi_version;
};

static_assert(sizeof(probe_stats) == 232, "probe_stats 是对外 ABI");

// 规则表回读：A1 的判据（规则数、兜底动作与下发一致），以及 A2 的判据（非法 blob 之后
// generation / 内容不变）。
// ioctl_measure_process_path 的输出。输入是 UTF-16LE 字节（≤2048、偶数字节），
// 输出是这个结构：utf8_length = 0 表示“整条不匹配”（超限或非法代理项）。
struct process_path_measurement
{
    std::uint32_t utf8_length;
    std::uint32_t reserved;
    char utf8[512];
};
static_assert(sizeof(process_path_measurement) == 520, "process_path_measurement 布局");

struct probe_rule_info
{
    std::uint32_t abi_version;
    std::uint32_t generation; // 每次成功替换 +1
    std::uint32_t installed; // 1 = 当前有表
    std::uint32_t rule_count;
    std::uint32_t fallback_action; // rule_action 的数值
    std::uint32_t total_size;
    std::uint32_t process_pool_size;
    std::uint32_t network_pool_count;
};

static_assert(sizeof(probe_rule_info) == 32, "probe_rule_info 是对外 ABI");

// 环形事件日志。时间戳是 KeQueryInterruptTime() 的 100ns 计数，仅用于排序与相对比较；
// 地址按原样保存（IPv4 只用前 4 字节），端口为主机字节序。
struct probe_event
{
    std::uint64_t timestamp_100ns;
    std::uint32_t sequence;
    std::uint32_t process_id;
    std::uint8_t remote_address[16];
    std::uint8_t local_address[16];
    std::uint16_t remote_port;
    std::uint16_t local_port;
    std::uint16_t redirect_port;
    std::uint8_t decision;
    std::uint8_t family; // AF_INET / AF_INET6
    std::uint8_t action; // probe_action
    std::uint8_t branch; // redirect_branch
    std::uint32_t matched_rule; // 未命中 / 未走到匹配时为 0xFFFFFFFF
};

static_assert(sizeof(probe_event) == 64, "probe_event 是对外 ABI");
static_assert(offsetof(probe_event, remote_address) == 16);
static_assert(offsetof(probe_event, remote_port) == 48);
static_assert(offsetof(probe_event, matched_rule) == 60);

// 实验（Host UDP 路线 A 探针）：临时 ABI，不进 probe_abi_version。
// 任务 10-21-host-udp-route-a-probe 归档时删除；转正则另立正式 ABI。
inline constexpr std::uint32_t udp_probe_config_version = 1;
inline constexpr std::uint32_t udp_probe_table_capacity = 64;

struct udp_probe_config
{
    std::uint32_t version;
    std::uint32_t enabled;
    std::uint32_t listener_pid;
    std::uint16_t listener_port;
    std::uint8_t return_mode; // 0=off 1=observe 2=rewrite_inline 3=rewrite_inject
    std::uint8_t return_layer; // 0=datagram_data 1=recv_accept
    std::uint16_t remote_port_filter; // 0=all
    std::uint8_t redirect_target; // 0=文档分支(ANY->loopback) 1=target_address
    std::uint8_t reserved0;
    std::uint8_t target_address[16]; // redirect_target==1 时使用；IPv4 用前 4 字节
    std::uint32_t reserved1;
};

static_assert(sizeof(udp_probe_config) == 40, "udp_probe_config 是实验 ABI");
static_assert(offsetof(udp_probe_config, listener_port) == 12);
static_assert(offsetof(udp_probe_config, target_address) == 20);

struct udp_probe_stats
{
    std::uint64_t seen;
    std::uint64_t redirected;
    std::uint64_t redirect_failures;
    std::uint64_t return_seen;
    std::uint64_t return_matched;
    std::uint64_t return_rewritten;
    std::uint64_t return_failures;
    std::uint64_t ippacket_seen;
    std::uint64_t ippacket_matched;
    std::uint32_t table_entries;
    std::uint32_t last_status;
    std::uint32_t abi_version;
    std::uint32_t reserved;
};

static_assert(sizeof(udp_probe_stats) == 88, "udp_probe_stats 是实验 ABI");

struct udp_probe_return_header
{
    std::uint32_t version;
    std::uint32_t app_address; // 网络序
    std::uint32_t original_address; // 网络序
    std::uint16_t app_port; // 主机序
    std::uint16_t original_port; // 主机序
    std::uint16_t payload_length;
    std::uint16_t family; // 4
    std::uint32_t interface_index;
    std::uint32_t compartment_id;
};

static_assert(sizeof(udp_probe_return_header) == 28, "udp_probe_return_header 是实验 ABI");

inline constexpr std::uint32_t ioctl_udp_probe_set = make_ioctl(0x880);
inline constexpr std::uint32_t ioctl_udp_probe_get_stats = make_ioctl(0x881);
inline constexpr std::uint32_t ioctl_udp_probe_return = make_ioctl(0x883);

} // namespace winforward::wfp::probe
