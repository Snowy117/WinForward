#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

// 只挂 DATAGRAM_DATA_V4，做 inbound 回包观测；本版只计数，不改写。
constexpr GUID udp_callout_key{ 0x8e1c4a63, 0x2f7d, 0x4b18, { 0xa3, 0x71, 0x5d, 0x92, 0xc4, 0x08, 0xe6, 0x51 } };
constexpr GUID udp_filter_key{ 0x9f2d5b74, 0x3a8e, 0x5c29, { 0xb4, 0x82, 0x6e, 0xa3, 0xd5, 0x19, 0xf7, 0x62 } };
constexpr wchar_t udp_callout_name[] = L"WinForward UDP Probe Datagram Data V4";
constexpr wchar_t udp_callout_description[] = L"WinForward UDP route A probe (experimental)";
constexpr wchar_t udp_filter_name[] = L"WinForward UDP Probe Return Filter V4";

struct return_table_entry
{
    SOCKADDR_STORAGE local;
    SOCKADDR_STORAGE remote;
    bool valid;
};

KSPIN_LOCK g_udp_lock{};
bool g_udp_lock_initialized = false;
udp_probe_config g_udp_config{};
udp_probe_stats g_udp_stats{};
return_table_entry g_udp_table[udp_probe_table_capacity]{};
HANDLE g_udp_engine = nullptr;
UINT32 g_udp_callout_id = 0;
bool g_udp_callout_registered = false;
bool g_udp_callout_added = false;
UINT64 g_udp_filter_id = 0;
bool g_udp_filter_added = false;

[[nodiscard]] std::uint16_t field_u16(const FWPS_INCOMING_VALUES0* values, UINT16 index) noexcept
{
    if (values == nullptr || index >= values->valueCount)
    {
        return 0;
    }
    const FWP_VALUE0& value = values->incomingValue[index].value;
    return value.type == FWP_UINT16 ? value.uint16 : 0;
}

[[nodiscard]] std::uint32_t field_u32(const FWPS_INCOMING_VALUES0* values, UINT16 index) noexcept
{
    if (values == nullptr || index >= values->valueCount)
    {
        return 0;
    }
    const FWP_VALUE0& value = values->incomingValue[index].value;
    return value.type == FWP_UINT32 ? value.uint32 : 0;
}

[[nodiscard]] bool
local_matches(const SOCKADDR_STORAGE& entry_local, std::uint16_t port, std::uint32_t address) noexcept
{
    const auto* local = reinterpret_cast<const SOCKADDR*>(&entry_local);
    if (local->sa_family != AF_INET)
    {
        return false;
    }
    const auto* v4 = reinterpret_cast<const SOCKADDR_IN*>(local);
    if (RtlUshortByteSwap(v4->sin_port) != port)
    {
        return false;
    }
    if (v4->sin_addr.S_un.S_addr == 0)
    {
        return true; // 原始 local 是 ANY：按端口匹配。
    }
    return v4->sin_addr.S_un.S_addr == address;
}

void log_return_seen(std::uint16_t app_port, const SOCKADDR_STORAGE& remote) noexcept
{
    const auto* v4 = reinterpret_cast<const SOCKADDR_IN*>(&remote);
    DbgPrint("wfudp: return seen app_port=%u src=%u.%u.%u.%u:%u mode=%u\n",
             app_port,
             v4->sin_addr.S_un.S_un_b.s_b1,
             v4->sin_addr.S_un.S_un_b.s_b2,
             v4->sin_addr.S_un.S_un_b.s_b3,
             v4->sin_addr.S_un.S_un_b.s_b4,
             RtlUshortByteSwap(v4->sin_port),
             g_udp_config.return_mode);
}

} // namespace

void NTAPI udp_probe_classify_fn(const FWPS_INCOMING_VALUES0* in_fixed_values,
                                 const FWPS_INCOMING_METADATA_VALUES0* in_meta_values,
                                 void* layer_data,
                                 const void* classify_context,
                                 const FWPS_FILTER1* filter,
                                 UINT64 flow_context,
                                 FWPS_CLASSIFY_OUT0* classify_out) noexcept;

