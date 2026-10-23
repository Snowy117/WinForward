#pragma once

// 驱动与测试程序之间的控制面 ABI（v7）。C# 侧在 tools/wfprobe/Protocol.cs 里逐字节镜像，
// 两侧都有布局断言。任何字段调整都必须同时提升 probe_abi_version。
//
// 与 src/common/redirect_context.h 的分工：那个头定义的是“用途数据”（随连接走、用户态用
// SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT 读回）；本头定义的是“控制面”（IOCTL 下发的
// 配置与规则 blob、回灌的计数与事件）。两者都不含指针。

#include <cstddef>
#include <cstdint>

namespace winforward::wfp::probe
{

inline constexpr std::uint32_t probe_abi_version = 7;

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

// v5：forwarded TCP 数据面。三块新 IOCTL 与 host 配置完全独立：
//   - set/get_forwarded_config：redirect 的启用开关、listener 端口与本地地址 L（DNAT 目标）；
//   - query_forwarded_redirect：listener 接受连接后按 (listener_port, client_ip, client_port)
//     查原始目的地 / upstream_id / ingress，查不到返回 STATUS_NOT_FOUND。
// 关闭 forwarded config 会清空 redirect 表；换 blob 不清表（旧流保持旧表项与旧 upstream）。
inline constexpr std::uint32_t ioctl_set_forwarded_config = make_ioctl(0x80C);
inline constexpr std::uint32_t ioctl_get_forwarded_config = make_ioctl(0x80D);
inline constexpr std::uint32_t ioctl_query_forwarded_redirect = make_ioctl(0x80E);

// v7：per-ingress listener 映射。v5/v6 的单 listener config（28 字节）保留为 legacy 兼容路径
// （ingress_count == 0，任意入口都 DNAT 到同一个 L）；新路径下发一组 (ifindex, L, listener_port)
// 记录，未配置的入口在命中 redirect 规则时 fail-closed。
//
// 0xFFFFFFFF 是"任意入口"的通配记录：只有找不到精确 ifindex 记录时才使用它。它与规则里的
// ingress_adapter_id 是同一约定；具体 ifindex 0 仍是一个合法入口。
inline constexpr std::uint32_t forwarded_ingress_capacity = 16;
inline constexpr std::uint32_t forwarded_ingress_any_ifindex = 0xFFFFFFFFU;

// 一条入口映射（28 字节）。ifindex 与 listener 地址/端口共同决定 DNAT 目标；同一 ifindex 可以
// 出现多条记录（同一端口绑定多个 L），驱动为新 redirect 表项挑选一个当前回程 key 空闲的 L。
struct probe_forwarded_ingress
{
    std::uint32_t ifindex;
    std::uint16_t listener_port;
    std::uint8_t family; // 4 / 6
    std::uint8_t reserved0;
    std::uint8_t listener_address[16];
    std::uint32_t reserved1;
};

static_assert(sizeof(probe_forwarded_ingress) == 28, "probe_forwarded_ingress 是对外 ABI");
static_assert(offsetof(probe_forwarded_ingress, ifindex) == 0);
static_assert(offsetof(probe_forwarded_ingress, listener_port) == 4);
static_assert(offsetof(probe_forwarded_ingress, family) == 6);
static_assert(offsetof(probe_forwarded_ingress, reserved0) == 7);
static_assert(offsetof(probe_forwarded_ingress, listener_address) == 8);
static_assert(offsetof(probe_forwarded_ingress, reserved1) == 24);

// ioctl_set/get_forwarded_ingress_config 的输入/输出：这个 16 字节头，紧跟 ingress_count 条
// probe_forwarded_ingress（每条 28 字节，从偏移 16 开始连续存放）。记录不是 C++ 数组字段，
// 这样 ABI 扫描器与 C# 侧都能用同一份显式序列化。
struct probe_forwarded_ingress_config
{
    std::uint32_t abi_version;
    std::uint32_t enabled;
    std::uint32_t ingress_count; // <= forwarded_ingress_capacity
    std::uint32_t reserved0;
};

static_assert(sizeof(probe_forwarded_ingress_config) == 16, "probe_forwarded_ingress_config 是对外 ABI");
static_assert(offsetof(probe_forwarded_ingress_config, abi_version) == 0);
static_assert(offsetof(probe_forwarded_ingress_config, enabled) == 4);
static_assert(offsetof(probe_forwarded_ingress_config, ingress_count) == 8);
static_assert(offsetof(probe_forwarded_ingress_config, reserved0) == 12);

inline constexpr std::uint32_t ioctl_set_forwarded_ingress_config = make_ioctl(0x80F);
inline constexpr std::uint32_t ioctl_get_forwarded_ingress_config = make_ioctl(0x810);
inline constexpr std::uint32_t forwarded_ingress_config_size =
    sizeof(probe_forwarded_ingress_config) + forwarded_ingress_capacity * sizeof(probe_forwarded_ingress);

// 一次 redirect 查询的输入：用户态用 listener 端口、本地 listener 地址 L 与对端（client）填。
// TCP 是 accepted socket 的本地/对端，UDP 是收到 datagram 的本地地址与源地址。protocol 区分同一
// listener 端口上的 TCP 与 UDP 表项：0 表示 TCP（B4b 兼容值），6 = TCP，17 = UDP；其他值拒绝。
// family 与 client_address 必须与驱动表项的地址族一致。
//
// v7 在 24 字节 v6 结构后追加 listener_address：查询旧客户端仍可只发 24 字节，驱动按"任意 L"
// 处理；新客户端发 40 字节，L 必须与表项一致（同一端口绑多个 L 时这是唯一的区分键）。
struct probe_forwarded_redirect_query
{
    std::uint16_t listener_port;
    std::uint8_t family; // 4 / 6
    std::uint8_t reserved0;
    std::uint8_t client_address[16];
    std::uint16_t client_port;
    std::uint8_t protocol; // 0/6 = TCP（0 是 B4b 兼容值），17 = UDP
    std::uint8_t reserved1;
    std::uint8_t listener_address[16];
};

inline constexpr std::uint32_t probe_forwarded_redirect_query_v1_size = 24;
static_assert(sizeof(probe_forwarded_redirect_query) == 40, "probe_forwarded_redirect_query 是对外 ABI");
static_assert(offsetof(probe_forwarded_redirect_query, listener_port) == 0);
static_assert(offsetof(probe_forwarded_redirect_query, family) == 2);
static_assert(offsetof(probe_forwarded_redirect_query, client_address) == 4);
static_assert(offsetof(probe_forwarded_redirect_query, client_port) == 20);
static_assert(offsetof(probe_forwarded_redirect_query, protocol) == 22);
static_assert(offsetof(probe_forwarded_redirect_query, reserved1) == 23);
static_assert(offsetof(probe_forwarded_redirect_query, listener_address) == 24);

// redirect 表项的生命周期状态。数值是对外 ABI，只追加。
inline constexpr std::uint32_t forwarded_redirect_state_syn = 1;
inline constexpr std::uint32_t forwarded_redirect_state_established = 2;
inline constexpr std::uint32_t forwarded_redirect_state_closing = 3;

// 查询输出。magic/version 让用户态在解析字段前先认版本；地址按原样保存（IPv4 只用前 4 字节）。
// v1（64 字节）只有 v5 字段；v2 在尾部追加 listener_address 与表项 generation（建表时的规则
// generation），旧客户端只给 64 字节输出时驱动仍写 v1。
inline constexpr std::uint32_t forwarded_redirect_magic = 0x31524657U; // 内存中为 "WFR1"
inline constexpr std::uint16_t forwarded_redirect_version = 1;
inline constexpr std::uint16_t forwarded_redirect_version_with_listener = 2;

struct probe_forwarded_redirect
{
    std::uint32_t magic;
    std::uint16_t version;
    std::uint8_t family; // 4 / 6
    std::uint8_t protocol; // IPPROTO_TCP / IPPROTO_UDP
    std::uint8_t client_address[16];
    std::uint16_t client_port;
    std::uint8_t server_address[16];
    std::uint16_t server_port;
    std::uint16_t listener_port;
    std::uint16_t reserved0;
    std::uint32_t ingress_ifindex;
    std::uint32_t upstream_id;
    std::uint32_t rule_index;
    std::uint32_t state; // forwarded_redirect_state_*
    std::uint8_t listener_address[16]; // v2：DNAT 目标 L（回程 key 的一部分）
    std::uint32_t generation; // v2：表项建表时的 forwarded 规则 generation
    std::uint32_t reserved1; // v2：必须为 0
};

inline constexpr std::uint32_t probe_forwarded_redirect_v1_size = 64;
static_assert(sizeof(probe_forwarded_redirect) == 88, "probe_forwarded_redirect 是对外 ABI");
static_assert(offsetof(probe_forwarded_redirect, magic) == 0);
static_assert(offsetof(probe_forwarded_redirect, version) == 4);
static_assert(offsetof(probe_forwarded_redirect, family) == 6);
static_assert(offsetof(probe_forwarded_redirect, protocol) == 7);
static_assert(offsetof(probe_forwarded_redirect, client_address) == 8);
static_assert(offsetof(probe_forwarded_redirect, client_port) == 24);
static_assert(offsetof(probe_forwarded_redirect, server_address) == 26);
static_assert(offsetof(probe_forwarded_redirect, server_port) == 42);
static_assert(offsetof(probe_forwarded_redirect, listener_port) == 44);
static_assert(offsetof(probe_forwarded_redirect, reserved0) == 46);
static_assert(offsetof(probe_forwarded_redirect, ingress_ifindex) == 48);
static_assert(offsetof(probe_forwarded_redirect, upstream_id) == 52);
static_assert(offsetof(probe_forwarded_redirect, rule_index) == 56);
static_assert(offsetof(probe_forwarded_redirect, state) == 60);
static_assert(offsetof(probe_forwarded_redirect, listener_address) == 64);
static_assert(offsetof(probe_forwarded_redirect, generation) == 80);
static_assert(offsetof(probe_forwarded_redirect, reserved1) == 84);

// forwarded 数据面配置（v5/v6 的单 listener 形式，保留为 legacy 兼容路径）。enabled=1 时
// listener_address/listener_port 是 DNAT 目标：入口帧的目的地址与端口改写为
// (listener_address, listener_port)，再由 Windows 栈投递给绑定在 listener_port 上的进程。
// family 只允许 4 / 6；关闭时 driver 会清空 redirect 表。
//
// v7 起新用户态优先使用 probe_forwarded_ingress_config：它按 ingress ifindex 给出各自的
// L/listener_port。这个 28 字节结构继续可用，语义是"任意入口都 DNAT 到同一个 L"（等价于一条
// ifindex=0xFFFFFFFF 的记录）；两种下发方式互斥，后下发的会替换前一种的运行时映射。

struct probe_forwarded_config
{
    std::uint32_t enabled;
    std::uint16_t listener_port;
    std::uint8_t family; // 4 / 6；enabled=1 时必填
    std::uint8_t reserved0;
    std::uint8_t listener_address[16];
    std::uint32_t reserved1;
};

static_assert(sizeof(probe_forwarded_config) == 28, "probe_forwarded_config 是对外 ABI");
static_assert(offsetof(probe_forwarded_config, enabled) == 0);
static_assert(offsetof(probe_forwarded_config, listener_port) == 4);
static_assert(offsetof(probe_forwarded_config, family) == 6);
static_assert(offsetof(probe_forwarded_config, reserved0) == 7);
static_assert(offsetof(probe_forwarded_config, listener_address) == 8);
static_assert(offsetof(probe_forwarded_config, reserved1) == 24);
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
//       self_injected + no_table + malformed + non_ip + permitted + blocked + redirected
//   permitted == matched_permit + fallback_permit + udp_return_miss
//   blocked == matched_block + fallback_block + redirect_blocked + redirect_return_failed
//              + redirect_return_ambiguous + udp_blocked + udp_return_failed
//              + udp_return_ambiguous
//   matched == matched_permit + matched_block + matched_redirect
//   matched_redirect + udp_fragmented == redirect_ingress + redirect_blocked + udp_ingress + udp_blocked
//   redirect_blocked == no_config + no_state + no_ingress + capacity + non_tcp + inject_failed
//   udp_blocked == udp_no_config + udp_no_state + udp_no_ingress + udp_capacity + udp_fragmented
//                  + udp_inject_failed
//   redirected == redirect_ingress + redirect_return + udp_ingress + udp_return
//   fallback == fallback_permit + fallback_block
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
    // v5 在 stats 尾部追加 redirect 计数；v4 的 0–143 字段偏移不变，last_status / abi_version 后移。
    std::uint64_t matched_redirect;
    std::uint64_t redirected;
    // v5：redirect 数据面计数。bucket 语义：
    //   matched_redirect：命中 redirect 规则的帧数（原帧一定被消费或 block）；
    //   redirected：成功改写并注入的帧数（入口 DNAT + 回程 SNAT）；
    //   redirect_blocked：入口 redirect 失败被 block 的帧数（no_config/no_state/capacity/non_tcp/inject）；
    //   redirect_return_failed：回程 SNAT 失败被 block 的 listener 帧数。
    std::uint64_t redirect_created;
    std::uint64_t redirect_ingress;
    std::uint64_t redirect_return;
    std::uint64_t redirect_blocked;
    std::uint64_t redirect_return_failed;
    std::uint64_t redirect_no_config;
    std::uint64_t redirect_no_state;
    std::uint64_t redirect_capacity;
    std::uint64_t redirect_non_tcp;
    std::uint64_t redirect_inject_failed;
    std::uint64_t redirect_expired;
    std::uint64_t redirect_return_miss;
    // v5 诊断：多 NET_BUFFER 的 NBL、注入完成例程的调用/失败次数。
    std::uint64_t redirect_multi_nb;
    std::uint64_t inject_complete_calls;
    std::uint64_t inject_complete_failed;
    // B5：UDP 数据面计数。bucket 语义与 TCP 一一对应，但计数按 NBL 记（一个 NBL 可含多个 NET_BUFFER）：
    //   udp_created：首个 datagram 建表次数（后续 datagram 命中不增）；
    //   udp_ingress / udp_return：成功 DNAT / SNAT 注入的 NBL 数；
    //   udp_blocked：入口 UDP redirect 失败被 block 的帧数；
    //   udp_return_failed：回程 UDP SNAT 失败被 block 的 listener 帧数；
    //   udp_return_miss：listener 源端口候选但查表未命中的帧数（该帧按普通本机流量 permit）；
    //   udp_no_state 目前保留：UDP 每次未命中都会尝试建表，只有建表失败（capacity）才算失败。
    std::uint64_t udp_created;
    std::uint64_t udp_ingress;
    std::uint64_t udp_return;
    std::uint64_t udp_blocked;
    std::uint64_t udp_return_failed;
    std::uint64_t udp_return_miss;
    std::uint64_t udp_no_config;
    std::uint64_t udp_no_state;
    std::uint64_t udp_capacity;
    std::uint64_t udp_fragmented;
    std::uint64_t udp_inject_failed;
    std::uint64_t udp_expired;
    std::uint64_t udp_multi_nb;
    std::uint32_t redirect_active;
    std::uint32_t redirect_high_water;
    std::uint32_t last_status;
    std::uint32_t abi_version;
    // v7 追加：per-ingress / 多 L 的失败、歧义与混合 NBL 丢弃计数（7 个 u64，448→456 字节）。
    // v6 的 0–399 字节前缀不变；旧客户端取 400/448 字节仍可用，新客户端用
    // sizeof(probe_forwarded_stats) 判断是否包含全部字段。
    //   redirect_return_ambiguous / udp_return_ambiguous：同一个 (L, listener_port, client) 下
    //     出现两条以上表项时，回程帧无法唯一确定原始 server；整帧 block（fail closed），不猜。
    //   redirect_no_ingress / udp_no_ingress：config enabled 但该入口没有 listener 映射。
    //   redirect_multi_l / udp_multi_l：新表项选中了同入口的非首个 L（多 L 分布计数）。
    std::uint64_t redirect_return_ambiguous;
    std::uint64_t udp_return_ambiguous;
    std::uint64_t redirect_no_ingress;
    std::uint64_t udp_no_ingress;
    std::uint64_t redirect_multi_l;
    std::uint64_t udp_multi_l;
    //   udp_return_miss_blocked：UDP 回程 NBL 里既有命中又有候选未命中时，未命中的帧无法单独
    //     permit（整块 NBL 已因命中帧被消费），只能随原 NBL 丢弃；单独计数，不参与
    //     udp_return_miss 的 “permit” 恒等式。单 NB（常见路径）不会产生该计数。
    std::uint64_t udp_return_miss_blocked;
};

