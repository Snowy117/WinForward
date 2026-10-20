#pragma once

// 驱动内部共享面：probe_driver / probe_control / probe_classify / rule_table / block_filters /
// redirect_target 六个翻译单元。设备与 WFP 对象的生命周期只在 probe_driver.cpp 里；classify 侧只碰下面列出的窄接口。

#ifndef NDIS630
#define NDIS630 1
#endif

// fwpmk.h 里的层/子层 GUID 用 DEFINE_GUID 声明；不定义 INITGUID 时它们只有 extern 声明，
// 链接阶段会报 FWPM_LAYER_ALE_CONNECT_REDIRECT_V4/V6 一类符号未解析。三个翻译单元都会定义，
// guiddef.h 用的是 DECLSPEC_SELECTANY，重复定义由链接器合并。
#ifndef INITGUID
#define INITGUID
#endif

// clang-format off
// 包含顺序不能按字母重排，三层依赖各有用处：
//   1. 先 C++ 标准头：WDK 的 km\crt 里有一份旧的 <cstddef>，若它先于 MSVC 的版本被包含，
//      std::size_t 就不存在，而 src/common/*.h 依赖 std::size_t；
//   2. WDK 头内部必须 ntifs -> ntddk -> ndis -> fwpmk -> fwpsk -> wdmsec：ntifs 必须在 ntddk
//      之前（反过来 PEPROCESS/PETHREAD 会重定义）；fwpsk.h 需要 ndis.h 的 NET_BUFFER_LIST，
//      而 ndis.h 又需要先定义 NDIS630；wdmsec.h 提供 SDDL_DEVOBJ_* 与 IoCreateDeviceSecure；
//   3. src/common 的头放在最后，它们只依赖标准头。
// clang-format 会重排这个块，所以整段用 off/on 锁住。
#include "probe_ioctl.h"

#include <ntifs.h>
#include <ntddk.h>
#include <ndis.h>
#include <fwpmk.h>
#include <fwpsk.h>
#include <wdmsec.h>

#include "redirect_context.h"
#include "matcher.h"
#include "rule_blob.h"
#include "rule_table.h"
// clang-format on