void udp_probe_start(HANDLE engine_handle) noexcept
{
    if (!g_udp_lock_initialized)
    {
        KeInitializeSpinLock(&g_udp_lock);
        g_udp_lock_initialized = true;
    }
    g_udp_engine = engine_handle;
    g_udp_stats.abi_version = udp_probe_config_version;

    FWPS_CALLOUT1 callout{};
    callout.calloutKey = udp_callout_key;
    callout.flags = 0;
    callout.classifyFn = udp_probe_classify_fn;
    callout.notifyFn = probe_notify_fn;
    callout.flowDeleteFn = nullptr;

    NTSTATUS status = FwpsCalloutRegister1(probe_state_ref().device_object, &callout, &g_udp_callout_id);
    if (!NT_SUCCESS(status))
    {
        g_udp_stats.last_status = static_cast<std::uint32_t>(status);
        DbgPrint("wfudp: callout register failed 0x%08X\n", status);
        return;
    }
    g_udp_callout_registered = true;

    FWPM_CALLOUT0 definition{};
    definition.calloutKey = udp_callout_key;
    definition.displayData.name = const_cast<wchar_t*>(udp_callout_name);
    definition.displayData.description = const_cast<wchar_t*>(udp_callout_description);
    definition.providerKey = const_cast<GUID*>(&provider_key);
    definition.applicableLayer = FWPM_LAYER_DATAGRAM_DATA_V4;
    status = FwpmCalloutAdd0(engine_handle, &definition, nullptr, nullptr);
    if (!NT_SUCCESS(status))
    {
        FwpsCalloutUnregisterById0(g_udp_callout_id);
        g_udp_callout_registered = false;
        g_udp_stats.last_status = static_cast<std::uint32_t>(status);
        DbgPrint("wfudp: callout add failed 0x%08X\n", status);
        return;
    }
    g_udp_callout_added = true;

    FWPM_FILTER0 filter{};
    filter.filterKey = udp_filter_key;
    filter.displayData.name = const_cast<wchar_t*>(udp_filter_name);
    filter.layerKey = FWPM_LAYER_DATAGRAM_DATA_V4;
    filter.subLayerKey = FWPM_SUBLAYER_UNIVERSAL;
    filter.providerKey = const_cast<GUID*>(&provider_key);
    filter.weight.type = FWP_EMPTY;
    filter.action.type = FWP_ACTION_CALLOUT_TERMINATING;
    filter.action.calloutKey = udp_callout_key;
    status = FwpmFilterAdd0(engine_handle, &filter, nullptr, &g_udp_filter_id);
    if (!NT_SUCCESS(status))
    {
        FwpmCalloutDeleteByKey0(engine_handle, &udp_callout_key);
        g_udp_callout_added = false;
        FwpsCalloutUnregisterById0(g_udp_callout_id);
        g_udp_callout_registered = false;
        g_udp_stats.last_status = static_cast<std::uint32_t>(status);
        DbgPrint("wfudp: filter add failed 0x%08X\n", status);
        return;
    }
    g_udp_filter_added = true;
    DbgPrint("wfudp: probe callout ready\n");
    udp_ippacket_start(engine_handle);
    udp_inject_start();
}

void udp_probe_stop() noexcept
{
    udp_ippacket_stop();
    udp_inject_stop();
    if (g_udp_engine != nullptr && g_udp_filter_added)
    {
        FwpmFilterDeleteById0(g_udp_engine, g_udp_filter_id);
        g_udp_filter_added = false;
    }
    if (g_udp_engine != nullptr && g_udp_callout_added)
    {
        FwpmCalloutDeleteByKey0(g_udp_engine, &udp_callout_key);
        g_udp_callout_added = false;
    }
    if (g_udp_callout_registered)
    {
        FwpsCalloutUnregisterById0(g_udp_callout_id);
        g_udp_callout_registered = false;
    }
    g_udp_engine = nullptr;
}

void udp_probe_apply_config(const udp_probe_config& config) noexcept
{
    KIRQL old_irql{};
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    g_udp_config = config;
    if (config.enabled == 0)
    {
        RtlZeroMemory(g_udp_table, sizeof(g_udp_table));
    }
    KeReleaseSpinLock(&g_udp_lock, old_irql);
}

