#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

struct block_conditions
{
    FWPM_FILTER_CONDITION0 entries[4];
    FWP_RANGE0 port_range;
    FWP_V4_ADDR_AND_MASK v4_mask;
    FWP_V6_ADDR_AND_MASK v6_mask;
    FWP_BYTE_BLOB app_id;
    std::uint8_t app_id_storage[max_process_path_length * 2 + 2];
    UINT32 count;
};

// 规则里的进程串是 UTF-8（ASCII 已折叠小写，非 ASCII 原样）；FWP 条件要的是 ALE_APP_ID 的
// 原始字节（UTF-16LE + NUL）。这条路径在 PASSIVE_LEVEL 的 IOCTL 里，可以用 RtlUTF8ToUnicodeN；
// 非法 UTF-8 或超长时返回 false，调用方拒绝整块 blob（绝不退化成“无条件 block”）。
[[nodiscard]] bool fill_block_app_id(const char* selector, std::uint8_t* storage, UINT32& byte_count) noexcept
{
    byte_count = 0;
    ULONG selector_length = 0;
    while (selector[selector_length] != '\0')
    {
        ++selector_length;
    }
    if (selector_length == 0)
    {
        return false;
    }

    ULONG written = 0;
    // 契约：选择器 UTF-8 ≤511 字节（不含 NUL）。UTF-8 字节数 ≥ UTF-16 码元数，所以
    // 511 个码元 × 2 字节 = 1022 是这条规则的上界；超出的选择器拒绝整块 blob。
    const ULONG capacity = static_cast<ULONG>((max_process_path_length - 1) * 2);
    NTSTATUS status =
        RtlUTF8ToUnicodeN(reinterpret_cast<PWSTR>(storage), capacity, &written, selector, selector_length);
    if (!NT_SUCCESS(status) || written == 0)
    {
        return false;
    }
    storage[written] = 0;
    storage[written + 1] = 0;
    byte_count = written + 2;
    return true;
}

// 每层只展开该族的网段：声明的族掩码不含该层、或网段池里没有该族，都跳过该层。
[[nodiscard]] bool rule_applies_to_layer(const blob_view& view, const rule& selected, bool ipv6_layer) noexcept
{
    const std::uint8_t family_mask_bit = ipv6_layer ? rule_family_mask_ipv6 : rule_family_mask_ipv4;
    const std::uint8_t family = ipv6_layer ? rule_family_ipv6 : rule_family_ipv4;
    if (selected.family_mask != rule_family_mask_any && (selected.family_mask & family_mask_bit) == 0)
    {
        return false;
    }
    if (selected.network_count == 0)
    {
        return true;
    }
    for (std::uint32_t index = 0; index < selected.network_count; ++index)
    {
        if (view.network_pool[selected.network_first + index].family == family)
        {
            return true;
        }
    }
    return false;
}

// 该层要展开的网段份数；没有网段（任意）时算一份，由 build_block_conditions 省略地址条件。
[[nodiscard]] std::uint32_t
layer_network_count(const blob_view& view, const rule& selected, std::uint8_t family) noexcept
{
    if (selected.network_count == 0)
    {
        return 1;
    }

    std::uint32_t count = 0;
    for (std::uint32_t index = 0; index < selected.network_count; ++index)
    {
        if (view.network_pool[selected.network_first + index].family == family)
        {
            ++count;
        }
    }
    return count;
}

[[nodiscard]] const network_prefix*
layer_network(const blob_view& view, const rule& selected, std::uint8_t family, std::uint32_t ordinal) noexcept
{
    if (selected.network_count == 0)
    {
        return nullptr;
    }

    std::uint32_t seen = 0;
    for (std::uint32_t index = 0; index < selected.network_count; ++index)
    {
        const network_prefix& prefix = view.network_pool[selected.network_first + index];
        if (prefix.family != family)
        {
            continue;
        }
        if (seen == ordinal)
        {
            return &prefix;
        }
        ++seen;
    }
    return nullptr;
}

struct protocol_expansion
{
    std::uint8_t values[2];
    std::uint32_t count;
};

[[nodiscard]] protocol_expansion expand_block_protocols(std::uint8_t protocol_mask) noexcept
{
    // 策略只治理 TCP/UDP。ALE_AUTH_CONNECT 不写协议条件时还会匹配首个出站 ICMP（以及按 UDP
    // 处理的其他 IP 协议），而 classify 对这些协议走 permitted_non_tcp 早退；因此 block filter
    // 必须显式写 6/17，不能依赖“省略条件等价于该层所有协议”。掩码 0 与 3 都展开成两条。
    // <mutation id="block-filter-protocol">
    if (protocol_mask == rule_protocol_mask_any || protocol_mask == rule_protocol_mask_all)
    {
        return protocol_expansion{ { rule_protocol_tcp, rule_protocol_udp }, 2 };
    }
    // </mutation>
    if ((protocol_mask & rule_protocol_mask_tcp) != 0)
    {
        return protocol_expansion{ { rule_protocol_tcp, 0 }, 1 };
    }
    return protocol_expansion{ { rule_protocol_udp, 0 }, 1 };
}

