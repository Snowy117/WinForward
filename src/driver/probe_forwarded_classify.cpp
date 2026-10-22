#include "probe_driver.h"
#include "probe_forwarded_path.h"
#include "probe_forwarded_state.h"

namespace winforward::wfp::probe
{
namespace
{

constexpr std::uint8_t tcp_flag_fin = 0x01;
constexpr std::uint8_t tcp_flag_rst = 0x04;

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

    // 回程 SNAT：listener 进程发出的 TCP 帧，源端口等于 listener_port。MAC 层一个 NBL 可以带多个
    // NET_BUFFER（每帧一个），必须逐帧查询/改写/注入；原 NBL 整体 block。计数按 NBL 记，
    // 与 outbound_frames 的「一次 classify = 一个 NBL」口径一致。
    if (outbound && facts.protocol == 6 && facts.tcp_length != 0)
    {
        probe_forwarded_config config{};
        {
            KIRQL old_irql{};
            const bool raised = forwarded_lock(old_irql);
            config = state.config;
            forwarded_unlock(raised, old_irql);
        }

        const bool configured = config.enabled != 0 && config.listener_port != 0;
        NET_BUFFER_LIST* const nbl = static_cast<NET_BUFFER_LIST*>(layer_data);
        std::uint32_t buffer_count = 0;
        std::uint32_t injected = 0;
        std::uint32_t failed = 0;
        std::uint32_t candidates = 0;
        std::uint32_t missed = 0;
        bool closing = false;
        forwarded_frame_facts closing_facts{};

        for (NET_BUFFER* buffer = nbl != nullptr ? NET_BUFFER_LIST_FIRST_NB(nbl) : nullptr; buffer != nullptr;
             buffer = NET_BUFFER_NEXT_NB(buffer))
        {
            ++buffer_count;
            forwarded_frame_facts buffer_facts{};
            if (probe_forwarded_parse_frame(buffer, buffer_facts) != forwarded_frame_kind::ip ||
                buffer_facts.protocol != 6 || buffer_facts.tcp_length == 0 ||
                buffer_facts.source_port != config.listener_port)
            {
                continue;
            }

            ++candidates;

            forwarded_redirect_snapshot return_entry{};
            KIRQL old_irql{};
            const bool raised = forwarded_lock(old_irql);
            const forwarded_redirect_lookup lookup = forwarded_redirect_find_return(state.redirects,
                                                                                    buffer_facts.family,
                                                                                    config.listener_port,
                                                                                    buffer_facts.destination_address,
                                                                                    buffer_facts.destination_port,
                                                                                    return_entry);
            if (lookup == forwarded_redirect_lookup::expired)
            {
                ++state.stats.redirect_expired;
            }
            forwarded_unlock(raised, old_irql);
            if (lookup != forwarded_redirect_lookup::found)
            {
                ++missed;
                continue;
            }

            forwarded_frame_rewrite rewrite{};
            rewrite.source_address = return_entry.server_address;
            rewrite.rewrite_source_port = true;
            rewrite.source_port = return_entry.server_port;
            rewrite.source_mac = return_entry.local_mac;
            rewrite.destination_mac = return_entry.client_mac;

            const NTSTATUS status = probe_forwarded_inject_frame(
                buffer, buffer_facts, true, return_entry.ingress_ifindex, ndis_port, rewrite);
            if (NT_SUCCESS(status))
            {
                ++injected;
                if (tcp_is_fin_or_rst(buffer_facts))
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

        if (buffer_count > 1)
        {
            delta.redirect_multi_nb = 1;
        }

        if (injected > 0)
        {
            delta.redirect_return = 1;
            delta.redirected = 1;
            if (failed > 0)
            {
                delta.redirect_inject_failed = failed;
            }
            if (closing)
            {
                mark_forwarded_redirect_closing(state,
                                                closing_facts.family,
                                                config.listener_port,
                                                closing_facts.destination_address,
                                                closing_facts.destination_port);
            }
            release_forwarded_snapshot(table);
            publish_forwarded_counters(state, delta);
            set_frame_action(classify_out, true);
            return;
        }

        if (configured && failed > 0)
        {
            // 所有命中表项的回程帧都没能排队注入：原帧已 block，必须显式计入失败。
            delta.redirect_return_failed = 1;
            delta.blocked = 1;
            release_forwarded_snapshot(table);
            publish_forwarded_counters(state, delta);
            set_frame_action(classify_out, true);
            return;
        }

        if (configured && candidates > 0 && missed > 0)
        {
            delta.redirect_return_miss = 1;
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

        const bool usable_tcp = facts.protocol == 6 && facts.tcp_length != 0;
        if (inbound && usable_tcp)
        {
            const forwarded_inbound_plan plan =
                plan_forwarded_inbound(state, facts, interface_index, ndis_port, result.upstream_id, result.rule_index);
            if (plan.proceed)
            {
                if (plan.created)
                {
                    delta.redirect_created = 1;
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

                for (NET_BUFFER* buffer = ingress_nbl != nullptr ? NET_BUFFER_LIST_FIRST_NB(ingress_nbl) : nullptr;
                     buffer != nullptr;
                     buffer = NET_BUFFER_NEXT_NB(buffer))
                {
                    ++buffer_count;
                    forwarded_frame_facts buffer_facts{};
                    if (probe_forwarded_parse_frame(buffer, buffer_facts) != forwarded_frame_kind::ip ||
                        buffer_facts.protocol != 6 || buffer_facts.tcp_length == 0)
                    {
                        ++failed;
                        continue;
                    }

                    const NTSTATUS status = probe_forwarded_inject_frame(
                        buffer, buffer_facts, false, plan.entry.ingress_ifindex, plan.entry.ndis_port, rewrite);
                    if (NT_SUCCESS(status))
                    {
                        ++injected;
                        if (tcp_is_fin_or_rst(buffer_facts))
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

                if (buffer_count > 1)
                {
                    delta.redirect_multi_nb = 1;
                }

                if (injected > 0)
                {
                    delta.redirect_ingress = 1;
                    delta.redirected = 1;
                    if (failed > 0)
                    {
                        delta.redirect_inject_failed = failed;
                    }
                    if (closing)
                    {
                        mark_forwarded_redirect_closing(state,
                                                        closing_facts.family,
                                                        plan.listener_port,
                                                        closing_facts.source_address,
                                                        closing_facts.source_port);
                    }
                }
                else
                {
                    delta.redirect_blocked = 1;
                    delta.blocked = 1;
                    delta.redirect_inject_failed = failed > 0 ? failed : 1;
                }
            }
            else
            {
                delta.redirect_blocked = 1;
                delta.blocked = 1;
                if (plan.no_config)
                {
                    delta.redirect_no_config = 1;
                }
                else if (plan.capacity)
                {
                    delta.redirect_capacity = 1;
                }
                else
                {
                    delta.redirect_no_state = 1;
                }
                state.stats.last_status = static_cast<std::uint32_t>(STATUS_NOT_FOUND);
            }
        }
        else
        {
            // 非 TCP 或没有可用 TCP 头：B4b 先 block（UDP 归 B5）；出向命中 redirect 规则同样 fail closed。
            delta.redirect_blocked = 1;
            delta.blocked = 1;
            if (facts.protocol == 6)
            {
                delta.redirect_no_state = 1;
            }
            else
            {
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
