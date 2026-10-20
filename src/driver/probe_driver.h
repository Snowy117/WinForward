#pragma once
// 探针驱动的内部共享面。只有 probe_driver.cpp 与 probe_classify.cpp 两个翻译单元包含它。

#ifndef NDIS630
#define NDIS630 1
#endif

// fwpmk.h 里的层/子层 GUID 用 DEFINE_GUID 声明；不定义 INITGUID 时它们只有 extern 声明，
// 链接阶段会报 FWPM_LAYER_ALE_CONNECT_REDIRECT_V4 一类符号未解析。两个翻译单元都会定义，
// guiddef.h 用的是 DECLSPEC_SELECTANY，重复定义由链接器合并。
#ifndef INITGUID
#define INITGUID
#endif

// clang-format off
// 包含顺序不能按字母重排，三层依赖各有用处：
//   1. 先 C++ 标准头：WDK 的 km\crt 里有一份旧的 <cstddef>，若它先于 MSVC 的版本被包含，
//      std::size_t 就不存在，而 src/common/redirect_context.h 依赖 std::size_t；
//   2. WDK 头内部必须 ntifs -> ntddk -> ndis -> fwpmk -> fwpsk -> wdmsec：ntifs 必须在 ntddk
//      之前（反过来 PEPROCESS/PETHREAD 会重定义）；fwpsk.h 需要 ndis.h 的 NET_BUFFER_LIST，
//      而 ndis.h 又需要先定义 NDIS630；wdmsec.h 提供 SDDL_DEVOBJ_* 与 IoCreateDeviceSecure；
//   3. redirect_context.h 放在最后。
#include "probe_ioctl.h"

#include <ntifs.h>
#include <ntddk.h>
#include <ndis.h>
#include <fwpmk.h>
#include <fwpsk.h>
#include <wdmsec.h>

#include "redirect_context.h"
// clang-format on

namespace winforward::wfp::probe
{

// 多字符字面量 'rPfW' 在小端内存中的字节序是 'W','f','P','r'；
// 用户态按内存字节匹配该 tag 观察非分页池用量。
inline constexpr std::uint32_t pool_tag = 'rPfW';
inline constexpr std::uint32_t event_capacity = 64;

// 驱动的全部可变状态。放在一个结构体里，生命周期与驱动一致；不做动态初始化。
struct probe_state
{
    PDEVICE_OBJECT device_object;
    HANDLE engine_handle;
    HANDLE redirect_handle;
    UINT32 callout_id;
    UINT64 filter_id;
    bool provider_added;
    bool callout_registered;
    bool callout_added;
    bool filter_added;
    KSPIN_LOCK lock;
    probe_config config;
    probe_stats stats;
    probe_event events[event_capacity];
    std::uint32_t event_write_index;
    std::uint64_t event_total;
};

[[nodiscard]] probe_state& probe_state_ref() noexcept;

// classify 侧只通过下面这几个窄接口碰状态，避免把锁的用法散到两个翻译单元。
void probe_take_config(probe_config& target) noexcept;
void probe_increment(std::uint64_t& counter) noexcept;
void probe_log_event(probe_event event) noexcept;
[[nodiscard]] NTSTATUS probe_read_events(void* buffer, std::uint32_t buffer_size, std::uint32_t& event_count) noexcept;
void probe_reset_stats() noexcept;

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

} // namespace winforward::wfp::probe