inline constexpr std::uint32_t probe_forwarded_stats_v6_size = 400;
inline constexpr std::uint32_t probe_forwarded_stats_v7_size = 448;
static_assert(sizeof(probe_forwarded_stats) == 456, "probe_forwarded_stats 是对外 ABI");

static_assert(offsetof(probe_forwarded_stats, permitted) == 24);
static_assert(offsetof(probe_forwarded_stats, self_injected) == 88);
static_assert(offsetof(probe_forwarded_stats, rejected_blobs) == 128);
static_assert(offsetof(probe_forwarded_stats, tables_replaced) == 136);
static_assert(offsetof(probe_forwarded_stats, matched_redirect) == 144);
static_assert(offsetof(probe_forwarded_stats, redirected) == 152);
static_assert(offsetof(probe_forwarded_stats, redirect_created) == 160);
static_assert(offsetof(probe_forwarded_stats, redirect_multi_nb) == 256);
static_assert(offsetof(probe_forwarded_stats, inject_complete_calls) == 264);
static_assert(offsetof(probe_forwarded_stats, inject_complete_failed) == 272);
static_assert(offsetof(probe_forwarded_stats, udp_created) == 280);
static_assert(offsetof(probe_forwarded_stats, udp_ingress) == 288);
static_assert(offsetof(probe_forwarded_stats, udp_return) == 296);
static_assert(offsetof(probe_forwarded_stats, udp_blocked) == 304);
static_assert(offsetof(probe_forwarded_stats, udp_return_failed) == 312);
static_assert(offsetof(probe_forwarded_stats, udp_return_miss) == 320);
static_assert(offsetof(probe_forwarded_stats, udp_no_config) == 328);
static_assert(offsetof(probe_forwarded_stats, udp_no_state) == 336);
static_assert(offsetof(probe_forwarded_stats, udp_capacity) == 344);
static_assert(offsetof(probe_forwarded_stats, udp_fragmented) == 352);
static_assert(offsetof(probe_forwarded_stats, udp_inject_failed) == 360);
static_assert(offsetof(probe_forwarded_stats, udp_expired) == 368);
static_assert(offsetof(probe_forwarded_stats, udp_multi_nb) == 376);
static_assert(offsetof(probe_forwarded_stats, redirect_active) == 384);
static_assert(offsetof(probe_forwarded_stats, redirect_high_water) == 388);
static_assert(offsetof(probe_forwarded_stats, last_status) == 392);
static_assert(offsetof(probe_forwarded_stats, abi_version) == 396);
static_assert(offsetof(probe_forwarded_stats, redirect_return_ambiguous) == 400);
static_assert(offsetof(probe_forwarded_stats, udp_return_ambiguous) == 408);
static_assert(offsetof(probe_forwarded_stats, redirect_no_ingress) == 416);
static_assert(offsetof(probe_forwarded_stats, udp_no_ingress) == 424);
static_assert(offsetof(probe_forwarded_stats, redirect_multi_l) == 432);
static_assert(offsetof(probe_forwarded_stats, udp_multi_l) == 440);
static_assert(offsetof(probe_forwarded_stats, udp_return_miss_blocked) == 448);

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
