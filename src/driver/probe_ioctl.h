#pragma once

// 驱动与测试程序之间的控制面 ABI（v4）。C# 侧在 tools/wfprobe/Protocol.cs 里逐字节镜像，
// 两侧都有布局断言。任何字段调整都必须同时提升 probe_abi_version。
//
// 与 src/common/redirect_context.h 的分工：那个头定义的是“用途数据”（随连接走、用户态用
// SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT 读回）；本头定义的是“控制面”（IOCTL 下发的
// 配置与规则 blob、回灌的计数与事件）。两者都不含指针。

#include <cstddef>
#include <cstdint>

namespace winforward::wfp::probe
{

inline constexpr std::uint32_t probe_abi_version = 4;

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

// v4：MAC 帧层（forwarded）规则表。三块 IOCTL 与 host 的规则表完全独立，互不影响：
//   - set_forwarded_blob 只替换 forwarded 表，不碰 rule_table / probe_config / ALE callout；
//   - get_forwarded_info / get_forwarded_stats 是只读快照；
//   - reset_forwarded_stats 只清 forwarded 计数，host 的 reset_stats 语义不变。
inline constexpr std::uint32_t ioctl_set_forwarded_blob = make_ioctl(0x808);
inline constexpr std::uint32_t ioctl_get_forwarded_info = make_ioctl(0x809);
inline constexpr std::uint32_t ioctl_get_forwarded_stats = make_ioctl(0x80A);
inline constexpr std::uint32_t ioctl_reset_forwarded_stats = make_ioctl(0x80B);

// 一次 classify 的结论。数值是对外 ABI，C# 侧按名字镜像；新增只追加。
enum class probe_decision : std::uint8_t
{
    permitted_disabled = 0,
    permitted_no_write_right = 1,
    permitted_non_tcp = 2, // 协议既非 TCP 也非 UDP
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
    std::uint64_t permitted_non_tcp; // 协议既非 TCP 也非 UDP 的早退
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
    std::uint64_t udp_classify_calls; // classify 层决策数：进入 classify 且协议为 UDP
    std::uint64_t udp_permitted; // UDP 最终放行（含 redirect 失败后的 fail-open）
    std::uint64_t udp_blocked; // classify 层 block 决策数；热替换后旧关联在 auth filter 层丢弃的 datagram 不计入
    std::uint64_t udp_redirected; // UDP 成功挂上 redirect context
    std::uint32_t last_status;
    std::uint32_t abi_version;
};

static_assert(sizeof(probe_stats) == 264, "probe_stats 是对外 ABI");
static_assert(offsetof(probe_stats, udp_classify_calls) == 224);
static_assert(offsetof(probe_stats, udp_permitted) == 232);
static_assert(offsetof(probe_stats, udp_blocked) == 240);
static_assert(offsetof(probe_stats, udp_redirected) == 248);
static_assert(offsetof(probe_stats, last_status) == 256);
static_assert(offsetof(probe_stats, abi_version) == 260);

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

// forwarded 规则表回读。字段与 probe_rule_info 同构：判据同样是「非法 blob 之后 generation 不变」。
// abi_version 放在偏移 0，客户端在解析其余字段之前先核对版本。
struct probe_forwarded_info
{
    std::uint32_t abi_version;
    std::uint32_t generation; // 每次成功替换 +1
    std::uint32_t installed; // 1 = 当前有表
    std::uint32_t rule_count;
    std::uint32_t fallback_action; // rule_action 的数值
    std::uint32_t total_size;
    std::uint32_t network_pool_count;
    std::uint32_t reserved0; // 必须为 0
};

static_assert(sizeof(probe_forwarded_info) == 32, "probe_forwarded_info 是对外 ABI");
static_assert(offsetof(probe_forwarded_info, abi_version) == 0);
static_assert(offsetof(probe_forwarded_info, generation) == 4);
static_assert(offsetof(probe_forwarded_info, installed) == 8);
static_assert(offsetof(probe_forwarded_info, rule_count) == 12);
static_assert(offsetof(probe_forwarded_info, fallback_action) == 16);
static_assert(offsetof(probe_forwarded_info, total_size) == 20);
static_assert(offsetof(probe_forwarded_info, network_pool_count) == 24);
static_assert(offsetof(probe_forwarded_info, reserved0) == 28);

// MAC 帧层计数。全部是单调累加，reset_forwarded_stats 清零后重新累加。
// 恒等式（对任意 reset 之后的增量成立）：
//   classify_calls == no_write_right + inbound_frames + outbound_frames
//   inbound_frames + outbound_frames ==
//       self_injected + no_table + malformed + non_ip + permitted + blocked
//   permitted == matched_permit + fallback_permit；blocked == matched_block + fallback_block
//   matched == matched_permit + matched_block；fallback == fallback_permit + fallback_block
struct probe_forwarded_stats
{
    std::uint64_t classify_calls;
    std::uint64_t inbound_frames;
    std::uint64_t outbound_frames;
    std::uint64_t permitted;
    std::uint64_t blocked;
    std::uint64_t matched;
    std::uint64_t matched_permit;
    std::uint64_t matched_block;
    std::uint64_t fallback;
    std::uint64_t fallback_permit;
    std::uint64_t fallback_block;
    std::uint64_t self_injected; // 防环：FwpsQueryPacketInjectionState0 判定为本驱动注入
    std::uint64_t no_table; // 没有 forwarded 表时的默认放行
    std::uint64_t malformed; // NBL / 以太网 / IP / 传输层头部解析失败（一律放行）
    std::uint64_t non_ip; // 非 IPv4/IPv6 帧（ARP 等）：forwarded 规则不接管，一律放行
    std::uint64_t no_write_right;
    std::uint64_t rejected_blobs;
    std::uint64_t tables_replaced;
    std::uint32_t last_status;
    std::uint32_t abi_version;
};

static_assert(sizeof(probe_forwarded_stats) == 152, "probe_forwarded_stats 是对外 ABI");
static_assert(offsetof(probe_forwarded_stats, permitted) == 24);
static_assert(offsetof(probe_forwarded_stats, self_injected) == 88);
static_assert(offsetof(probe_forwarded_stats, rejected_blobs) == 128);
static_assert(offsetof(probe_forwarded_stats, tables_replaced) == 136);
static_assert(offsetof(probe_forwarded_stats, last_status) == 144);
static_assert(offsetof(probe_forwarded_stats, abi_version) == 148);

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
    std::uint8_t protocol; // IPPROTO_TCP / IPPROTO_UDP
    std::uint8_t reserved0; // 显式占位，保持 matched_rule 在 60
    std::uint32_t matched_rule; // 未命中 / 未走到匹配时为 0xFFFFFFFF
};

static_assert(sizeof(probe_event) == 64, "probe_event 是对外 ABI");
static_assert(offsetof(probe_event, remote_address) == 16);
static_assert(offsetof(probe_event, remote_port) == 48);
static_assert(offsetof(probe_event, branch) == 57);
static_assert(offsetof(probe_event, protocol) == 58);
static_assert(offsetof(probe_event, reserved0) == 59);
static_assert(offsetof(probe_event, matched_rule) == 60);

} // namespace winforward::wfp::probe
