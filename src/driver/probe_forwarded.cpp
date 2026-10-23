#include "probe_driver.h"
#include "probe_forwarded_state.h"

namespace winforward::wfp::probe
{
namespace
{

// 两层的 callout / filter 各用独立 GUID：FWPM_CALLOUT0 只有一个 applicableLayer，
// calloutKey 也必须唯一。与 host 的 ALE callout 完全不同的值，便于 netsh wfp show 对照。
constexpr GUID forwarded_callout_key_inbound{
    0x9c4e17a8, 0x2d63, 0x4f0b, { 0xa1, 0x77, 0x5e, 0x28, 0xb4, 0x60, 0xd3, 0x91 }
};
constexpr GUID forwarded_callout_key_outbound{
    0x3e8b25c9, 0x4f71, 0x5a2d, { 0xb8, 0x16, 0x7c, 0x39, 0xe5, 0x84, 0x2f, 0x6a }
};
constexpr GUID forwarded_filter_key_inbound{
    0x7a1d63e4, 0x8c5b, 0x49f2, { 0x9d, 0x30, 0x62, 0xfe, 0x1b, 0xc7, 0x58, 0x0d }
};
constexpr GUID forwarded_filter_key_outbound{
    0x2b6f04d8, 0x15e9, 0x4b7a, { 0x8c, 0xf5, 0x39, 0x70, 0xa6, 0x2d, 0xe1, 0x43 }
};

constexpr wchar_t forwarded_callout_name_inbound[] = L"WinForward MAC_FRAME_NATIVE Inbound";
constexpr wchar_t forwarded_callout_name_outbound[] = L"WinForward MAC_FRAME_NATIVE Outbound";
constexpr wchar_t forwarded_filter_name_inbound[] = L"WinForward Forwarded Filter Inbound";
constexpr wchar_t forwarded_filter_name_outbound[] = L"WinForward Forwarded Filter Outbound";
constexpr wchar_t forwarded_callout_description[] =
    L"WinForward forwarded rules on native MAC frame layers (pass/block)";
constexpr std::uint32_t retire_wait_rounds = 200; // 10 ms * 200 = 2 s
constexpr std::uint32_t retire_wait_milliseconds = 10;
constexpr std::uint32_t injection_pool_tag = 'jIfW';
} // namespace

forwarded_state g_forwarded_state{};

namespace
{

[[nodiscard]] bool references_drained(const forwarded_table* table) noexcept
{
    return InterlockedCompareExchange(const_cast<volatile LONG*>(&table->references), 0, 0) == 0;
}

void wait_one_tick() noexcept
{
    LARGE_INTEGER interval{};
    interval.QuadPart = -static_cast<LONGLONG>(retire_wait_milliseconds) * 10000;
    KeDelayExecutionThread(KernelMode, FALSE, &interval);
}

void push_retired(forwarded_table* table) noexcept
{
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    table->retired_next = g_forwarded_state.retired_tables;
    g_forwarded_state.retired_tables = table;
    forwarded_unlock(raised, old_irql);
}

void drain_retired() noexcept
{
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    forwarded_table* free_list = nullptr;
    forwarded_table** link = &g_forwarded_state.retired_tables;
    while (*link != nullptr)
    {
        forwarded_table* current = *link;
        if (references_drained(current))
        {
            *link = current->retired_next;
            current->retired_next = free_list;
            free_list = current;
        }
        else
        {
            link = &current->retired_next;
        }
    }
    forwarded_unlock(raised, old_irql);

    while (free_list != nullptr)
    {
        forwarded_table* next = free_list->retired_next;
        free_forwarded_table(free_list);
        free_list = next;
    }
}

void replace_table(forwarded_table* table) noexcept
{
    forwarded_state& state = g_forwarded_state;

    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    forwarded_table* previous = state.table;
    state.table = table;
    ++state.generation;
    forwarded_unlock(raised, old_irql);

    if (previous != nullptr)
    {
        for (std::uint32_t attempt = 0; attempt < retire_wait_rounds; ++attempt)
        {
            if (references_drained(previous))
            {
                free_forwarded_table(previous);
                previous = nullptr;
                break;
            }
            wait_one_tick();
        }
        if (previous != nullptr)
        {
            push_retired(previous);
        }
    }

    drain_retired();
}

NTSTATUS register_layer(layer_state& layer,
                        HANDLE engine_handle,
                        const GUID& callout_key,
                        const GUID& filter_key,
                        const GUID& layer_key,
                        const wchar_t* callout_name,
                        const wchar_t* filter_name) noexcept
{
    FWPS_CALLOUT1 callout{};
    callout.calloutKey = callout_key;
    callout.flags = 0;
    callout.classifyFn = probe_forwarded_classify_fn;
    callout.notifyFn = probe_forwarded_notify_fn;
    callout.flowDeleteFn = nullptr;
    NTSTATUS status = FwpsCalloutRegister1(probe_state_ref().device_object, &callout, &layer.callout_id);
    if (!NT_SUCCESS(status))
    {
        return status;
    }
    layer.callout_registered = true;

    FWPM_CALLOUT0 definition{};
    definition.calloutKey = callout_key;
    definition.displayData.name = const_cast<wchar_t*>(callout_name);
    definition.displayData.description = const_cast<wchar_t*>(forwarded_callout_description);
    definition.providerKey = const_cast<GUID*>(&provider_key);
    definition.applicableLayer = layer_key;
    status = FwpmCalloutAdd0(engine_handle, &definition, nullptr, nullptr);
    if (!NT_SUCCESS(status))
    {
        return status;
    }
    layer.callout_added = true;

    // INSPECTION 只能观察、不能改动作；block 必须由 TERMINATING callout 给出。
    // 子层用 FWPM_SUBLAYER_UNIVERSAL。MAC_FRAME_NATIVE 层当前没有 Windows 防火墙 filter（防火墙在
    // ALE 层，§7.9 的实测只适用于 ALE），这里的终止型 PERMIT 不会碰到防火墙；它对同层里权重更低的
    // 第三方 L2 filter 有优先权——多驱动共存（WinpkFilter 等）需要 B4b 实测，不能当成已验证。
    FWPM_FILTER0 filter{};
    filter.filterKey = filter_key;
    filter.displayData.name = const_cast<wchar_t*>(filter_name);
    filter.layerKey = layer_key;
    filter.subLayerKey = FWPM_SUBLAYER_UNIVERSAL;
    filter.providerKey = const_cast<GUID*>(&provider_key);
    filter.weight.type = FWP_EMPTY;
    filter.action.type = FWP_ACTION_CALLOUT_TERMINATING;
    filter.action.calloutKey = callout_key;
    status = FwpmFilterAdd0(engine_handle, &filter, nullptr, &layer.filter_id);
    if (!NT_SUCCESS(status))
    {
        return status;
    }
    layer.filter_added = true;
    return STATUS_SUCCESS;
}

} // namespace

forwarded_table* allocate_forwarded_table(const std::uint8_t* data, std::uint32_t size, NTSTATUS& status) noexcept
{
    status = STATUS_INVALID_PARAMETER;
    if (data == nullptr || size == 0 || size > rule_blob_max_size)
    {
        return nullptr;
    }

    auto* table = static_cast<forwarded_table*>(
        ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(forwarded_table) + size, rule_pool_tag));
    if (table == nullptr)
    {
        status = STATUS_INSUFFICIENT_RESOURCES;
        return nullptr;
    }

