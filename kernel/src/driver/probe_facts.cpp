#include "probe_facts.h"

#include "v4_mapped.h"

namespace winforward::wfp::probe
{
namespace
{

[[nodiscard]] bool is_ipv6_layer(const FWPS_INCOMING_VALUES0* values) noexcept
{
    return values->layerId == FWPS_LAYER_ALE_CONNECT_REDIRECT_V6;
}

[[nodiscard]] bool is_redirect_layer(const FWPS_INCOMING_VALUES0* values) noexcept
{
    return values->layerId == FWPS_LAYER_ALE_CONNECT_REDIRECT_V4 ||
           values->layerId == FWPS_LAYER_ALE_CONNECT_REDIRECT_V6;
}

[[nodiscard]] std::uint8_t field_u8(const FWPS_INCOMING_VALUES0* values, UINT16 index) noexcept
{
    if (index >= values->valueCount)
    {
        return 0;
    }
    const FWP_VALUE0& value = values->incomingValue[index].value;
    return value.type == FWP_UINT8 ? value.uint8 : 0;
}

[[nodiscard]] std::uint32_t field_u32(const FWPS_INCOMING_VALUES0* values, UINT16 index) noexcept
{
    if (index >= values->valueCount)
    {
        return 0;
    }
    const FWP_VALUE0& value = values->incomingValue[index].value;
    return value.type == FWP_UINT32 ? value.uint32 : 0;
}

[[nodiscard]] UINT16 host_port(const SOCKADDR* address) noexcept
{
    if (address->sa_family == AF_INET6)
    {
        return RtlUshortByteSwap(reinterpret_cast<const SOCKADDR_IN6*>(address)->sin6_port);
    }
    return RtlUshortByteSwap(reinterpret_cast<const SOCKADDR_IN*>(address)->sin_port);
}

void copy_address(const SOCKADDR* address, std::uint8_t* target) noexcept
{
    if (address->sa_family == AF_INET6)
    {
        RtlCopyMemory(target, reinterpret_cast<const SOCKADDR_IN6*>(address)->sin6_addr.s6_addr, 16);
        return;
    }
    RtlCopyMemory(target, &reinterpret_cast<const SOCKADDR_IN*>(address)->sin_addr, 4);
}

// 双模客户端连 IPv4 字面量时，V6 层给出 ::ffff:a.b.c.d；包路径看到的是 IPv4 报文，matcher 与
// redirect context 的原始目的地也必须按 IPv4 语义取值。raw family / remote_address 保持 WFP
// 原值：probe_event 诊断与 redirect target（listener 所在层）都依赖它们。
void assign_matcher_facts(connection_facts& facts) noexcept
{
    facts.matcher_family = facts.family == AF_INET6 ? rule_family_ipv6 : rule_family_ipv4;
    RtlCopyMemory(facts.matcher_remote_address, facts.remote_address, sizeof(facts.matcher_remote_address));

    std::uint8_t normalized_family = 0;
    std::uint8_t normalized_address[16] = {};
    if (try_normalize_v4_mapped(
            facts.matcher_family, facts.matcher_remote_address, normalized_family, normalized_address))
    {
        facts.matcher_family = normalized_family;
        RtlCopyMemory(facts.matcher_remote_address, normalized_address, sizeof(facts.matcher_remote_address));
    }
}

} // namespace

bool gather_facts(const FWPS_INCOMING_VALUES0* values,
                  const FWPS_INCOMING_METADATA_VALUES0* metadata,
                  void* layer_data,
                  connection_facts& facts) noexcept
{
    if (values == nullptr || layer_data == nullptr || !is_redirect_layer(values))
    {
        return false;
    }

    RtlZeroMemory(&facts, sizeof(facts));
    const bool ipv6 = is_ipv6_layer(values);
    const auto* request = static_cast<const FWPS_CONNECT_REQUEST0*>(layer_data);
    RtlCopyMemory(&facts.remote, &request->remoteAddressAndPort, sizeof(SOCKADDR_STORAGE));
    RtlCopyMemory(&facts.local, &request->localAddressAndPort, sizeof(SOCKADDR_STORAGE));

    const auto* remote = reinterpret_cast<const SOCKADDR*>(&facts.remote);
    const auto* local = reinterpret_cast<const SOCKADDR*>(&facts.local);
    if (ipv6)
    {
        if (remote->sa_family != AF_INET6 || local->sa_family != AF_INET6)
        {
            return false;
        }
    }
    else if (remote->sa_family != AF_INET || local->sa_family != AF_INET)
    {
        return false;
    }

    facts.family = static_cast<std::uint8_t>(remote->sa_family);
    facts.remote_port = host_port(remote);
    facts.local_port = host_port(local);
    copy_address(remote, facts.remote_address);
    copy_address(local, facts.local_address);
    assign_matcher_facts(facts);

    const UINT16 protocol_field =
        ipv6 ? FWPS_FIELD_ALE_CONNECT_REDIRECT_V6_IP_PROTOCOL : FWPS_FIELD_ALE_CONNECT_REDIRECT_V4_IP_PROTOCOL;
    const UINT16 flags_field =
        ipv6 ? FWPS_FIELD_ALE_CONNECT_REDIRECT_V6_FLAGS : FWPS_FIELD_ALE_CONNECT_REDIRECT_V4_FLAGS;
    const UINT16 app_id_field =
        ipv6 ? FWPS_FIELD_ALE_CONNECT_REDIRECT_V6_ALE_APP_ID : FWPS_FIELD_ALE_CONNECT_REDIRECT_V4_ALE_APP_ID;
    facts.protocol = field_u8(values, protocol_field);
    facts.condition_flags = field_u32(values, flags_field);

    if (metadata != nullptr && FWPS_IS_METADATA_FIELD_PRESENT(metadata, FWPS_METADATA_FIELD_PROCESS_ID))
    {
        facts.process_id = static_cast<UINT32>(metadata->processId);
    }
    if (metadata != nullptr && FWPS_IS_METADATA_FIELD_PRESENT(metadata, FWPS_METADATA_FIELD_PROCESS_PATH))
    {
        // 诊断计数：官方 metadata 表说 redirect 层没有 PROCESS_PATH，实测应为 0。
        probe_increment(probe_state_ref().stats.process_path_metadata_seen);
    }

    if (app_id_field < values->valueCount)
    {
        const FWP_VALUE0& value = values->incomingValue[app_id_field].value;
        if (value.type == FWP_BYTE_BLOB_TYPE && value.byteBlob != nullptr)
        {
            collect_process_path(
                value.byteBlob->data, value.byteBlob->size, facts.process_path, facts.process_path_length);
        }
    }
    return true;
}

} // namespace winforward::wfp::probe
