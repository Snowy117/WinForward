#include "probe_driver.h"
#include "probe_forwarded_state.h"

namespace winforward::wfp::probe
{
namespace
{
// B3 实测：MAC_FRAME_NATIVE 的 inFixedValues 只有媒体类型 / 接口 / 端口 / L2 标志 / 分区，
// 没有 MAC、EtherType、IP 与端口，全部要自己从 NBL 解析。窗口 192 字节覆盖
// 以太网头(14) + 最多两个 VLAN 标签(8) + IPv4 选项前 60 或 IPv6 40+扩展头 64 + TCP 20。
constexpr std::uint32_t ethernet_header_size = 14;
constexpr std::uint32_t vlan_tag_size = 4;
constexpr std::uint32_t max_vlan_tags = 2;
constexpr std::uint32_t ipv4_min_header_size = 20;
constexpr std::uint32_t ipv6_header_size = 40;
constexpr std::uint32_t ipv6_max_extension_hops = 4;
constexpr std::uint32_t frame_window_size = 192;

constexpr std::uint16_t ethertype_ipv4 = 0x0800;
constexpr std::uint16_t ethertype_ipv6 = 0x86DD;
constexpr std::uint16_t ethertype_vlan = 0x8100;
constexpr std::uint16_t ethertype_qinq = 0x88A8;

constexpr std::uint8_t ip_protocol_tcp = 6;
constexpr std::uint8_t ip_protocol_udp = 17;
constexpr std::uint8_t ipv6_next_hop_by_hop = 0;
constexpr std::uint8_t ipv6_next_routing = 43;
constexpr std::uint8_t ipv6_next_fragment = 44;
constexpr std::uint8_t ipv6_next_destination_options = 60;

[[nodiscard]] std::uint16_t read_be16(const std::uint8_t* data) noexcept
{
    return static_cast<std::uint16_t>((static_cast<std::uint16_t>(data[0]) << 8) | data[1]);
}

enum class frame_kind : std::uint8_t
{
    ip = 0,
    non_ip = 1,
    malformed = 2,
};

// 已解析的帧事实。地址按「谁是谁」保存，方向映射在 classify 里做。
struct frame_facts
{
    std::uint8_t source_address[16];
    std::uint8_t destination_address[16];
    std::uint16_t source_port;
    std::uint16_t destination_port;
    std::uint8_t protocol;
    address_family family;
};

[[nodiscard]] bool
parse_ipv4(const std::uint8_t* frame, std::uint32_t length, std::uint32_t offset, frame_facts& facts) noexcept
{
    if (length < offset + ipv4_min_header_size)
    {
        return false;
    }

    const std::uint8_t version_ihl = frame[offset];
    if ((version_ihl >> 4) != 4)
    {
        return false;
    }

    const std::uint32_t header_length = static_cast<std::uint32_t>(version_ihl & 0x0FU) * 4U;
    if (header_length < ipv4_min_header_size || length < offset + header_length)
    {
        return false;
    }

    const std::uint16_t total_length = read_be16(frame + offset + 2);
    if (total_length < header_length)
    {
        return false;
    }

    // 非首片没有传输层头部：协议仍然可知，端口一律按 0 处理（端口规则不会命中）。
    const std::uint16_t fragment = read_be16(frame + offset + 6);
    const bool first_fragment = (static_cast<std::uint32_t>(fragment) & 0x1FFFU) == 0;

    facts.family = address_family::ipv4;
    facts.protocol = frame[offset + 9];
    RtlCopyMemory(facts.source_address, frame + offset + 12, 4);
    RtlCopyMemory(facts.destination_address, frame + offset + 16, 4);

    const std::uint32_t transport = offset + header_length;
    if (first_fragment && (facts.protocol == ip_protocol_tcp || facts.protocol == ip_protocol_udp) &&
        length >= transport + 4)
    {
        facts.source_port = read_be16(frame + transport);
        facts.destination_port = read_be16(frame + transport + 2);
    }
    return true;
}

[[nodiscard]] bool
parse_ipv6(const std::uint8_t* frame, std::uint32_t length, std::uint32_t offset, frame_facts& facts) noexcept
{
    if (length < offset + ipv6_header_size)
    {
        return false;
    }
    if ((frame[offset] >> 4) != 6)
    {
        return false;
    }

    std::uint8_t next_header = frame[offset + 6];
    std::uint32_t transport = offset + ipv6_header_size;
    bool first_fragment = true;

    // 只走常见的扩展头（逐跳 / 路由 / 目的选项 / 分片 / AH），上限四跳：
    // 解析的目的只是找到传输层端口，走不到就按「端口不可得」处理。
    for (std::uint32_t hop = 0; hop < ipv6_max_extension_hops; ++hop)
    {
        if (next_header == ipv6_next_hop_by_hop || next_header == ipv6_next_routing ||
            next_header == ipv6_next_destination_options)
        {
            if (length < transport + 2)
            {
                return false;
            }
            const std::uint32_t extension_length = (static_cast<std::uint32_t>(frame[transport + 1]) + 1U) * 8U;
            if (length < transport + extension_length)
            {
                return false;
            }
            next_header = frame[transport];
            transport += extension_length;
            continue;
        }

        if (next_header == ipv6_next_fragment)
        {
            if (length < transport + 8)
            {
                return false;
            }
            const std::uint16_t fragment = read_be16(frame + transport + 2);
            first_fragment = (static_cast<std::uint32_t>(fragment) >> 3) == 0;
            next_header = frame[transport];
            transport += 8;
            continue;
        }

        break;
    }

    facts.family = address_family::ipv6;
    facts.protocol = next_header;
    RtlCopyMemory(facts.source_address, frame + offset + 8, 16);
    RtlCopyMemory(facts.destination_address, frame + offset + 24, 16);

    if (first_fragment && (facts.protocol == ip_protocol_tcp || facts.protocol == ip_protocol_udp) &&
        length >= transport + 4)
    {
        facts.source_port = read_be16(frame + transport);
        facts.destination_port = read_be16(frame + transport + 2);
    }
    return true;
}

// 从 NBL 解析一帧。layer_data 在 MAC_FRAME_NATIVE 上就是从以太网头开始的 NBL。
[[nodiscard]] frame_kind parse_frame(const NET_BUFFER_LIST* nbl, frame_facts& facts) noexcept
{
    if (nbl == nullptr)
    {
        return frame_kind::malformed;
    }

    const NET_BUFFER* buffer = NET_BUFFER_LIST_FIRST_NB(const_cast<NET_BUFFER_LIST*>(nbl));
    if (buffer == nullptr)
    {
        return frame_kind::malformed;
    }

    const ULONG data_length = NET_BUFFER_DATA_LENGTH(const_cast<NET_BUFFER*>(buffer));
    if (data_length < ethernet_header_size)
    {
        return frame_kind::malformed;
    }

    UCHAR window[frame_window_size]{};
    const ULONG wanted = data_length < frame_window_size ? data_length : frame_window_size;
    // NdisGetDataBuffer 在数据连续时返回原指针并忽略 Storage，必须用返回值。
    const std::uint8_t* frame =
        static_cast<const std::uint8_t*>(NdisGetDataBuffer(const_cast<NET_BUFFER*>(buffer), wanted, window, 1, 0));
    if (frame == nullptr)
    {
        return frame_kind::malformed;
    }

    const std::uint32_t length = wanted;
    std::uint32_t offset = ethernet_header_size;
    std::uint16_t ethertype = read_be16(frame + 12);

    // 0x8100 (802.1Q) / 0x88A8 (802.1ad)：标签 4 字节（TPID 已读，剩下 TCI + 内层 EtherType）。
    for (std::uint32_t tags = 0; tags < max_vlan_tags; ++tags)
    {
        if (ethertype != ethertype_vlan && ethertype != ethertype_qinq)
        {
            break;
        }
        if (length < offset + vlan_tag_size)
        {
            return frame_kind::malformed;
        }
        ethertype = read_be16(frame + offset + 2);
        offset += vlan_tag_size;
    }

    if (ethertype == ethertype_ipv4)
    {
        return parse_ipv4(frame, length, offset, facts) ? frame_kind::ip : frame_kind::malformed;
    }
    if (ethertype == ethertype_ipv6)
    {
        return parse_ipv6(frame, length, offset, facts) ? frame_kind::ip : frame_kind::malformed;
    }
    // ARP / LLDP / 其它非 IP 帧不归 forwarded 规则管：一律放行，绝不参与 block。
    return frame_kind::non_ip;
}

[[nodiscard]] std::uint32_t frame_interface_index(const FWPS_INCOMING_VALUES0* values, bool inbound) noexcept
{
    if (values == nullptr)
    {
        return 0;
    }
    const UINT16 field = inbound ? FWPS_FIELD_INBOUND_MAC_FRAME_NATIVE_INTERFACE_INDEX
                                 : FWPS_FIELD_OUTBOUND_MAC_FRAME_NATIVE_INTERFACE_INDEX;
    if (field >= values->valueCount)
    {
        return 0;
    }
    const FWP_VALUE0& value = values->incomingValue[field].value;
    return value.type == FWP_UINT32 ? value.uint32 : 0;
}

void set_frame_action(FWPS_CLASSIFY_OUT0* classify_out, bool block) noexcept
{
    classify_out->actionType = block ? FWP_ACTION_BLOCK : FWP_ACTION_PERMIT;
    classify_out->rights &= ~FWPS_RIGHT_ACTION_WRITE;
}

} // namespace

void NTAPI probe_forwarded_classify_fn(const FWPS_INCOMING_VALUES0* in_fixed_values,
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

    forwarded_state& state = g_forwarded_state;
    // 一帧的全部计数先在本地累加，退出时一次性并入全局；snapshot / reset 与这里共用同一把锁，
    // 因此不会读到「classify_calls 已加、终态还没加」的中间状态。
    probe_forwarded_stats delta{};
    delta.classify_calls = 1;

    if ((classify_out->rights & FWPS_RIGHT_ACTION_WRITE) == 0)
    {
        delta.no_write_right = 1;
        publish_forwarded_counters(state, delta);
        return;
    }

    const bool inbound = in_fixed_values != nullptr && in_fixed_values->layerId == FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE;
    const bool outbound =
        in_fixed_values != nullptr && in_fixed_values->layerId == FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE;
    if (!inbound && !outbound)
    {
        delta.malformed = 1;
        publish_forwarded_counters(state, delta);
        set_frame_action(classify_out, false);
        return;
    }
    if (inbound)
    {
        delta.inbound_frames = 1;
    }
    else
    {
        delta.outbound_frames = 1;
    }

    // 防环：本驱动注入的帧会被再次 classify（B4b 起才有注入路径，这里先把闸装上）。
    if (state.injection_handle != nullptr && layer_data != nullptr)
    {
        const FWPS_PACKET_INJECTION_STATE injection_state = FwpsQueryPacketInjectionState0(
            state.injection_handle, static_cast<const NET_BUFFER_LIST*>(layer_data), nullptr);
        if (injection_state == FWPS_PACKET_INJECTED_BY_SELF ||
            injection_state == FWPS_PACKET_PREVIOUSLY_INJECTED_BY_SELF)
        {
            delta.self_injected = 1;
            publish_forwarded_counters(state, delta);
            set_frame_action(classify_out, false);
            return;
        }
    }

    forwarded_table* table = nullptr;
    take_forwarded_snapshot(table);
    if (table == nullptr)
    {
        delta.no_table = 1;
        publish_forwarded_counters(state, delta);
        set_frame_action(classify_out, false);
        return;
    }

    frame_facts facts{};
    const frame_kind kind = parse_frame(static_cast<const NET_BUFFER_LIST*>(layer_data), facts);
    if (kind != frame_kind::ip)
    {
        release_forwarded_snapshot(table);
        if (kind == frame_kind::non_ip)
        {
            delta.non_ip = 1;
        }
        else
        {
            delta.malformed = 1;
        }
        publish_forwarded_counters(state, delta);
        set_frame_action(classify_out, false);
        return;
    }

    forwarded_connection_view connection{};
    connection.ingress_adapter_id = frame_interface_index(in_fixed_values, inbound);
    // 方向语义：client 永远是「被转发的客户端」，remote 永远是它要去的远端。
    // 入向 client = 源、remote = 目的、remote_port = 目的端口；
    // 出向 client = 目的、remote = 源、remote_port = 源端口。
    if (inbound)
    {
        RtlCopyMemory(connection.client_address, facts.source_address, 16);
        RtlCopyMemory(connection.remote_address, facts.destination_address, 16);
        connection.remote_port = facts.destination_port;
    }
    else
    {
        RtlCopyMemory(connection.client_address, facts.destination_address, 16);
        RtlCopyMemory(connection.remote_address, facts.source_address, 16);
        connection.remote_port = facts.source_port;
    }
    connection.protocol = facts.protocol;
    connection.family = facts.family;

    const forwarded_match_result result = match_forwarded(table->view, connection);
    release_forwarded_snapshot(table);

    bool block = false;
    if (result.matched)
    {
        delta.matched = 1;
        if (result.action == rule_action::block)
        {
            delta.matched_block = 1;
            block = true;
        }
        else
        {
            delta.matched_permit = 1;
        }
    }
    else
    {
        delta.fallback = 1;
        if (result.action == rule_action::block)
        {
            delta.fallback_block = 1;
            block = true;
        }
        else
        {
            delta.fallback_permit = 1;
        }
    }

    if (block)
    {
        delta.blocked = 1;
    }
    else
    {
        delta.permitted = 1;
    }
    publish_forwarded_counters(state, delta);
    set_frame_action(classify_out, block);
}

NTSTATUS NTAPI probe_forwarded_notify_fn(FWPS_CALLOUT_NOTIFY_TYPE notify_type,
                                         const GUID* filter_key,
                                         FWPS_FILTER1* filter) noexcept
{
    UNREFERENCED_PARAMETER(notify_type);
    UNREFERENCED_PARAMETER(filter_key);
    UNREFERENCED_PARAMETER(filter);
    return STATUS_SUCCESS;
}

} // namespace winforward::wfp::probe