    RtlZeroMemory(table, sizeof(forwarded_table));
    table->data = reinterpret_cast<std::uint8_t*>(table) + sizeof(forwarded_table);
    table->data_size = size;
    RtlCopyMemory(table->data, data, size);

    if (!try_open_forwarded_blob(table->data, size, table->view))
    {
        ExFreePoolWithTag(table, rule_pool_tag);
        return nullptr;
    }

    // B4b：redirect 规则由 classify 的数据面执行；这里不再整块拒绝。
    status = STATUS_SUCCESS;
    return table;
}

void free_forwarded_table(forwarded_table* table) noexcept
{
    if (table != nullptr)
    {
        ExFreePoolWithTag(table, rule_pool_tag);
    }
}

void probe_forwarded_init() noexcept
{
    KeInitializeSpinLock(&g_forwarded_state.lock);
    g_forwarded_state.stats.abi_version = probe_abi_version;
}

NTSTATUS probe_forwarded_start(HANDLE engine_handle) noexcept
{
    forwarded_state& state = g_forwarded_state;
    if (engine_handle == nullptr)
    {
        return STATUS_INVALID_PARAMETER;
    }

    NTSTATUS status = FwpsInjectionHandleCreate0(AF_UNSPEC, FWPS_INJECTION_TYPE_L2, &state.injection_handle);
    if (!NT_SUCCESS(status))
    {
        state.injection_handle = nullptr;
        return status;
    }

    NET_BUFFER_LIST_POOL_PARAMETERS pool_parameters{};
    pool_parameters.Header.Type = NDIS_OBJECT_TYPE_DEFAULT;
    pool_parameters.Header.Revision = NET_BUFFER_LIST_POOL_PARAMETERS_REVISION_1;
    pool_parameters.Header.Size = NDIS_SIZEOF_NET_BUFFER_LIST_POOL_PARAMETERS_REVISION_1;
    pool_parameters.fAllocateNetBuffer = TRUE;
    pool_parameters.DataSize = 0;
    pool_parameters.PoolTag = injection_pool_tag;
    state.nbl_pool = NdisAllocateNetBufferListPool(nullptr, &pool_parameters);
    if (state.nbl_pool == nullptr)
    {
        probe_forwarded_stop(engine_handle);
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    if (!forwarded_redirect_init(state.redirects))
    {
        probe_forwarded_stop(engine_handle);
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    status = register_layer(state.inbound,
                            engine_handle,
                            forwarded_callout_key_inbound,
                            forwarded_filter_key_inbound,
                            FWPM_LAYER_INBOUND_MAC_FRAME_NATIVE,
                            forwarded_callout_name_inbound,
                            forwarded_filter_name_inbound);
    if (NT_SUCCESS(status))
    {
        status = register_layer(state.outbound,
                                engine_handle,
                                forwarded_callout_key_outbound,
                                forwarded_filter_key_outbound,
                                FWPM_LAYER_OUTBOUND_MAC_FRAME_NATIVE,
                                forwarded_callout_name_outbound,
                                forwarded_filter_name_outbound);
    }
    if (!NT_SUCCESS(status))
    {
        probe_forwarded_stop(engine_handle);
        return status;
    }
    return STATUS_SUCCESS;
}

void probe_forwarded_stop(HANDLE engine_handle) noexcept
{
    forwarded_state& state = g_forwarded_state;

    // 注册逆序：出向先拆，再拆入向。拆除例程与 host 共用（同一套 BUSY/IN_USE 重试）。
    if (engine_handle != nullptr)
    {
        unregister_callout_layer(state.outbound, forwarded_callout_key_outbound);
        unregister_callout_layer(state.inbound, forwarded_callout_key_inbound);
    }

    if (state.injection_handle != nullptr)
    {
        FwpsInjectionHandleDestroy0(state.injection_handle);
        state.injection_handle = nullptr;
    }
    // FwpsInjectionHandleDestroy0 按文档会等所有 outstanding 注入回调；这里再显式等一次，
    // 保证 NBL 池在最后一个完成例程释放 NBL 之后才被销毁。
    for (std::uint32_t attempt = 0;
         attempt < retire_wait_rounds && InterlockedCompareExchange64(&state.outstanding_injections, 0, 0) != 0;
         ++attempt)
    {
        wait_one_tick();
    }

    if (state.nbl_pool != nullptr)
    {
        NdisFreeNetBufferListPool(state.nbl_pool);
        state.nbl_pool = nullptr;
    }

    forwarded_redirect_free(state.redirects);
    RtlZeroMemory(&state.config, sizeof(state.config));
}

void probe_forwarded_release_all() noexcept
{
    forwarded_state& state = g_forwarded_state;

    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    forwarded_table* current = state.table;
    forwarded_table* retired = state.retired_tables;
    state.table = nullptr;
    state.retired_tables = nullptr;
    forwarded_unlock(raised, old_irql);

    while (retired != nullptr)
    {
        forwarded_table* next = retired->retired_next;
        free_forwarded_table(retired);
        retired = next;
    }
    free_forwarded_table(current);
}

NTSTATUS probe_forwarded_apply_blob(const void* buffer, ULONG input_length) noexcept
{
    forwarded_state& state = g_forwarded_state;
    if (buffer == nullptr || input_length == 0 || input_length > rule_blob_max_size)
    {
        probe_forwarded_stats delta{};
        delta.rejected_blobs = 1;
        publish_forwarded_counters(state, delta);
        return STATUS_INVALID_BUFFER_SIZE;
    }

    NTSTATUS status = STATUS_SUCCESS;
    forwarded_table* table = allocate_forwarded_table(static_cast<const std::uint8_t*>(buffer), input_length, status);
    if (table == nullptr)
    {
        probe_forwarded_stats delta{};
        delta.rejected_blobs = 1;
        publish_forwarded_counters(state, delta);
        state.stats.last_status = static_cast<std::uint32_t>(status);
        return status;
    }

    // 换表与 host 的规则替换共用同一把互斥量：两边的退役等待都发生在 PASSIVE，串行化后
    // 不会出现两个线程同时替换 / 各自等待旧表的情况。forwarded 表本身与 host 表无关。
    KeWaitForSingleObject(&probe_state_ref().rule_mutex, Executive, KernelMode, FALSE, nullptr);
    replace_table(table);
    probe_forwarded_stats replaced{};
    replaced.tables_replaced = 1;
    publish_forwarded_counters(state, replaced);
    KeReleaseMutex(&probe_state_ref().rule_mutex, FALSE);
    return STATUS_SUCCESS;
}

NTSTATUS probe_forwarded_apply_config(const void* buffer, ULONG input_length) noexcept
{
    forwarded_state& state = g_forwarded_state;
    if (buffer == nullptr || input_length < sizeof(probe_forwarded_config))
    {
        return STATUS_INVALID_PARAMETER;
    }

    probe_forwarded_config candidate{};
    RtlCopyMemory(&candidate, buffer, sizeof(candidate));
    if (candidate.reserved0 != 0 || candidate.reserved1 != 0)
    {
        return STATUS_INVALID_PARAMETER;
    }
    if (candidate.family != 0 && candidate.family != 4 && candidate.family != 6)
    {
        return STATUS_INVALID_PARAMETER;
    }

    const bool enabled = candidate.enabled != 0;
    if (enabled)
    {
        if (candidate.listener_port == 0 || (candidate.family != 4 && candidate.family != 6))
        {
            return STATUS_INVALID_PARAMETER;
        }
        const std::uint32_t address_size = candidate.family == 4 ? 4U : 16U;
        bool address_present = false;
        for (std::uint32_t index = 0; index < 16; ++index)
        {
            if (index < address_size)
            {
                address_present = address_present || candidate.listener_address[index] != 0;
            }
            else if (candidate.listener_address[index] != 0)
            {
                return STATUS_INVALID_PARAMETER;
            }
        }
        if (!address_present)
        {
            return STATUS_INVALID_PARAMETER;
        }
    }

    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    const probe_forwarded_config previous = state.config;
    const bool listener_changed = previous.listener_port != candidate.listener_port ||
                                  previous.family != candidate.family ||
                                  !RtlEqualMemory(previous.listener_address, candidate.listener_address, 16);
    state.config = candidate;
    if (!enabled || previous.enabled != candidate.enabled || listener_changed)
    {
        forwarded_redirect_clear(state.redirects);
    }
    state.stats.last_status = static_cast<std::uint32_t>(STATUS_SUCCESS);
    forwarded_unlock(raised, old_irql);
    return STATUS_SUCCESS;
}

NTSTATUS probe_forwarded_get_config(void* buffer, ULONG output_length) noexcept
{
    if (buffer == nullptr || output_length < sizeof(probe_forwarded_config))
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    forwarded_state& state = g_forwarded_state;
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    const probe_forwarded_config snapshot = state.config;
    forwarded_unlock(raised, old_irql);

    RtlCopyMemory(buffer, &snapshot, sizeof(snapshot));
    return STATUS_SUCCESS;
}

NTSTATUS
probe_forwarded_query_redirect(const void* buffer, ULONG input_length, void* output, ULONG output_length) noexcept
{
    if (buffer == nullptr || input_length < sizeof(probe_forwarded_redirect_query) || output == nullptr ||
        output_length < sizeof(probe_forwarded_redirect))
    {
        return STATUS_INVALID_PARAMETER;
    }

    probe_forwarded_redirect_query query{};
    RtlCopyMemory(&query, buffer, sizeof(query));
    const std::uint8_t protocol = query.protocol == 0 ? 6 : query.protocol;
    if (query.reserved0 != 0 || query.reserved1 != 0 || query.listener_port == 0 || query.client_port == 0 ||
        (query.family != 4 && query.family != 6) || (protocol != 6 && protocol != 17))
    {
        return STATUS_INVALID_PARAMETER;
    }

    forwarded_state& state = g_forwarded_state;
    forwarded_redirect_snapshot snapshot{};
    forwarded_redirect_lookup lookup = forwarded_redirect_lookup::missing;

    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    lookup = forwarded_redirect_find_return(state.redirects,
                                            query.family,
                                            protocol,
                                            query.listener_port,
                                            query.client_address,
                                            query.client_port,
                                            snapshot);
    if (lookup == forwarded_redirect_lookup::expired)
    {
        if (protocol == 17)
        {
            ++state.stats.udp_expired;
        }
        else
        {
            ++state.stats.redirect_expired;
        }
    }
    forwarded_unlock(raised, old_irql);

    if (lookup != forwarded_redirect_lookup::found)
    {
        return STATUS_NOT_FOUND;
    }

    probe_forwarded_redirect result{};
    result.magic = forwarded_redirect_magic;
    result.version = forwarded_redirect_version;
    result.family = snapshot.family;
    result.protocol = snapshot.protocol;
    result.client_port = snapshot.client_port;
    result.server_port = snapshot.server_port;
    result.listener_port = snapshot.listener_port;
    result.ingress_ifindex = snapshot.ingress_ifindex;
    result.upstream_id = snapshot.upstream_id;
    result.rule_index = snapshot.rule_index;
    result.state = snapshot.state;
    RtlCopyMemory(result.client_address, snapshot.client_address, 16);
    RtlCopyMemory(result.server_address, snapshot.server_address, 16);
    RtlCopyMemory(output, &result, sizeof(result));
    return STATUS_SUCCESS;
}

void probe_forwarded_info_snapshot(probe_forwarded_info& info) noexcept
{
    forwarded_state& state = g_forwarded_state;

    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    info = probe_forwarded_info{};
    info.abi_version = probe_abi_version;
    info.generation = state.generation;
    info.installed = state.table != nullptr ? 1U : 0U;
    if (state.table != nullptr)
    {
        info.rule_count = state.table->view.rule_count;
        info.fallback_action = static_cast<std::uint32_t>(state.table->view.fallback_action);
        info.total_size = state.table->view.size;
        info.network_pool_count = state.table->view.network_pool_count;
    }
    forwarded_unlock(raised, old_irql);
}

void probe_forwarded_stats_snapshot(probe_forwarded_stats& stats) noexcept
{
    forwarded_state& state = g_forwarded_state;
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    RtlCopyMemory(&stats, &state.stats, sizeof(stats));
    stats.redirect_active = state.redirects.active;
    if (state.redirects.high_water > stats.redirect_high_water)
    {
        stats.redirect_high_water = state.redirects.high_water;
    }
    forwarded_unlock(raised, old_irql);
}

void probe_forwarded_reset_stats() noexcept
{
    forwarded_state& state = g_forwarded_state;

    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    const std::uint32_t active = state.redirects.active;
    state.redirects.high_water = active;
    RtlZeroMemory(&state.stats, sizeof(state.stats));
    state.stats.abi_version = probe_abi_version;
    state.stats.redirect_active = active;
    state.stats.redirect_high_water = active;
    forwarded_unlock(raised, old_irql);
}

} // namespace winforward::wfp::probe
