#include "probe_driver.h"
#include "probe_forwarded_checksum.h"
#include "probe_forwarded_state.h"

namespace winforward::wfp::probe
{
namespace
{

// 注入帧的数据缓冲池标签：与 rule_table ('rTfW') / redirect context ('rPfW') 分开，
// 用户态按 tag 观察池用量时不会把注入缓冲算进规则表或 context。
constexpr std::uint32_t injection_pool_tag = 'jIfW';

constexpr std::uint32_t ethernet_header_size = 14;
constexpr std::uint32_t vlan_tag_size = 4;
constexpr std::uint32_t max_vlan_tags = 2;
constexpr std::uint32_t ipv4_min_header_size = 20;
constexpr std::uint32_t ipv6_header_size = 40;
constexpr std::uint32_t ipv6_max_extension_hops = 4;
constexpr std::uint32_t tcp_min_header_size = 20;
constexpr std::uint32_t udp_header_size = 8;
constexpr std::uint32_t frame_window_size = 192;

constexpr std::uint16_t ethertype_ipv4 = 0x0800;
constexpr std::uint16_t ethertype_ipv6 = 0x86DD;
constexpr std::uint16_t ethertype_vlan = 0x8100;
constexpr std::uint16_t ethertype_qinq = 0x88A8;

constexpr std::uint8_t ipv6_next_hop_by_hop = 0;
constexpr std::uint8_t ipv6_next_routing = 43;
constexpr std::uint8_t ipv6_next_fragment = 44;
constexpr std::uint8_t ipv6_next_destination_options = 60;

[[nodiscard]] std::uint16_t read_be16(const std::uint8_t* data) noexcept
{
    return static_cast<std::uint16_t>((static_cast<std::uint16_t>(data[0]) << 8) | data[1]);
}

[[nodiscard]] bool
parse_ipv4(const std::uint8_t* frame, std::uint32_t length, std::uint32_t offset, forwarded_frame_facts& facts) noexcept
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

    const std::uint16_t fragment = read_be16(frame + offset + 6);
    const bool more_fragments = (fragment & 0x2000U) != 0;
    const bool first_fragment = (static_cast<std::uint32_t>(fragment) & 0x1FFFU) == 0;
    facts.first_fragment = first_fragment;
    facts.fragmented = more_fragments || !first_fragment;
    facts.family = 4;
    facts.protocol = frame[offset + 9];
    facts.ip_header_length = header_length;
    facts.ip_payload_length = static_cast<std::uint32_t>(total_length) - header_length;
    RtlCopyMemory(facts.source_address, frame + offset + 12, 4);
    RtlCopyMemory(facts.destination_address, frame + offset + 16, 4);

    const std::uint32_t transport = offset + header_length;
    facts.transport_offset = transport;
    if (first_fragment && facts.protocol == ip_protocol_tcp && length >= transport + tcp_min_header_size &&
        facts.ip_payload_length >= tcp_min_header_size)
    {
        facts.source_port = read_be16(frame + transport);
        facts.destination_port = read_be16(frame + transport + 2);
        facts.tcp_flags = frame[transport + 13];
        facts.transport_length = facts.ip_payload_length;
    }
    else if (first_fragment && facts.protocol == ip_protocol_udp && length >= transport + 4)
    {
        facts.source_port = read_be16(frame + transport);
        facts.destination_port = read_be16(frame + transport + 2);
        if (length >= transport + 8)
        {
            facts.transport_length = read_be16(frame + transport + 4);
        }
    }
    return true;
}

[[nodiscard]] bool
parse_ipv6(const std::uint8_t* frame, std::uint32_t length, std::uint32_t offset, forwarded_frame_facts& facts) noexcept
{
    if (length < offset + ipv6_header_size)
    {
        return false;
    }
    if ((frame[offset] >> 4) != 6)
    {
        return false;
    }

    const std::uint16_t payload_length = read_be16(frame + offset + 4);
    std::uint8_t next_header = frame[offset + 6];
    std::uint32_t transport = offset + ipv6_header_size;
    bool first_fragment = true;
    bool fragmented = false;

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
            fragmented = true;
            next_header = frame[transport];
            transport += 8;
            continue;
        }