void append_protocol_condition(block_conditions& out, std::uint8_t protocol) noexcept
{
    FWPM_FILTER_CONDITION0& condition = out.entries[out.count++];
    condition.fieldKey = FWPM_CONDITION_IP_PROTOCOL;
    condition.matchType = FWP_MATCH_EQUAL;
    condition.conditionValue.type = FWP_UINT8;
    condition.conditionValue.uint8 = protocol;
}

void append_port_condition(block_conditions& out, const port_range& range) noexcept
{
    out.port_range.valueLow.type = FWP_UINT16;
    out.port_range.valueLow.uint16 = range.start;
    out.port_range.valueHigh.type = FWP_UINT16;
    out.port_range.valueHigh.uint16 = range.end;
    FWPM_FILTER_CONDITION0& condition = out.entries[out.count++];
    condition.fieldKey = FWPM_CONDITION_IP_REMOTE_PORT;
    condition.matchType = FWP_MATCH_RANGE;
    condition.conditionValue.type = FWP_RANGE_TYPE;
    condition.conditionValue.rangeValue = &out.port_range;
}

void append_network_condition(block_conditions& out, const network_prefix& prefix) noexcept
{
    // /0 表示该族任意地址，省略条件即可；层已经由 rule_applies_to_layer 限定到这一族。
    if (prefix.prefix_length == 0)
    {
        return;
    }

    if (prefix.family == rule_family_ipv4)
    {
        // FWP_V4_ADDR_AND_MASK 的 addr 与 mask 都是主机字节序（fwptypes.h 的文档原文）。
        std::uint32_t network_order = 0;
        RtlCopyMemory(&network_order, prefix.network, 4);
        out.v4_mask.addr = RtlUlongByteSwap(network_order);
        out.v4_mask.mask = prefix.prefix_length >= 32 ? 0xFFFFFFFFU : (0xFFFFFFFFU << (32 - prefix.prefix_length));
        FWPM_FILTER_CONDITION0& condition = out.entries[out.count++];
        condition.fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS;
        condition.matchType = FWP_MATCH_EQUAL;
        condition.conditionValue.type = FWP_V4_ADDR_MASK;
        condition.conditionValue.v4AddrMask = &out.v4_mask;
    }
    else if (prefix.family == rule_family_ipv6)
    {
        RtlCopyMemory(out.v6_mask.addr, prefix.network, 16);
        out.v6_mask.prefixLength = prefix.prefix_length;
        FWPM_FILTER_CONDITION0& condition = out.entries[out.count++];
        condition.fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS;
        condition.matchType = FWP_MATCH_EQUAL;
        condition.conditionValue.type = FWP_V6_ADDR_MASK;
        condition.conditionValue.v6AddrMask = &out.v6_mask;
    }
}

[[nodiscard]] bool append_app_id_condition(block_conditions& out, const char* selector) noexcept
{
    if (!fill_block_app_id(selector, out.app_id_storage, out.app_id.size))
    {
        return false;
    }
    out.app_id.data = out.app_id_storage;
    FWPM_FILTER_CONDITION0& condition = out.entries[out.count++];
    condition.fieldKey = FWPM_CONDITION_ALE_APP_ID;
    condition.matchType = FWP_MATCH_EQUAL;
    condition.conditionValue.type = FWP_BYTE_BLOB_TYPE;
    condition.conditionValue.byteBlob = &out.app_id;
    return true;
}

// process_text / port / network 为 nullptr 表示该字段任意；block 规则的进程条件只能是精确
// ALE_APP_ID（kind=path 且非目录），由 allocate_rule_table 的 block 表达边界保证。
[[nodiscard]] bool build_block_conditions(const char* process_text,
                                          const port_range* port,
                                          const network_prefix* network,
                                          std::uint8_t protocol,
                                          block_conditions& out) noexcept
{
    RtlZeroMemory(&out, sizeof(out));
    append_protocol_condition(out, protocol);
    if (port != nullptr)
    {
        append_port_condition(out, *port);
    }
    if (network != nullptr)
    {
        append_network_condition(out, *network);
    }
    return process_text == nullptr || append_app_id_condition(out, process_text);
}

