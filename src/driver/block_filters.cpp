#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

// FWPM_CONDITION_FLAGS 上“连接被 redirect 改道过”的标记。WDK 26100 的 fwpmu.h 里
// FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED = 0x00100000；条件用 FWP_UINT32 +
// FWP_MATCH_FLAGS_ALL_SET，所以只命中被我们改道到 sink 的连接，不会误伤该端口上的正常流量。
inline constexpr std::uint32_t connection_redirected_flag = 0x00100000;

// 一条 filter 的三个条件（同一 filter 内 AND）。顺序固定为 协议 → 远端端口 → redirect 标记。
inline constexpr std::uint32_t block_filter_condition_count = 3;
inline constexpr std::uint32_t block_filter_layer_count = 2;
inline constexpr std::uint32_t block_filter_protocol_count = 2;
inline constexpr std::uint8_t block_filter_protocols[block_filter_protocol_count] = { rule_protocol_tcp,
                                                                                      rule_protocol_udp };

static_assert(block_filter_layer_count * block_filter_protocol_count == block_sink_filter_count,
              "固定 block filter 必须正好覆盖 V4/V6 × TCP/UDP");

struct block_filter_conditions
{
    FWPM_FILTER_CONDITION0 entries[block_filter_condition_count];
    UINT32 count;
};

void append_protocol_condition(block_filter_conditions& out, std::uint8_t protocol) noexcept
{
    FWPM_FILTER_CONDITION0& condition = out.entries[out.count++];
    condition.fieldKey = FWPM_CONDITION_IP_PROTOCOL;
    condition.matchType = FWP_MATCH_EQUAL;
    condition.conditionValue.type = FWP_UINT8;
    condition.conditionValue.uint8 = protocol;
}

void append_sink_port_condition(block_filter_conditions& out, std::uint16_t sink_port) noexcept
{
    FWPM_FILTER_CONDITION0& condition = out.entries[out.count++];
    condition.fieldKey = FWPM_CONDITION_IP_REMOTE_PORT;
    condition.matchType = FWP_MATCH_EQUAL;
    condition.conditionValue.type = FWP_UINT16;
    condition.conditionValue.uint16 = sink_port;
}

void append_redirected_condition(block_filter_conditions& out) noexcept
{
    FWPM_FILTER_CONDITION0& condition = out.entries[out.count++];
    condition.fieldKey = FWPM_CONDITION_FLAGS;
    condition.matchType = FWP_MATCH_FLAGS_ALL_SET;
    condition.conditionValue.type = FWP_UINT32;
    condition.conditionValue.uint32 = connection_redirected_flag;
}

void build_block_conditions(std::uint8_t protocol, std::uint16_t sink_port, block_filter_conditions& out) noexcept
{
    RtlZeroMemory(&out, sizeof(out));
    append_protocol_condition(out, protocol);
    append_sink_port_condition(out, sink_port);
    append_redirected_condition(out);
}

[[nodiscard]] const GUID& block_filter_layer(std::uint32_t layer_index) noexcept
{
    return layer_index == 0 ? FWPM_LAYER_ALE_AUTH_CONNECT_V4 : FWPM_LAYER_ALE_AUTH_CONNECT_V6;
}

NTSTATUS add_block_filter(const GUID& layer_key,
                          const FWPM_FILTER_CONDITION0* conditions,
                          UINT32 condition_count,
                          UINT64& filter_id) noexcept
{
    FWPM_FILTER0 filter{};
    filter.filterKey = block_filter_key_template;
    filter.filterKey.Data1 += probe_state_ref().block_filter_serial++;
    filter.displayData.name = const_cast<wchar_t*>(block_filter_name);
    filter.layerKey = layer_key;
    filter.subLayerKey = FWPM_SUBLAYER_UNIVERSAL;
    filter.providerKey = const_cast<GUID*>(&provider_key);

    // 显式权重。跨子层由 FWPM_SUBLAYER_UNIVERSAL 决定；这个 FWP_ACTION_BLOCK filter 只会
    // 增加拒绝、不会放行，所以即使排在防火墙之前也不会让它拦不住的流量通过。
    filter.weight.type = FWP_UINT64;
    filter.weight.uint64 = const_cast<UINT64*>(&auth_filter_weight);

    filter.action.type = FWP_ACTION_BLOCK;
    filter.numFilterConditions = condition_count;
    filter.filterCondition = const_cast<FWPM_FILTER_CONDITION0*>(conditions);
    return FwpmFilterAdd0(probe_state_ref().engine_handle, &filter, nullptr, &filter_id);
}

} // namespace

NTSTATUS probe_install_block_filters(std::uint16_t sink_port, UINT64* ids, std::uint32_t& count) noexcept
{
    count = 0;
    if (ids == nullptr || sink_port == 0)
    {
        return STATUS_INVALID_PARAMETER;
    }

    // <mutation id="block-filter-skip">
    for (std::uint32_t layer_index = 0; layer_index < block_filter_layer_count; ++layer_index)
    // </mutation>
    {
        for (std::uint32_t protocol_index = 0; protocol_index < block_filter_protocol_count; ++protocol_index)
        {
            // <mutation id="block-filter-protocol">
            const std::uint8_t protocol = block_filter_protocols[protocol_index];
            // </mutation>
            block_filter_conditions conditions{};
            build_block_conditions(protocol, sink_port, conditions);
            const NTSTATUS status =
                add_block_filter(block_filter_layer(layer_index), conditions.entries, conditions.count, ids[count]);
            if (!NT_SUCCESS(status))
            {
                probe_delete_block_filters(ids, count);
                return status;
            }
            ++count;
        }
    }
    return STATUS_SUCCESS;
}

void probe_delete_block_filters(UINT64* ids, std::uint32_t& count) noexcept
{
    for (std::uint32_t index = 0; index < count; ++index)
    {
        FwpmFilterDeleteById0(probe_state_ref().engine_handle, ids[index]);
        ids[index] = 0;
    }
    count = 0;
}

} // namespace winforward::wfp::probe