        break;
    }

    const std::uint32_t extension_bytes = transport - (offset + ipv6_header_size);
    if (payload_length < extension_bytes)
    {
        return false;
    }

    facts.family = 6;
    facts.protocol = next_header;
    facts.ip_header_length = ipv6_header_size + extension_bytes;
    facts.ip_payload_length = static_cast<std::uint32_t>(payload_length) - extension_bytes;
    facts.transport_offset = transport;
    facts.first_fragment = first_fragment;
    facts.fragmented = fragmented;
    RtlCopyMemory(facts.source_address, frame + offset + 8, 16);
    RtlCopyMemory(facts.destination_address, frame + offset + 24, 16);
    if (first_fragment && next_header == ip_protocol_tcp && length >= transport + tcp_min_header_size &&
        facts.ip_payload_length >= tcp_min_header_size)
    {
        facts.source_port = read_be16(frame + transport);
        facts.destination_port = read_be16(frame + transport + 2);
        facts.tcp_flags = frame[transport + 13];
        facts.transport_length = facts.ip_payload_length;
    }
    else if (first_fragment && next_header == ip_protocol_udp && length >= transport + 4)
    {
        facts.source_port = read_be16(frame + transport);
        facts.destination_port = read_be16(frame + transport + 2);
        if (length >= transport + 8)
        {
            facts.transport_length = read_be16(frame + transport + 4);
        }
    }
    return true;
}

void NTAPI injection_complete(void* context, NET_BUFFER_LIST* nbl, BOOLEAN dispatch_level) noexcept
{
    UNREFERENCED_PARAMETER(context);
    UNREFERENCED_PARAMETER(dispatch_level);
    if (nbl == nullptr)
    {
        return;
    }

    forwarded_state& state = g_forwarded_state;
    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&state.stats.inject_complete_calls));
    const NDIS_STATUS injection_status = NET_BUFFER_LIST_STATUS(nbl);
    if (injection_status != NDIS_STATUS_SUCCESS)
    {
        InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&state.stats.inject_complete_failed));
        state.stats.last_status = static_cast<std::uint32_t>(injection_status);
    }
    InterlockedDecrement64(&state.outstanding_injections);

    NET_BUFFER* buffer = NET_BUFFER_LIST_FIRST_NB(nbl);
    PMDL mdl = buffer != nullptr ? NET_BUFFER_FIRST_MDL(buffer) : nullptr;
    PVOID data = mdl != nullptr ? MmGetMdlVirtualAddress(mdl) : nullptr;

    FwpsFreeNetBufferList0(nbl);
    if (mdl != nullptr)
    {
        NdisFreeMdl(mdl);
    }
    if (data != nullptr)
    {
        ExFreePoolWithTag(data, injection_pool_tag);
    }
}

[[nodiscard]] bool rewrite_in_bounds(const forwarded_frame_facts& facts) noexcept
{
    const std::uint32_t address_size = facts.family == 6 ? 16U : 4U;
    const std::uint32_t frame_length = facts.data_length;
    if (facts.network_offset + 20U > frame_length)
    {
        return false;
    }
    if (facts.network_offset + address_size * 2U > frame_length)
    {
        return false;
    }
    if (facts.transport_offset + 4U > frame_length)
    {
        return false;
    }
    if (facts.protocol == ip_protocol_tcp)
    {
        return facts.transport_length >= tcp_min_header_size &&
               facts.transport_offset + facts.transport_length <= frame_length;
    }
    if (facts.protocol == ip_protocol_udp)
    {
        if (facts.transport_length < udp_header_size)
        {
            return false;
        }
        if (facts.fragmented)
        {
            // 分片一律在 classify 早退 block；这里再拒绝一次，避免将来新增调用点绕过 fail-closed。
            return false;
        }
        return facts.transport_offset + facts.transport_length <= frame_length;
    }
    return false;
}

} // namespace