void udp_probe_snapshot(udp_probe_stats& stats) noexcept
{
    KIRQL old_irql{};
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    stats = g_udp_stats;
    std::uint32_t entries = 0;
    for (std::size_t index = 0; index < udp_probe_table_capacity; ++index)
    {
        if (g_udp_table[index].valid)
        {
            ++entries;
        }
    }
    stats.table_entries = entries;
    KeReleaseSpinLock(&g_udp_lock, old_irql);
}

bool udp_probe_enabled() noexcept
{
    KIRQL old_irql{};
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    const bool enabled = g_udp_config.enabled != 0;
    KeReleaseSpinLock(&g_udp_lock, old_irql);
    return enabled;
}

std::uint32_t udp_probe_listener_pid() noexcept
{
    KIRQL old_irql{};
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    const std::uint32_t pid = g_udp_config.listener_pid;
    KeReleaseSpinLock(&g_udp_lock, old_irql);
    return pid;
}

std::uint16_t udp_probe_listener_port() noexcept
{
    KIRQL old_irql{};
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    const std::uint16_t port = g_udp_config.listener_port;
    KeReleaseSpinLock(&g_udp_lock, old_irql);
    return port;
}

std::uint16_t udp_probe_remote_port_filter() noexcept
{
    KIRQL old_irql{};
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    const std::uint16_t port = g_udp_config.remote_port_filter;
    KeReleaseSpinLock(&g_udp_lock, old_irql);
    return port;
}

std::uint8_t udp_probe_redirect_target() noexcept
{
    KIRQL old_irql{};
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    const std::uint8_t target = g_udp_config.redirect_target;
    KeReleaseSpinLock(&g_udp_lock, old_irql);
    return target;
}

bool udp_probe_target_address(std::uint8_t* address) noexcept
{
    KIRQL old_irql{};
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    bool non_zero = false;
    for (std::size_t index = 0; index < 16; ++index)
    {
        address[index] = g_udp_config.target_address[index];
        if (address[index] != 0)
        {
            non_zero = true;
        }
    }
    KeReleaseSpinLock(&g_udp_lock, old_irql);
    return non_zero;
}

bool udp_probe_map_has_room() noexcept
{
    KIRQL old_irql{};
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    bool room = false;
    for (std::size_t index = 0; index < udp_probe_table_capacity; ++index)
    {
        if (!g_udp_table[index].valid)
        {
            room = true;
            break;
        }
    }
    KeReleaseSpinLock(&g_udp_lock, old_irql);
    return room;
}

bool udp_probe_map_insert(const SOCKADDR_STORAGE& local, const SOCKADDR_STORAGE& remote) noexcept
{
    KIRQL old_irql{};
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    bool inserted = false;
    for (std::size_t index = 0; index < udp_probe_table_capacity; ++index)
    {
        if (g_udp_table[index].valid)
        {
            continue;
        }
        g_udp_table[index].local = local;
        g_udp_table[index].remote = remote;
        g_udp_table[index].valid = true;
        inserted = true;
        break;
    }
    KeReleaseSpinLock(&g_udp_lock, old_irql);
    return inserted;
}

bool udp_probe_map_lookup(std::uint16_t port, std::uint32_t address) noexcept
{
    KIRQL old_irql{};
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    bool matched = false;
    for (std::size_t index = 0; index < udp_probe_table_capacity; ++index)
    {
        if (!g_udp_table[index].valid)
        {
            continue;
        }
        if (!local_matches(g_udp_table[index].local, port, address))
        {
            continue;
        }
        matched = true;
        break;
    }
    KeReleaseSpinLock(&g_udp_lock, old_irql);
    return matched;
}

void udp_probe_note_seen() noexcept
{
    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&g_udp_stats.seen));
}

void udp_probe_note_redirect(bool success) noexcept
{
    if (success)
    {
        InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&g_udp_stats.redirected));
    }
    else
    {
        InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&g_udp_stats.redirect_failures));
    }
}

void udp_probe_note_ippacket(bool matched) noexcept
{
    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&g_udp_stats.ippacket_seen));
    if (matched)
    {
        InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&g_udp_stats.ippacket_matched));
    }
}

