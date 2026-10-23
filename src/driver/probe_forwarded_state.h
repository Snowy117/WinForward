#pragma once

// forwarded 的跨翻译单元内部状态：规则表指针、generation、自旋锁、注入句柄、两层 callout
// 对象与计数。probe_forwarded.cpp（生命周期 / 换表 / IOCTL）与 probe_forwarded_classify.cpp
// （NBL 解析 / 匹配 / 计数）共用，不对外。
//
// include 约束：只能在 probe_driver.h 之后包含 —— 状态里用到 WDK 的 KSPIN_LOCK/HANDLE 与
// probe_driver.h 定义的 layer_state。

#include <cstdint>

#include "forwarded_fragment_table.h"
#include "probe_forwarded.h"
#include "probe_forwarded_frame.h"
#include "probe_forwarded_redirect.h"

namespace winforward::wfp::probe
{

struct forwarded_state
{
    forwarded_table* table;
    forwarded_table* retired_tables;
    std::uint32_t generation;
    KSPIN_LOCK lock;
    HANDLE injection_handle;
    NDIS_HANDLE nbl_pool; // 注入帧的 NBL 池（FwpsAllocateNetBufferAndNetBufferList0 使用）
    volatile LONG64 outstanding_injections; // 已排队但完成例程还没回调的注入数
    std::uint32_t injection_complete_delay_us; // 故障注入：完成例程延迟，用于 stop 的 outstanding 测试
    std::uint32_t last_completion_delay_us;
    std::uint64_t delayed_completions;
    layer_state inbound;
    layer_state outbound;
    probe_forwarded_config config;
    forwarded_redirect_table redirects;
    forwarded_fragment_slot fragment_blocks[forwarded_fragment_capacity];
    std::uint32_t fragment_block_cursor;
    probe_forwarded_stats stats;
};
extern forwarded_state g_forwarded_state;

// classify 在 DISPATCH_LEVEL，IOCTL 在 PASSIVE_LEVEL，两边都要能用这把锁；
_IRQL_requires_max_(DISPATCH_LEVEL) _IRQL_saves_global_(OldIrql, old_irql) _IRQL_raises_(DISPATCH_LEVEL)
    [[nodiscard]] inline bool forwarded_lock(KIRQL& old_irql) noexcept
{
    const bool raised = KeGetCurrentIrql() < DISPATCH_LEVEL;
    if (raised)
    {
        KeAcquireSpinLock(&g_forwarded_state.lock, &old_irql);
    }
    else
    {
        KeAcquireSpinLockAtDpcLevel(&g_forwarded_state.lock);
    }
    return raised;
}
// 返回值表示是否在调用点抬升了 IRQL。语义与 probe_lock 相同。
_IRQL_requires_(DISPATCH_LEVEL)
    _IRQL_restores_global_(OldIrql, old_irql) inline void forwarded_unlock(bool raised, KIRQL old_irql) noexcept
{
    if (raised)
    {
        KeReleaseSpinLock(&g_forwarded_state.lock, old_irql);
    }
    else
    {
        KeReleaseSpinLockFromDpcLevel(&g_forwarded_state.lock);
    }
}

inline forwarded_fragment_key make_forwarded_fragment_key(const forwarded_frame_facts& facts,
                                                          std::uint32_t ingress_adapter_id) noexcept
{
    forwarded_fragment_key key{};
    key.family = facts.family;
    key.protocol = facts.protocol;
    key.ingress_adapter_id = ingress_adapter_id;
    key.identification = facts.fragment_id;
    const std::uint32_t address_size = facts.family == 6 ? 16U : 4U;
    RtlCopyMemory(key.source_address, facts.source_address, address_size);
    RtlCopyMemory(key.destination_address, facts.destination_address, address_size);
    return key;
}

inline void remember_forwarded_fragment_block(forwarded_state& state,
                                              const forwarded_frame_facts& facts,
                                              std::uint32_t ingress_adapter_id) noexcept
{
    const forwarded_fragment_key key = make_forwarded_fragment_key(facts, ingress_adapter_id);
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    forwarded_fragment_remember(
        state.fragment_blocks, state.fragment_block_cursor, key, static_cast<std::uint64_t>(KeQueryInterruptTime()));
    forwarded_unlock(raised, old_irql);
}

[[nodiscard]] inline bool forwarded_fragment_is_blocked(forwarded_state& state,
                                                        const forwarded_frame_facts& facts,
                                                        std::uint32_t ingress_adapter_id) noexcept
{
    if (!facts.fragmented || facts.first_fragment)
    {
        return false;
    }

    const forwarded_fragment_key key = make_forwarded_fragment_key(facts, ingress_adapter_id);
    const std::uint64_t now = static_cast<std::uint64_t>(KeQueryInterruptTime());
    bool matched = false;
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    matched = forwarded_fragment_matches(state.fragment_blocks, key, now);
    forwarded_unlock(raised, old_irql);
    return matched;
}

#if defined(WFPROBE_FAULT_INJECTION)
[[nodiscard]] inline std::uint32_t read_injection_complete_delay_us() noexcept
{
    std::uint32_t value = 0;
    RTL_QUERY_REGISTRY_TABLE query[2]{};
    query[0].Flags = RTL_QUERY_REGISTRY_DIRECT;
    query[0].Name = const_cast<wchar_t*>(L"InjectionCompleteDelayUs");
    query[0].EntryContext = &value;
    query[0].DefaultType = REG_DWORD;
    query[0].DefaultData = &value;
    query[0].DefaultLength = sizeof(value);
    const NTSTATUS status = RtlQueryRegistryValues(RTL_REGISTRY_SERVICES, L"wfprobe", query, nullptr, nullptr);
    if (!NT_SUCCESS(status) || value > 15'000'000U)
    {
        return 0;
    }
    return value;
}
#endif

#if defined(WFPROBE_FAULT_INJECTION)
inline void record_stop_counter(const wchar_t* name, LONG64 value) noexcept
{
    const std::uint32_t low = static_cast<std::uint32_t>(value);
    RtlWriteRegistryValue(RTL_REGISTRY_SERVICES,
                          L"wfprobe",
                          const_cast<wchar_t*>(name),
                          REG_DWORD,
                          const_cast<std::uint32_t*>(&low),
                          sizeof(low));
}
#endif

inline void install_forwarded_test_delay(forwarded_state& state) noexcept
{
#if defined(WFPROBE_FAULT_INJECTION)
    state.injection_complete_delay_us = read_injection_complete_delay_us();
    if (state.injection_complete_delay_us != 0)
    {
        DbgPrint("wfprobe: fault injection completion delay=%u us\n", state.injection_complete_delay_us);
    }
    record_stop_counter(L"InjectionDelayEffectiveUs", state.injection_complete_delay_us);
#else
    UNREFERENCED_PARAMETER(state);
#endif
}

inline LONG64 record_forwarded_stop_entry(forwarded_state& state) noexcept
{
#if defined(WFPROBE_FAULT_INJECTION)
    const LONG64 value = InterlockedCompareExchange64(&state.outstanding_injections, 0, 0);
    record_stop_counter(L"LastStopOutstandingAtEntry", value);
    DbgPrint("wfprobe: forwarded stop entry outstanding_injections=%lld\n", value);
    return value;
#else
    UNREFERENCED_PARAMETER(state);
    return 0;
#endif
}

inline void record_forwarded_stop_counters(forwarded_state& state, LONG64 outstanding_after) noexcept
{
#if defined(WFPROBE_FAULT_INJECTION)
    record_stop_counter(L"LastStopOutstandingAfterDrain", outstanding_after);
    record_stop_counter(L"LastStopDelayedCompletions", static_cast<LONG64>(state.delayed_completions));
    record_stop_counter(L"LastStopCompletionDelayUs", state.last_completion_delay_us);
#else
    UNREFERENCED_PARAMETER(state);
    UNREFERENCED_PARAMETER(outstanding_after);
#endif
}

inline void finalize_forwarded_injection_pool(forwarded_state& state, bool drained, LONG64 outstanding_after) noexcept
{
    if (!drained)
    {
        DbgPrint("wfprobe: forwarded stop outstanding_injections=%lld; leaking NBL pool (tag WfIj) to avoid UAF\n",
                 outstanding_after);
        return;
    }
    if (state.nbl_pool != nullptr)
    {
        NdisFreeNetBufferListPool(state.nbl_pool);
        state.nbl_pool = nullptr;
    }
}

// classify 侧取表：锁内换指针并加引用；没有表时 table 保持 nullptr。
inline void take_forwarded_snapshot(forwarded_table*& table) noexcept
{
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    table = g_forwarded_state.table;
    if (table != nullptr)
    {
        InterlockedIncrement(const_cast<volatile LONG*>(&table->references));
    }
    forwarded_unlock(raised, old_irql);
}

inline void release_forwarded_snapshot(const forwarded_table* table) noexcept
{
    if (table != nullptr)
    {
        InterlockedDecrement(const_cast<volatile LONG*>(&table->references));
    }
}

// 一帧（或一次控制面操作）的计数增量：先攒在本地，最后在同一把锁内一次性并入全局计数。
// classify 的每个返回路径只调用一次；snapshot / reset 也用这把锁，因此任何快照都不会读到
// 「classify_calls 已加、终态还没加」的中间状态，reset 之后的增量同样满足恒等式。
inline void publish_forwarded_counters(forwarded_state& state, const probe_forwarded_stats& delta) noexcept
{
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    state.stats.classify_calls += delta.classify_calls;
    state.stats.inbound_frames += delta.inbound_frames;
    state.stats.outbound_frames += delta.outbound_frames;
    state.stats.permitted += delta.permitted;
    state.stats.blocked += delta.blocked;
    state.stats.matched += delta.matched;
    state.stats.matched_permit += delta.matched_permit;
    state.stats.matched_block += delta.matched_block;
    state.stats.fallback += delta.fallback;
    state.stats.fallback_permit += delta.fallback_permit;
    state.stats.fallback_block += delta.fallback_block;
    state.stats.self_injected += delta.self_injected;
    state.stats.no_table += delta.no_table;
    state.stats.malformed += delta.malformed;
    state.stats.non_ip += delta.non_ip;
    state.stats.no_write_right += delta.no_write_right;
    state.stats.rejected_blobs += delta.rejected_blobs;
    state.stats.tables_replaced += delta.tables_replaced;
    state.stats.matched_redirect += delta.matched_redirect;
    state.stats.redirected += delta.redirected;
    state.stats.redirect_created += delta.redirect_created;
    state.stats.redirect_ingress += delta.redirect_ingress;
    state.stats.redirect_return += delta.redirect_return;
    state.stats.redirect_blocked += delta.redirect_blocked;
    state.stats.redirect_return_failed += delta.redirect_return_failed;
    state.stats.redirect_no_config += delta.redirect_no_config;
    state.stats.redirect_no_state += delta.redirect_no_state;
    state.stats.redirect_capacity += delta.redirect_capacity;
    state.stats.redirect_non_tcp += delta.redirect_non_tcp;
    state.stats.redirect_inject_failed += delta.redirect_inject_failed;
    state.stats.redirect_expired += delta.redirect_expired;
    state.stats.redirect_return_miss += delta.redirect_return_miss;
    state.stats.redirect_multi_nb += delta.redirect_multi_nb;
    state.stats.udp_created += delta.udp_created;
    state.stats.udp_ingress += delta.udp_ingress;
    state.stats.udp_return += delta.udp_return;
    state.stats.udp_blocked += delta.udp_blocked;
    state.stats.udp_return_failed += delta.udp_return_failed;
    state.stats.udp_return_miss += delta.udp_return_miss;
    state.stats.udp_no_config += delta.udp_no_config;
    state.stats.udp_no_state += delta.udp_no_state;
    state.stats.udp_capacity += delta.udp_capacity;
    state.stats.udp_fragmented += delta.udp_fragmented;
    state.stats.udp_inject_failed += delta.udp_inject_failed;
    state.stats.udp_expired += delta.udp_expired;
    state.stats.udp_multi_nb += delta.udp_multi_nb;
    forwarded_unlock(raised, old_irql);
}

} // namespace winforward::wfp::probe
