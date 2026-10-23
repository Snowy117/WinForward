#include "probe_forwarded_return.h"

#include "probe_driver.h"
#include "probe_forwarded_path.h"
#include "probe_forwarded_state.h"

namespace winforward::wfp::probe
{
namespace
{

constexpr std::uint8_t ip_protocol_tcp = 6;
constexpr std::uint8_t ip_protocol_udp = 17;

constexpr std::uint8_t tcp_flag_fin = 0x01;
constexpr std::uint8_t tcp_flag_rst = 0x04;

[[nodiscard]] bool tcp_is_fin_or_rst(const forwarded_frame_facts& facts) noexcept
{
    return (facts.tcp_flags & (tcp_flag_fin | tcp_flag_rst)) != 0;
}

[[nodiscard]] probe_forwarded_config snapshot_config(forwarded_state& state) noexcept
{
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    const probe_forwarded_config config = state.config;
    forwarded_unlock(raised, old_irql);
    return config;
}

} // namespace

forwarded_return_decision handle_forwarded_tcp_return(forwarded_state& state,
                                                      const forwarded_frame_facts& facts,
                                                      void* layer_data,
                                                      std::uint32_t ndis_port,
                                                      probe_forwarded_stats& delta) noexcept
{
    if (facts.protocol != ip_protocol_tcp || facts.transport_length == 0)
    {
        return { false, false };
    }

    const probe_forwarded_config config = snapshot_config(state);
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
            buffer_facts.protocol != ip_protocol_tcp || buffer_facts.transport_length == 0 ||
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
                                                                                ip_protocol_tcp,
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

        const NTSTATUS status =
            probe_forwarded_inject_frame(buffer, buffer_facts, true, return_entry.ingress_ifindex, ndis_port, rewrite);
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

    if (injected > 0 && failed == 0)
    {
        delta.redirect_return = 1;
        delta.redirected = 1;
        if (missed > 0)
        {
            // 混合命中+未命中的 NBL：原 NBL 整体 block（fail closed），miss 按 NET_BUFFER 逐个计数。
            delta.redirect_return_miss = missed;
        }

        if (closing)
        {
            mark_forwarded_redirect_closing(state,
                                            closing_facts.family,
                                            ip_protocol_tcp,
                                            config.listener_port,
                                            closing_facts.destination_address,
                                            closing_facts.destination_port);
        }
        return { true, true };
    }

    if (failed > 0)
    {
        // 部分或全部回程帧注入失败：原 NBL 整体 block，按失败计；不能把部分成功当 redirected。
        delta.redirect_return_failed = 1;
        delta.blocked = 1;
        delta.redirect_inject_failed = failed;
        if (missed > 0)
        {
            delta.redirect_return_miss = missed;
        }
        return { true, true };
    }

    if (configured && candidates > 0 && missed > 0)
    {
        // 纯 miss：整个 NBL 按普通本机流量放行，miss 按 NET_BUFFER 逐个计数。
        delta.redirect_return_miss = missed;
    }

    return { false, false };
}

forwarded_return_decision handle_forwarded_udp_return(forwarded_state& state,
                                                      const forwarded_frame_facts& facts,
                                                      void* layer_data,
                                                      std::uint32_t ndis_port,
                                                      probe_forwarded_stats& delta) noexcept
{
    if (facts.protocol != ip_protocol_udp)
    {
        return { false, false };
    }

    const probe_forwarded_config config = snapshot_config(state);
    const bool configured = config.enabled != 0 && config.listener_port != 0;
    if (!configured)
    {
        return { false, false };
    }

    NET_BUFFER_LIST* const nbl = static_cast<NET_BUFFER_LIST*>(layer_data);
    std::uint32_t buffer_count = 0;
    std::uint32_t injected = 0;
    std::uint32_t failed = 0;
    std::uint32_t candidates = 0;

    for (NET_BUFFER* buffer = nbl != nullptr ? NET_BUFFER_LIST_FIRST_NB(nbl) : nullptr; buffer != nullptr;
         buffer = NET_BUFFER_NEXT_NB(buffer))
    {
        ++buffer_count;
        forwarded_frame_facts buffer_facts{};
        if (probe_forwarded_parse_frame(buffer, buffer_facts) != forwarded_frame_kind::ip ||
            buffer_facts.protocol != ip_protocol_udp || buffer_facts.source_port != config.listener_port)
        {
            continue;
        }

        ++candidates;

        forwarded_redirect_snapshot return_entry{};
        KIRQL old_irql{};
        const bool raised = forwarded_lock(old_irql);
        const forwarded_redirect_lookup lookup = forwarded_redirect_find_return(state.redirects,
                                                                                buffer_facts.family,
                                                                                ip_protocol_udp,
                                                                                config.listener_port,
                                                                                buffer_facts.destination_address,
                                                                                buffer_facts.destination_port,
                                                                                return_entry);
        if (lookup == forwarded_redirect_lookup::expired)
        {
            ++state.stats.udp_expired;
        }
        forwarded_unlock(raised, old_irql);
        if (lookup != forwarded_redirect_lookup::found)
        {
            continue;
        }

        forwarded_frame_rewrite rewrite{};
        rewrite.source_address = return_entry.server_address;
        rewrite.rewrite_source_port = true;
        rewrite.source_port = return_entry.server_port;
        rewrite.source_mac = return_entry.local_mac;
        rewrite.destination_mac = return_entry.client_mac;

        const NTSTATUS status =
            probe_forwarded_inject_frame(buffer, buffer_facts, true, return_entry.ingress_ifindex, ndis_port, rewrite);
        if (NT_SUCCESS(status))
        {
            ++injected;
        }
        else
        {
            ++failed;
            state.stats.last_status = static_cast<std::uint32_t>(status);
        }
    }

    if (buffer_count > 1)
    {
        delta.udp_multi_nb = 1;
    }

    if (injected > 0 && failed == 0)
    {
        delta.udp_return = 1;
        delta.redirected = 1;
        return { true, true };
    }

    if (failed > 0)
    {
        delta.udp_return_failed = 1;
        delta.udp_inject_failed = failed;
        delta.blocked = 1;
        return { true, true };
    }

    if (candidates > 0)
    {
        // listener 源端口但不是任何重定向关联的回包：按普通本机流量 permit。
        delta.udp_return_miss = 1;
        delta.permitted = 1;
        return { true, false };
    }
    return { false, false };
}

} // namespace winforward::wfp::probe