forwarded_frame_kind probe_forwarded_parse_frame(const NET_BUFFER* buffer, forwarded_frame_facts& facts) noexcept
{
    RtlZeroMemory(&facts, sizeof(facts));
    if (buffer == nullptr)
    {
        return forwarded_frame_kind::malformed;
    }

    const ULONG data_length = NET_BUFFER_DATA_LENGTH(const_cast<NET_BUFFER*>(buffer));
    if (data_length < ethernet_header_size)
    {
        return forwarded_frame_kind::malformed;
    }

    UCHAR window[frame_window_size]{};
    const ULONG wanted = data_length < frame_window_size ? data_length : frame_window_size;
    const std::uint8_t* frame =
        static_cast<const std::uint8_t*>(NdisGetDataBuffer(const_cast<NET_BUFFER*>(buffer), wanted, window, 1, 0));
    if (frame == nullptr)
    {
        return forwarded_frame_kind::malformed;
    }

    facts.data_length = data_length;
    RtlCopyMemory(facts.destination_mac, frame, 6);
    RtlCopyMemory(facts.source_mac, frame + 6, 6);

    const std::uint32_t length = wanted;
    std::uint32_t offset = ethernet_header_size;
    std::uint16_t ethertype = read_be16(frame + 12);

    for (std::uint32_t tags = 0; tags < max_vlan_tags; ++tags)
    {
        if (ethertype != ethertype_vlan && ethertype != ethertype_qinq)
        {
            break;
        }
        if (length < offset + vlan_tag_size)
        {
            return forwarded_frame_kind::malformed;
        }
        ethertype = read_be16(frame + offset + 2);
        offset += vlan_tag_size;
    }

    facts.network_offset = offset;
    if (ethertype == ethertype_ipv4)
    {
        return parse_ipv4(frame, length, offset, facts) ? forwarded_frame_kind::ip : forwarded_frame_kind::malformed;
    }
    if (ethertype == ethertype_ipv6)
    {
        return parse_ipv6(frame, length, offset, facts) ? forwarded_frame_kind::ip : forwarded_frame_kind::malformed;
    }
    return forwarded_frame_kind::non_ip;
}

forwarded_frame_kind probe_forwarded_parse_frame(const NET_BUFFER_LIST* nbl, forwarded_frame_facts& facts) noexcept
{
    if (nbl == nullptr)
    {
        RtlZeroMemory(&facts, sizeof(facts));
        return forwarded_frame_kind::malformed;
    }

    return probe_forwarded_parse_frame(NET_BUFFER_LIST_FIRST_NB(const_cast<NET_BUFFER_LIST*>(nbl)), facts);
}