namespace winforward::wfp::probe
{

// 驱动自己的 provider key，以及 block filter 的 key 模板与显示名（probe_driver.cpp 与
// block_filters.cpp 共用）。
inline constexpr GUID provider_key{ 0x3f6a1d24, 0x7c58, 0x4b9e, { 0x8d, 0x21, 0x6a, 0x0f, 0x5c, 0x3e, 0x91, 0x77 } };
inline constexpr GUID block_filter_key_template{
    0xa0d3849b, 0xe3cf, 0xb265, { 0xf4, 0x98, 0xd1, 0x76, 0xc3, 0xa5, 0x08, 0xee }
};
inline constexpr wchar_t block_filter_name[] = L"WinForward Block Filter";

// 多字符字面量 'rPfW' 在小端内存中的字节序是 'W','f','P','r'；
// 用户态按内存字节匹配该 tag 观察非分页池用量。
inline constexpr std::uint32_t pool_tag = 'rPfW';
// 规则表用单独的池标签：用户态按 WfPr 观察的是 redirect context 的分配/释放。
inline constexpr std::uint32_t rule_pool_tag = 'rTfW';

// block filter 的显式权重（FWP_UINT64）。跨子层次序由 FWPM_SUBLAYER_UNIVERSAL 决定，
// 这个值只用于同子层内不依赖引擎默认权重推导；依据见 block_filters.cpp 的 filter 构造处。
inline constexpr std::uint64_t auth_filter_weight = 0x10;

// 一次最多能编译出多少条 block filter（V4+V6 合计）。规则 blob 的上限是 64 KiB，
// 但 block 规则的数量应该远小于这个值；超出时整块 blob 被拒绝，避免静默丢弃规则。
inline constexpr std::uint32_t max_block_filters = 64;
inline constexpr std::uint32_t event_capacity = 64;

// 规则进程池是 UTF-8 字节串（ASCII 大小写折叠，非 ASCII 原样）。连接侧把 ALE_APP_ID 的
// UTF-16 路径手工转成 UTF-8，输出上限就是这个字节数；超限或非法代理项整条不匹配，不截断。
inline constexpr std::size_t max_process_path_length = 512;

struct layer_state
{
    UINT32 callout_id;
    UINT64 filter_id;
    bool callout_registered;
    bool callout_added;
    bool filter_added;
};

// 驱动的全部可变状态。生命周期与驱动一致；不做动态初始化。
struct probe_state
{
    PDEVICE_OBJECT device_object;
    HANDLE engine_handle;
    HANDLE redirect_handle;
    layer_state v4;
    layer_state v6;
    bool provider_added;
    // block 规则编译出的 ALE_AUTH_CONNECT 过滤器（V4/V6 混合，按创建顺序记录 id）。
    UINT64 block_filter_ids[max_block_filters];
    std::uint32_t block_filter_count;
    std::uint32_t block_filter_serial; // 每次都换新 key，避免与仍在生效的旧 filter 撞 key
    KMUTEX rule_mutex; // 串行化「换表 + block filter 增删」，只在 PASSIVE 的 IOCTL 路径上等
    KSPIN_LOCK lock; // 保护 config / rules / generation / 退役链 / 事件环
    probe_config config;
    probe_stats stats;
    rule_table* rules;
    rule_table* retired_rules;
    std::uint32_t generation;
    probe_event events[event_capacity];
    std::uint32_t event_write_index;
    std::uint64_t event_total;
};

[[nodiscard]] probe_state& probe_state_ref() noexcept;

// 自旋锁：classify 在 DISPATCH_LEVEL，IOCTL 在 PASSIVE_LEVEL，两边都要能用。
// 返回值表示是否在调用点抬升了 IRQL（决定释放时用哪个变体）。
[[nodiscard]] bool probe_lock(KIRQL& old_irql) noexcept;
void probe_unlock(bool raised, KIRQL old_irql) noexcept;

// classify 侧接口。
void probe_take_snapshot(probe_config& config, const rule_table*& rules) noexcept;
void probe_release_rules(const rule_table* rules) noexcept;
void probe_increment(std::uint64_t& counter) noexcept;
void probe_log_event(const probe_event& event) noexcept;

// 控制面接口（dispatch 用）。
NTSTATUS probe_read_events(void* buffer, std::uint32_t buffer_size, std::uint32_t& event_count) noexcept;
void probe_reset_stats() noexcept;

// IOCTL 分派（实现在 probe_control.cpp）：create_device 把它们装进 MajorFunction 表。
NTSTATUS dispatch_create_close(PDEVICE_OBJECT device_object, PIRP irp) noexcept;
NTSTATUS dispatch_device_control(PDEVICE_OBJECT device_object, PIRP irp) noexcept;

// 由 redirect 分支调用：把 remoteAddressAndPort 改成 127.0.0.1/::1 或连接原本的 local 地址。
void probe_build_redirect_target(const SOCKADDR* remote,
                                 const SOCKADDR* local,
                                 std::uint16_t listener_port,
                                 SOCKADDR_STORAGE& target,
                                 redirect_branch& branch) noexcept;

// 把 ALE_APP_ID 的 UTF-16LE 字节转换成规则池用的 UTF-8（纯算术，可在 DISPATCH_LEVEL 跑）。
void collect_process_path(const void* data, std::uint32_t byte_count, char* target, std::uint32_t& length) noexcept;

// 诊断用：把 UTF-16LE 缓冲区按 collect_process_path 的规则转成 UTF-8（边界测试）。
void probe_measure_process_path(const void* data,
                                std::uint32_t byte_count,
                                char* target,
                                std::uint32_t& length) noexcept;

void NTAPI probe_classify_fn(const FWPS_INCOMING_VALUES0* in_fixed_values,
                             const FWPS_INCOMING_METADATA_VALUES0* in_meta_values,
                             void* layer_data,
                             const void* classify_context,
                             const FWPS_FILTER1* filter,
                             UINT64 flow_context,
                             FWPS_CLASSIFY_OUT0* classify_out) noexcept;

NTSTATUS NTAPI probe_notify_fn(FWPS_CALLOUT_NOTIFY_TYPE notify_type,
                               const GUID* filter_key,
                               FWPS_FILTER1* filter) noexcept;

// block 规则在 ALE_AUTH_CONNECT 上以 FWP_ACTION_BLOCK filter 执行：redirect 层不能阻断连接
// （research 有实测）。filter 只在规则表里存在 block 规则时创建，规则替换时整组换掉。
[[nodiscard]] NTSTATUS probe_install_block_filters(const blob_view& view, UINT64* ids, std::uint32_t& count) noexcept;
void probe_delete_block_filters(UINT64* ids, std::uint32_t& count) noexcept;

} // namespace winforward::wfp::probe
