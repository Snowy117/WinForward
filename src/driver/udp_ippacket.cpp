#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

constexpr GUID ippacket_callout_key{ 0x1a3e5c71, 0x4b92, 0x6d38, { 0xc5, 0x91, 0x7f, 0xb4, 0xe6, 0x2a, 0x08, 0x73 } };
constexpr GUID ippacket_filter_key{ 0x2b4f6d82, 0x5ca3, 0x7e49, { 0xd6, 0xa2, 0x80, 0xc5, 0xf7, 0x3b, 0x19, 0x84 } };
constexpr wchar_t ippacket_callout_name[] = L"WinForward UDP Probe Inbound IPPacket V4";
constexpr wchar_t ippacket_callout_description[] = L"WinForward UDP route A probe (experimental)";
constexpr wchar_t ippacket_filter_name[] = L"WinForward UDP Probe Inbound IPPacket Filter V4";

UINT32 g_ippacket_callout_id = 0;
bool g_ippacket_callout_registered = false;
bool g_ippacket_callout_added = false;
UINT64 g_ippacket_filter_id = 0;
bool g_ippacket_filter_added = false;

[[nodiscard]] std::uint32_t field_u32(const FWPS_INCOMING_VALUES0* values, UINT16 index) noexcept
{
    if (values == nullptr || index >= values->valueCount)
    {
        return 0;
    }
    const FWP_VALUE0& value = values->incomingValue[index].value;
    return value.type == FWP_UINT32 ? value.uint32 : 0;
}

} // namespace

void NTAPI udp_ippacket_classify_fn(const FWPS_INCOMING_VALUES0* in_fixed_values,
                                    const FWPS_INCOMING_METADATA_VALUES0* in_meta_values,
                                    void* layer_data,
                                    const void* classify_context,
                                    const FWPS_FILTER1* filter,
                                    UINT64 flow_context,
                                    FWPS_CLASSIFY_OUT0* classify_out) noexcept
{
    UNREFERENCED_PARAMETER(in_meta_values);
    UNREFERENCED_PARAMETER(classify_context);
    UNREFERENCED_PARAMETER(filter);
    UNREFERENCED_PARAMETER(flow_context);

    if (classify_out == nullptr)
    {
        return;
    }
    classify_out->actionType = FWP_ACTION_PERMIT;
    classify_out->rights &= ~FWPS_RIGHT_ACTION_WRITE;

    if (in_fixed_values == nullptr || in_fixed_values->layerId != FWPS_LAYER_INBOUND_IPPACKET_V4 ||
        layer_data == nullptr)
    {
        return;
    }

    auto* net_buffer_list = static_cast<NET_BUFFER_LIST*>(layer_data);
    NET_BUFFER* net_buffer = NET_BUFFER_LIST_FIRST_NB(net_buffer_list);
    if (net_buffer == nullptr)
    {
        return;
    }

    const std::uint32_t length = NET_BUFFER_DATA_LENGTH(net_buffer);
    if (length < 28)
    {
        return;
    }

    const auto* data = static_cast<const std::uint8_t*>(NdisGetDataBuffer(net_buffer, 28, nullptr, 1, 0));
    if (data == nullptr)
    {
        return;
    }

    const std::uint8_t version_ihl = data[0];
    if ((version_ihl >> 4) != 4)
    {
        return;
    }
    const std::uint8_t header_length = static_cast<std::uint8_t>((version_ihl & 0x0F) * 4);
    if (header_length < 20 || length < static_cast<std::uint32_t>(header_length) + 8 || data[9] != IPPROTO_UDP)
    {
        return;
    }

    const std::uint16_t source_port = RtlUshortByteSwap(*reinterpret_cast<const std::uint16_t*>(data + header_length));
    const std::uint16_t destination_port =
        RtlUshortByteSwap(*reinterpret_cast<const std::uint16_t*>(data + header_length + 2));
    if (source_port != udp_probe_listener_port())
    {
        return;
    }

    const std::uint32_t local_address = field_u32(in_fixed_values, FWPS_FIELD_INBOUND_IPPACKET_V4_IP_LOCAL_ADDRESS);
    const std::uint32_t remote_address = field_u32(in_fixed_values, FWPS_FIELD_INBOUND_IPPACKET_V4_IP_REMOTE_ADDRESS);
    const bool matched = udp_probe_map_lookup(destination_port, local_address);
    udp_probe_note_ippacket(matched);

    const auto* local_bytes = reinterpret_cast<const std::uint8_t*>(&local_address);
    const auto* remote_bytes = reinterpret_cast<const std::uint8_t*>(&remote_address);
    DbgPrint("wfudp: ippacket src=%u.%u.%u.%u:%u dst=%u.%u.%u.%u:%u matched=%u\n",
             remote_bytes[0],
             remote_bytes[1],
             remote_bytes[2],
             remote_bytes[3],
             source_port,
             local_bytes[0],
             local_bytes[1],
             local_bytes[2],
             local_bytes[3],
             destination_port,
             matched ? 1U : 0U);
}

