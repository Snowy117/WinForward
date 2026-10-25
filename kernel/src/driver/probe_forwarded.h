#pragma once

// MAC 帧层（FWPS_LAYER_INBOUND/OUTBOUND_MAC_FRAME_NATIVE）的 forwarded 规则表、计数与 callout
// 生命周期。与 host 路径（rule_table / probe_classify / probe_config）完全独立：这里只有
// forwarded_blob 的副本、自己的 generation、自己的自旋锁与自己的 callout id。
//
// B4b 起这里同时承载 TCP redirect 数据面：DNAT、redirect 表、回程 SNAT 与帧注入都由本模块
// 的实现执行；injection handle 在 `probe_forwarded_start` 创建，既用于注入，也用于
// `FwpsQueryPacketInjectionState0` 的防环查询。
//
// include 约束：本头使用 WDK 的 NTSTATUS/HANDLE/ULONG/LONG 与 src/common 的 forwarded_blob.h，
// 只允许在 probe_driver.h 的 WDK 头之后包含。

#include <cstdint>

#include "forwarded_blob.h"
#include "probe_ioctl.h"

namespace winforward::wfp::probe
{

// 一块池内存里的 forwarded blob 副本 + 引用计数。语义与 rule_table 相同：
// classify 在锁内取指针并加引用，换表在同一把锁内换指针，之后等旧表引用归零再释放。
struct forwarded_table
{
    std::uint8_t* data; // 池块内的 blob 副本，4 字节对齐
    std::uint32_t data_size;
    forwarded_blob_view view; // 指向 data，不拥有内存
    volatile LONG references;
    forwarded_table* retired_next; // 引用迟迟不归零时挂到退役链
};

static_assert(sizeof(forwarded_table) % 4 == 0, "blob 副本必须紧跟在结构体后面且保持 4 字节对齐");

// 校验并复制一份 forwarded blob。失败时通过 status 区分「ABI 非法」与「本版不支持」：
//   STATUS_INVALID_PARAMETER —— blob 解不开（magic/版本/段/字段任一不合法）；
//   STATUS_NOT_SUPPORTED     —— blob 合法，但含 B4a 不执行的 redirect 规则。
[[nodiscard]] forwarded_table*
allocate_forwarded_table(const std::uint8_t* data, std::uint32_t size, NTSTATUS& status) noexcept;
void free_forwarded_table(forwarded_table* table) noexcept;

// DriverEntry 里调用一次：初始化 forwarded 自己的自旋锁（必须在任何 classify 之前）。
void probe_forwarded_init() noexcept;

// 注册两层 callout / filter 并创建 L2 注入句柄。失败时内部已按注册逆序回滚。
// engine_handle 是 probe_driver.cpp 打开的 WFP 引擎（与 host 共用）。
[[nodiscard]] NTSTATUS probe_forwarded_start(HANDLE engine_handle) noexcept;
// 注销两层 callout / filter 并销毁注入句柄；返回后不会再有 forwarded classify。可重复调用。
void probe_forwarded_stop(HANDLE engine_handle) noexcept;
// 卸载路径：callout 已注销、不可能再有读者，直接释放当前表与全部退役表。
void probe_forwarded_release_all() noexcept;

// ioctl_set_forwarded_blob。非法整块拒绝、旧表与 generation 不变。
[[nodiscard]] NTSTATUS probe_forwarded_apply_blob(const void* buffer, ULONG input_length) noexcept;
[[nodiscard]] NTSTATUS probe_forwarded_apply_config(const void* buffer, ULONG input_length) noexcept;
// v7：per-ingress listener 映射。两种下发方式互斥：后下发的替换前一种的有效映射。
[[nodiscard]] NTSTATUS probe_forwarded_apply_ingress_config(const void* buffer, ULONG input_length) noexcept;
[[nodiscard]] NTSTATUS probe_forwarded_get_config(void* buffer, ULONG output_length) noexcept;
// 输出 16 字节头 + ingress_count 条记录；written 是实际写回的字节数。
[[nodiscard]] NTSTATUS probe_forwarded_get_ingress_config(void* buffer, ULONG output_length, ULONG& written) noexcept;
[[nodiscard]] NTSTATUS
probe_forwarded_query_redirect(const void* buffer, ULONG input_length, void* output, ULONG output_length) noexcept;
void probe_forwarded_info_snapshot(probe_forwarded_info& info) noexcept;
void probe_forwarded_stats_snapshot(probe_forwarded_stats& stats) noexcept;
void probe_forwarded_reset_stats() noexcept;
// 两层的 classify 共用同一份实现（方向由 layerId 判定）。
void NTAPI probe_forwarded_classify_fn(const FWPS_INCOMING_VALUES0* in_fixed_values,
                                       const FWPS_INCOMING_METADATA_VALUES0* in_meta_values,
                                       void* layer_data,
                                       const void* classify_context,
                                       const FWPS_FILTER1* filter,
                                       UINT64 flow_context,
                                       FWPS_CLASSIFY_OUT0* classify_out) noexcept;

NTSTATUS NTAPI probe_forwarded_notify_fn(FWPS_CALLOUT_NOTIFY_TYPE notify_type,
                                         const GUID* filter_key,
                                         FWPS_FILTER1* filter) noexcept;

} // namespace winforward::wfp::probe