NTSTATUS probe_forwarded_inject_frame(const NET_BUFFER* source,
                                      const forwarded_frame_facts& facts,
                                      bool outbound,
                                      std::uint32_t interface_index,
                                      std::uint32_t ndis_port,
                                      const forwarded_frame_rewrite& rewrite) noexcept
{
    forwarded_state& state = g_forwarded_state;
    if (source == nullptr || state.injection_handle == nullptr || state.nbl_pool == nullptr)
    {
        return STATUS_INVALID_DEVICE_STATE;
    }
    if (facts.data_length < ethernet_header_size || facts.family == 0 || !rewrite_in_bounds(facts))
    {
        return STATUS_INVALID_PARAMETER;
    }

    const NET_BUFFER* buffer = source;

    const std::uint32_t frame_length = facts.data_length;
    PVOID storage = ExAllocatePool2(POOL_FLAG_NON_PAGED, frame_length, injection_pool_tag);
    if (storage == nullptr)
    {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    PVOID source_data = NdisGetDataBuffer(const_cast<NET_BUFFER*>(buffer), frame_length, storage, 1, 0);
    if (source_data == nullptr)
    {
        ExFreePoolWithTag(storage, injection_pool_tag);
        return STATUS_INVALID_PARAMETER;
    }
    if (source_data != storage)
    {
        RtlCopyMemory(storage, source_data, frame_length);
    }

    auto* frame = static_cast<std::uint8_t*>(storage);
    if (rewrite.destination_mac != nullptr)
    {
        RtlCopyMemory(frame, rewrite.destination_mac, 6);
    }
    if (rewrite.source_mac != nullptr)
    {
        RtlCopyMemory(frame + 6, rewrite.source_mac, 6);
    }

    const std::uint32_t network = facts.network_offset;
    const std::uint32_t transport = facts.transport_offset;
    if (rewrite.source_address != nullptr)
    {
        RtlCopyMemory(
            frame + network + (facts.family == 6 ? 8U : 12U), rewrite.source_address, facts.family == 6 ? 16U : 4U);
    }
    if (rewrite.destination_address != nullptr)
    {
        RtlCopyMemory(frame + network + (facts.family == 6 ? 24U : 16U),
                      rewrite.destination_address,
                      facts.family == 6 ? 16U : 4U);
    }
    if (rewrite.rewrite_source_port)
    {
        frame[transport] = static_cast<std::uint8_t>((rewrite.source_port >> 8) & 0xFFU);
        frame[transport + 1] = static_cast<std::uint8_t>(rewrite.source_port & 0xFFU);
    }
    if (rewrite.rewrite_destination_port)
    {
        frame[transport + 2] = static_cast<std::uint8_t>((rewrite.destination_port >> 8) & 0xFFU);
        frame[transport + 3] = static_cast<std::uint8_t>(rewrite.destination_port & 0xFFU);
    }

    if (facts.family == 4)
    {
        frame[network + 10] = 0;
        frame[network + 11] = 0;
        const std::uint16_t header_sum = ipv4_header_checksum(frame + network, facts.ip_header_length);
        frame[network + 10] = static_cast<std::uint8_t>((header_sum >> 8) & 0xFFU);
        frame[network + 11] = static_cast<std::uint8_t>(header_sum & 0xFFU);
    }

    if (facts.protocol == ip_protocol_udp)
    {
        frame[transport + 6] = 0;
        frame[transport + 7] = 0;
        std::uint16_t udp_sum = facts.family == 6
                                    ? udp_checksum_ipv6(frame + network, frame + transport, facts.transport_length)
                                    : udp_checksum_ipv4(frame + network, frame + transport, facts.transport_length);
        if (udp_sum == 0)
        {
            // RFC 768：计算结果是 0 时要写成全 1（0 表示“无校验和”，只对 IPv4 合法）。
            udp_sum = 0xFFFFU;
        }
        frame[transport + 6] = static_cast<std::uint8_t>((udp_sum >> 8) & 0xFFU);
        frame[transport + 7] = static_cast<std::uint8_t>(udp_sum & 0xFFU);
    }
    else
    {
        frame[transport + 16] = 0;
        frame[transport + 17] = 0;
        const std::uint16_t tcp_sum =
            facts.family == 6 ? tcp_checksum_ipv6(frame + network, frame + transport, facts.transport_length)
                              : tcp_checksum_ipv4(frame + network, frame + transport, facts.transport_length);
        frame[transport + 16] = static_cast<std::uint8_t>((tcp_sum >> 8) & 0xFFU);
        frame[transport + 17] = static_cast<std::uint8_t>(tcp_sum & 0xFFU);
    }

    // NdisHandle 传 NBL 池句柄而不是 nullptr：PREfast C6387 把 nullptr 当缺陷；
    // 也不能改用 IoAllocateMdl —— 实测那种 MDL 注入后在 inbound 层被本机栈丢弃（SYN 有去无回）。
    if (state.nbl_pool == nullptr)
    {
        ExFreePoolWithTag(storage, injection_pool_tag);
        return STATUS_INVALID_DEVICE_STATE;
    }

    PMDL mdl = NdisAllocateMdl(state.nbl_pool, storage, frame_length);
    if (mdl == nullptr)
    {
        ExFreePoolWithTag(storage, injection_pool_tag);
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    NET_BUFFER_LIST* injected = nullptr;
    NTSTATUS status = FwpsAllocateNetBufferAndNetBufferList0(state.nbl_pool, 0, 0, mdl, 0, frame_length, &injected);
    if (!NT_SUCCESS(status) || injected == nullptr)
    {
        NdisFreeMdl(mdl);
        ExFreePoolWithTag(storage, injection_pool_tag);
        return NT_SUCCESS(status) ? STATUS_INSUFFICIENT_RESOURCES : status;
    }

    const UINT16 layer = outbound ? static_cast<UINT16>(FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE)
                                  : static_cast<UINT16>(FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE);
    InterlockedIncrement64(&state.outstanding_injections);
    status = outbound ? FwpsInjectMacSendAsync0(state.injection_handle,
                                                nullptr,
                                                0,
                                                layer,
                                                interface_index,
                                                ndis_port,
                                                injected,
                                                injection_complete,
                                                nullptr)
                      : FwpsInjectMacReceiveAsync0(state.injection_handle,
                                                   nullptr,
                                                   0,
                                                   layer,
                                                   interface_index,
                                                   ndis_port,
                                                   injected,
                                                   injection_complete,
                                                   nullptr);
    if (!NT_SUCCESS(status))
    {
        InterlockedDecrement64(&state.outstanding_injections);
        // 注入 API 拒绝：NBL 仍归调用方，按 FwpsAllocate 的对称路径释放。
        FwpsFreeNetBufferList0(injected);
        NdisFreeMdl(mdl);
        ExFreePoolWithTag(storage, injection_pool_tag);
    }
    return status;
}

} // namespace winforward::wfp::probe