void udp_ippacket_start(HANDLE engine_handle) noexcept
{
    FWPS_CALLOUT1 callout{};
    callout.calloutKey = ippacket_callout_key;
    callout.flags = 0;
    callout.classifyFn = udp_ippacket_classify_fn;
    callout.notifyFn = probe_notify_fn;
    callout.flowDeleteFn = nullptr;

    NTSTATUS status = FwpsCalloutRegister1(probe_state_ref().device_object, &callout, &g_ippacket_callout_id);
    if (!NT_SUCCESS(status))
    {
        DbgPrint("wfudp: ippacket callout register failed 0x%08X\n", status);
        return;
    }
    g_ippacket_callout_registered = true;

    FWPM_CALLOUT0 definition{};
    definition.calloutKey = ippacket_callout_key;
    definition.displayData.name = const_cast<wchar_t*>(ippacket_callout_name);
    definition.displayData.description = const_cast<wchar_t*>(ippacket_callout_description);
    definition.providerKey = const_cast<GUID*>(&provider_key);
    definition.applicableLayer = FWPM_LAYER_INBOUND_IPPACKET_V4;
    status = FwpmCalloutAdd0(engine_handle, &definition, nullptr, nullptr);
    if (!NT_SUCCESS(status))
    {
        FwpsCalloutUnregisterById0(g_ippacket_callout_id);
        g_ippacket_callout_registered = false;
        DbgPrint("wfudp: ippacket callout add failed 0x%08X\n", status);
        return;
    }
    g_ippacket_callout_added = true;

    FWPM_FILTER0 filter{};
    filter.filterKey = ippacket_filter_key;
    filter.displayData.name = const_cast<wchar_t*>(ippacket_filter_name);
    filter.layerKey = FWPM_LAYER_INBOUND_IPPACKET_V4;
    filter.subLayerKey = FWPM_SUBLAYER_UNIVERSAL;
    filter.providerKey = const_cast<GUID*>(&provider_key);
    filter.weight.type = FWP_EMPTY;
    filter.action.type = FWP_ACTION_CALLOUT_TERMINATING;
    filter.action.calloutKey = ippacket_callout_key;
    status = FwpmFilterAdd0(engine_handle, &filter, nullptr, &g_ippacket_filter_id);
    if (!NT_SUCCESS(status))
    {
        FwpmCalloutDeleteByKey0(engine_handle, &ippacket_callout_key);
        g_ippacket_callout_added = false;
        FwpsCalloutUnregisterById0(g_ippacket_callout_id);
        g_ippacket_callout_registered = false;
        DbgPrint("wfudp: ippacket filter add failed 0x%08X\n", status);
        return;
    }
    g_ippacket_filter_added = true;
    DbgPrint("wfudp: ippacket probe ready\n");
}

void udp_ippacket_stop() noexcept
{
    if (g_ippacket_filter_added)
    {
        FwpmFilterDeleteById0(probe_state_ref().engine_handle, g_ippacket_filter_id);
        g_ippacket_filter_added = false;
    }
    if (g_ippacket_callout_added)
    {
        FwpmCalloutDeleteByKey0(probe_state_ref().engine_handle, &ippacket_callout_key);
        g_ippacket_callout_added = false;
    }
    if (g_ippacket_callout_registered)
    {
        FwpsCalloutUnregisterById0(g_ippacket_callout_id);
        g_ippacket_callout_registered = false;
    }
}

} // namespace winforward::wfp::probe