void build_protocol_only_conditions(std::uint8_t protocol, block_conditions& out) noexcept
{
    RtlZeroMemory(&out, sizeof(out));
    append_protocol_condition(out, protocol);
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

[[nodiscard]] NTSTATUS add_filter_variant(const char* process_text,
                                          const port_range* port,
                                          const network_prefix* network,
                                          std::uint8_t protocol,
                                          bool ipv6_layer,
                                          UINT64& filter_id) noexcept
{
    block_conditions conditions{};
    if (!build_block_conditions(process_text, port, network, protocol, conditions))
    {
        return STATUS_INVALID_PARAMETER;
    }
    return add_block_filter(ipv6_layer ? FWPM_LAYER_ALE_AUTH_CONNECT_V6 : FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                            conditions.entries,
                            conditions.count,
                            filter_id);
}

// 一条 block 规则按 layer × protocol × process × port × network 展开：WFP 同一字段的多个条件
// 是 AND，表达不了数组的 OR。进程必须全是精确文件路径；这里再做一次防御性检查。
[[nodiscard]] NTSTATUS
install_rule_block_filters(const blob_view& view, const rule& selected, UINT64* ids, std::uint32_t& count) noexcept
{
    const protocol_expansion protocols = expand_block_protocols(selected.protocol_mask);
    for (std::uint32_t layer = 0; layer < 2; ++layer)
    {
        const bool ipv6_layer = layer == 1;
        if (!rule_applies_to_layer(view, selected, ipv6_layer))
        {
            continue;
        }

        const std::uint8_t family = ipv6_layer ? rule_family_ipv6 : rule_family_ipv4;
        const std::uint32_t process_total = selected.process_count == 0 ? 1 : selected.process_count;
        const std::uint32_t port_total = selected.port_count == 0 ? 1 : selected.port_count;
        const std::uint32_t network_total = layer_network_count(view, selected, family);

        for (std::uint32_t protocol_index = 0; protocol_index < protocols.count; ++protocol_index)
        {
            for (std::uint32_t process_slot = 0; process_slot < process_total; ++process_slot)
            {
                const process_selector* process =
                    selected.process_count == 0 ? nullptr : &view.selector_pool[selected.process_first + process_slot];
                if (process != nullptr && (process->kind != process_selector_kind::path || process->is_directory != 0))
                {
                    return STATUS_INVALID_PARAMETER;
                }
                const char* process_text = process == nullptr ? nullptr : view.process_pool + process->string_offset;

                for (std::uint32_t port_slot = 0; port_slot < port_total; ++port_slot)
                {
                    const port_range* port =
                        selected.port_count == 0 ? nullptr : &view.port_pool[selected.port_first + port_slot];
                    for (std::uint32_t network_slot = 0; network_slot < network_total; ++network_slot)
                    {
                        if (count >= max_block_filters)
                        {
                            return STATUS_INVALID_PARAMETER;
                        }
                        const network_prefix* network = layer_network(view, selected, family, network_slot);
                        const NTSTATUS status = add_filter_variant(
                            process_text, port, network, protocols.values[protocol_index], ipv6_layer, ids[count]);
                        if (!NT_SUCCESS(status))
                        {
                            return status;
                        }
                        ++count;
                    }
                }
            }
        }
    }
    return STATUS_SUCCESS;
}

} // namespace

NTSTATUS probe_install_block_filters(const blob_view& view, UINT64* ids, std::uint32_t& count) noexcept
{
    count = 0;
    for (std::uint32_t rule_index = 0; rule_index < view.rule_count; ++rule_index)
    {
        const rule& selected = view.rules[rule_index];
        // <mutation id="block-filter-skip">
        if (selected.action != rule_action::block)
        // </mutation>
        {
            continue;
        }

        const NTSTATUS status = install_rule_block_filters(view, selected, ids, count);
        if (!NT_SUCCESS(status))
        {
            probe_delete_block_filters(ids, count);
            return status;
        }
    }

    if (view.fallback_action == rule_action::block)
    {
        // 兜底没有规则条件：每层 × 协议展开成 2 × 2 = 4 条只带协议条件的 filter。
        const protocol_expansion protocols = expand_block_protocols(rule_protocol_mask_any);
        for (std::uint32_t layer = 0; layer < 2; ++layer)
        {
            for (std::uint32_t protocol_index = 0; protocol_index < protocols.count; ++protocol_index)
            {
                if (count >= max_block_filters)
                {
                    probe_delete_block_filters(ids, count);
                    return STATUS_INVALID_PARAMETER;
                }
                block_conditions conditions{};
                build_protocol_only_conditions(protocols.values[protocol_index], conditions);
                const NTSTATUS status =
                    add_block_filter(layer == 1 ? FWPM_LAYER_ALE_AUTH_CONNECT_V6 : FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                                     conditions.entries,
                                     conditions.count,
                                     ids[count]);
                if (!NT_SUCCESS(status))
                {
                    probe_delete_block_filters(ids, count);
                    return status;
                }
                ++count;
            }
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
