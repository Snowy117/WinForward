#include "probe_driver.h"
#include "probe_forwarded_path.h"
#include "probe_forwarded_return.h"
#include "probe_forwarded_state.h"

namespace winforward::wfp::probe
{
namespace
{

constexpr std::uint8_t tcp_flag_fin = 0x01;
constexpr std::uint8_t tcp_flag_rst = 0x04;

constexpr std::uint8_t ip_protocol_tcp = 6;
constexpr std::uint8_t ip_protocol_udp = 17;

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

[[nodiscard]] std::uint32_t frame_ndis_port(const FWPS_INCOMING_VALUES0* values, bool inbound) noexcept
{
    if (values == nullptr)
    {
        return 0;
    }
    const UINT16 field =
        inbound ? FWPS_FIELD_INBOUND_MAC_FRAME_NATIVE_NDIS_PORT : FWPS_FIELD_OUTBOUND_MAC_FRAME_NATIVE_NDIS_PORT;
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

[[nodiscard]] bool tcp_is_fin_or_rst(const forwarded_frame_facts& facts) noexcept
{
    return (facts.tcp_flags & (tcp_flag_fin | tcp_flag_rst)) != 0;
}

[[nodiscard]] bool has_transport_payload(const forwarded_frame_facts& facts) noexcept
{
    return (facts.protocol == ip_protocol_tcp && facts.transport_length != 0) ||
           (facts.protocol == ip_protocol_udp && facts.transport_length != 0);
}
[[nodiscard]] bool same_forwarded_flow(const forwarded_frame_facts& left, const forwarded_frame_facts& right) noexcept
{
    if (left.family != right.family || left.protocol != right.protocol || left.source_port != right.source_port ||
        left.destination_port != right.destination_port)
    {
        return false;
    }

    const std::uint32_t address_size = left.family == 6 ? 16U : 4U;
    return RtlEqualMemory(left.source_address, right.source_address, address_size) &&
           RtlEqualMemory(left.destination_address, right.destination_address, address_size);
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

    // 防环：本驱动注入的帧会被再次 classify，直接放行并单独计数。
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

    forwarded_frame_facts facts{};
    const forwarded_frame_kind kind =
        probe_forwarded_parse_frame(static_cast<const NET_BUFFER_LIST*>(layer_data), facts);
    if (kind != forwarded_frame_kind::ip)
    {
        release_forwarded_snapshot(table);
        if (kind == forwarded_frame_kind::non_ip)
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

    const std::uint32_t interface_index = frame_interface_index(in_fixed_values, inbound);
    const std::uint32_t ndis_port = frame_ndis_port(in_fixed_values, inbound);

    // UDP 分片：首片带端口，按完整规则匹配；命中 redirect 时记录 (地址, IP ID) 并 block。
    // 后续非首片没有端口，只能按跟随表 block。不能只按“地址空间”判断：permit 规则在
    // redirect 规则前会让该流 fail-open，而 host 打同一 remote IP、不同端口的分片会被误 block。

    if (inbound && facts.protocol == ip_protocol_udp && facts.fragmented)
    {
        std::uint32_t enabled = 0;
        {
            KIRQL old_irql{};
            const bool raised = forwarded_lock(old_irql);
            enabled = state.config.enabled;
            forwarded_unlock(raised, old_irql);
        }

        if (enabled != 0)
        {
            bool block_fragment = false;
            if (facts.first_fragment)
            {
                forwarded_connection_view fragment_connection{};
                fragment_connection.ingress_adapter_id = interface_index;
                RtlCopyMemory(fragment_connection.client_address, facts.source_address, 16);
                RtlCopyMemory(fragment_connection.remote_address, facts.destination_address, 16);
                fragment_connection.remote_port = facts.destination_port;
                fragment_connection.protocol = facts.protocol;
                fragment_connection.family = static_cast<address_family>(facts.family);

                const forwarded_match_result fragment_match = match_forwarded(table->view, fragment_connection);
                if (fragment_match.matched && fragment_match.action == rule_action::redirect)
                {
                    remember_forwarded_fragment_block(state, facts, interface_index);
                    block_fragment = true;
                }
            }
            else
            {
                block_fragment = forwarded_fragment_is_blocked(state, facts, interface_index);
            }

            if (block_fragment)
            {
                delta.blocked = 1;
                delta.udp_blocked = 1;
                delta.udp_fragmented = 1;
                state.stats.last_status = static_cast<std::uint32_t>(STATUS_NOT_SUPPORTED);
                release_forwarded_snapshot(table);
                publish_forwarded_counters(state, delta);
                set_frame_action(classify_out, true);
                return;
            }
        }
    }

    if (outbound && facts.protocol == ip_protocol_tcp && facts.transport_length != 0)
    {
        const forwarded_return_decision decision =
            handle_forwarded_tcp_return(state, facts, layer_data, ndis_port, delta);
        if (decision.handled)
        {
            release_forwarded_snapshot(table);
            publish_forwarded_counters(state, delta);
            set_frame_action(classify_out, decision.block);
            return;
        }
    }

    if (outbound && facts.protocol == ip_protocol_udp)
    {
        const forwarded_return_decision decision =
            handle_forwarded_udp_return(state, facts, layer_data, ndis_port, delta);
        if (decision.handled)
        {
            release_forwarded_snapshot(table);
            publish_forwarded_counters(state, delta);
            set_frame_action(classify_out, decision.block);
            return;
        }
    }

    forwarded_connection_view connection{};
    connection.ingress_adapter_id = interface_index;
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
    connection.family = static_cast<address_family>(facts.family);

    const forwarded_match_result result = match_forwarded(table->view, connection);
    release_forwarded_snapshot(table);

    if (result.matched && result.action == rule_action::redirect)
    {
        delta.matched = 1;
        delta.matched_redirect = 1;

        const bool usable = has_transport_payload(facts);
        if (inbound && usable)
        {
            const forwarded_inbound_plan plan =
                plan_forwarded_inbound(state, facts, interface_index, ndis_port, result.upstream_id, result.rule_index);
            if (plan.proceed)
            {
                if (plan.created)
                {
                    if (facts.protocol == ip_protocol_udp)
                    {
                        delta.udp_created = 1;
                        if (plan.multi_l)
                        {
                            delta.udp_multi_l = 1;
                        }
                    }
                    else
                    {
                        delta.redirect_created = 1;
                        if (plan.multi_l)
                        {
                            delta.redirect_multi_l = 1;
                        }
                    }
                }

                forwarded_frame_rewrite rewrite{};
                rewrite.destination_address = plan.listener_address;
                rewrite.rewrite_destination_port = true;
                rewrite.destination_port = plan.listener_port;

                NET_BUFFER_LIST* const ingress_nbl = static_cast<NET_BUFFER_LIST*>(layer_data);
                std::uint32_t buffer_count = 0;
                std::uint32_t injected = 0;
                std::uint32_t failed = 0;
                bool closing = false;
                forwarded_frame_facts closing_facts{};

                bool mixed_flow = false;
                for (NET_BUFFER* buffer = ingress_nbl != nullptr ? NET_BUFFER_LIST_FIRST_NB(ingress_nbl) : nullptr;
                     buffer != nullptr;
                     buffer = NET_BUFFER_NEXT_NB(buffer))
                {
                    ++buffer_count;
                    forwarded_frame_facts buffer_facts{};
                    if (probe_forwarded_parse_frame(buffer, buffer_facts) != forwarded_frame_kind::ip ||
                        buffer_facts.protocol != facts.protocol || !has_transport_payload(buffer_facts) ||
                        !same_forwarded_flow(buffer_facts, facts))
                    {
                        mixed_flow = true;
                    }
                }

                if (mixed_flow)
                {
                    // NBL 里混入了不同流或不可解析的 NET_BUFFER：不能只改写一部分，整块 fail closed。
                    failed = buffer_count;
                    state.stats.last_status = static_cast<std::uint32_t>(STATUS_INVALID_PARAMETER);
                }
                else
                {
                    for (NET_BUFFER* buffer = ingress_nbl != nullptr ? NET_BUFFER_LIST_FIRST_NB(ingress_nbl) : nullptr;
                         buffer != nullptr;
                         buffer = NET_BUFFER_NEXT_NB(buffer))
                    {
                        forwarded_frame_facts buffer_facts{};
                        if (probe_forwarded_parse_frame(buffer, buffer_facts) != forwarded_frame_kind::ip)
                        {
                            ++failed;
                            continue;
                        }

                        const NTSTATUS status = probe_forwarded_inject_frame(
                            buffer, buffer_facts, false, plan.entry.ingress_ifindex, plan.entry.ndis_port, rewrite);
                        if (NT_SUCCESS(status))
                        {
                            ++injected;
                            if (facts.protocol == ip_protocol_tcp && tcp_is_fin_or_rst(buffer_facts))
                            {
                                closing = true;
                                closing_facts = buffer_facts;
                            }
                        }
                        else
                        {
                            ++failed;
                            state.stats.last_status = static_cast<std::uint32_t>(status);
                        }
                    }
                }

                if (buffer_count > 1)
                {
                    if (facts.protocol == ip_protocol_udp)
                    {
                        delta.udp_multi_nb = 1;
                    }
                    else
                    {
                        delta.redirect_multi_nb = 1;
                    }
                }

                if (injected > 0 && failed == 0)
                {
                    if (facts.protocol == ip_protocol_udp)
                    {
                        delta.udp_ingress = 1;
                    }
                    else
                    {
                        delta.redirect_ingress = 1;
                    }
                    delta.redirected = 1;
                    if (closing)
                    {
                        mark_forwarded_redirect_closing(state,
                                                        closing_facts.family,
                                                        ip_protocol_tcp,
                                                        plan.listener_address,
                                                        plan.listener_port,

                                                        closing_facts.source_address,
                                                        closing_facts.source_port);
                    }
                }
                else
                {
                    delta.blocked = 1;
                    if (facts.protocol == ip_protocol_udp)
                    {
                        delta.udp_blocked = 1;
                        delta.udp_inject_failed = failed > 0 ? failed : 1;
                    }
                    else
                    {
                        delta.redirect_blocked = 1;
                        delta.redirect_inject_failed = failed > 0 ? failed : 1;
                    }
                }
            }
            else
            {
                delta.blocked = 1;
                if (facts.protocol != ip_protocol_udp)
                {
                    delta.redirect_blocked = 1;
                }

                if (facts.protocol == ip_protocol_udp)
                {
                    delta.udp_blocked = 1;
                    if (plan.no_config)
                    {
                        delta.udp_no_config = 1;
                    }
                    else if (plan.no_ingress)
                    {
                        delta.udp_no_ingress = 1;
                    }
                    else if (plan.capacity)
                    {
                        delta.udp_capacity = 1;
                    }
                    else
                    {
                        delta.udp_no_state = 1;
                    }
                }
                else if (plan.no_config)
                {
                    delta.redirect_no_config = 1;
                }
                else if (plan.no_ingress)
                {
                    delta.redirect_no_ingress = 1;
                }
                else if (plan.capacity)
                {
                    delta.redirect_capacity = 1;
                }
                else
                {
                    delta.redirect_no_state = 1;
                }
                if (facts.protocol != ip_protocol_udp)
                {
                    delta.redirect_blocked = 1;
                }
                state.stats.last_status = static_cast<std::uint32_t>(STATUS_NOT_FOUND);
            }
        }
        else
        {
            // 非 TCP/UDP、没有可用传输层头，或出向帧命中 redirect 规则：不能安全改写，fail closed。
            delta.blocked = 1;
            if (facts.protocol == ip_protocol_udp)
            {
                delta.udp_blocked = 1;
                delta.udp_no_state = 1;
            }
            else if (facts.protocol == ip_protocol_tcp)
            {
                delta.redirect_blocked = 1;
                delta.redirect_no_state = 1;
            }
            else
            {
                delta.redirect_blocked = 1;
                delta.redirect_non_tcp = 1;
            }
            state.stats.last_status = static_cast<std::uint32_t>(STATUS_NOT_SUPPORTED);
        }

        publish_forwarded_counters(state, delta);
        set_frame_action(classify_out, true);
        return;
    }

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