void udp_probe_note_return_status(std::uint32_t status) noexcept
{
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&g_udp_stats.last_status), static_cast<LONG>(status));
}

void NTAPI udp_probe_classify_fn(const FWPS_INCOMING_VALUES0* in_fixed_values,
                                 const FWPS_INCOMING_METADATA_VALUES0* in_meta_values,
                                 void* layer_data,
                                 const void* classify_context,
                                 const FWPS_FILTER1* filter,
                                 UINT64 flow_context,
                                 FWPS_CLASSIFY_OUT0* classify_out) noexcept
{
    UNREFERENCED_PARAMETER(in_meta_values);
    UNREFERENCED_PARAMETER(layer_data);
    UNREFERENCED_PARAMETER(classify_context);
    UNREFERENCED_PARAMETER(filter);
    UNREFERENCED_PARAMETER(flow_context);

    if (classify_out == nullptr)
    {
        return;
    }
    // 观测层只放行；动作不留给后续 callout，与 TCP 路径的终态策略一致。
    classify_out->actionType = FWP_ACTION_PERMIT;
    classify_out->rights &= ~FWPS_RIGHT_ACTION_WRITE;

    if (in_fixed_values == nullptr || in_fixed_values->layerId != FWPS_LAYER_DATAGRAM_DATA_V4)
    {
        return;
    }

    udp_probe_config config{};
    KIRQL old_irql{};
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    config = g_udp_config;
    KeReleaseSpinLock(&g_udp_lock, old_irql);

    if (config.enabled == 0 || config.return_mode == 0)
    {
        return;
    }
    const std::uint32_t direction = field_u32(in_fixed_values, FWPS_FIELD_DATAGRAM_DATA_V4_DIRECTION);
    std::uint16_t app_port = 0;
    std::uint32_t app_address = 0;
    if (config.return_layer == 0)
    {
        if (direction != FWP_DIRECTION_INBOUND)
        {
            return;
        }
        if (field_u16(in_fixed_values, FWPS_FIELD_DATAGRAM_DATA_V4_IP_REMOTE_PORT) != config.listener_port)
        {
            return;
        }
        app_port = field_u16(in_fixed_values, FWPS_FIELD_DATAGRAM_DATA_V4_IP_LOCAL_PORT);
        app_address = field_u32(in_fixed_values, FWPS_FIELD_DATAGRAM_DATA_V4_IP_LOCAL_ADDRESS);
    }
    else
    {
        if (direction != FWP_DIRECTION_OUTBOUND)
        {
            return;
        }
        if (field_u16(in_fixed_values, FWPS_FIELD_DATAGRAM_DATA_V4_IP_LOCAL_PORT) != config.listener_port)
        {
            return;
        }
        app_port = field_u16(in_fixed_values, FWPS_FIELD_DATAGRAM_DATA_V4_IP_REMOTE_PORT);
        app_address = field_u32(in_fixed_values, FWPS_FIELD_DATAGRAM_DATA_V4_IP_REMOTE_ADDRESS);
    }
    SOCKADDR_STORAGE remote{};
    bool matched = false;
    KeAcquireSpinLock(&g_udp_lock, &old_irql);
    for (std::size_t index = 0; index < udp_probe_table_capacity; ++index)
    {
        if (!g_udp_table[index].valid)
        {
            continue;
        }
        if (!local_matches(g_udp_table[index].local, app_port, app_address))
        {
            continue;
        }
        remote = g_udp_table[index].remote;
        matched = true;
        break;
    }
    KeReleaseSpinLock(&g_udp_lock, old_irql);

    if (!matched)
    {
        return;
    }

    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&g_udp_stats.return_seen));
    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&g_udp_stats.return_matched));
    log_return_seen(app_port, remote);

    if (config.return_mode == 2 || config.return_mode == 3)
    {
        InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&g_udp_stats.return_failures));
        InterlockedExchange(reinterpret_cast<volatile LONG*>(&g_udp_stats.last_status),
                            static_cast<LONG>(STATUS_NOT_IMPLEMENTED));
    }
}

} // namespace winforward::wfp::probe
