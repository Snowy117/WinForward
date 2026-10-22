#include "probe_forwarded_path.h"

#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{
constexpr std::uint8_t tcp_flag_syn = 0x02;
constexpr std::uint8_t tcp_flag_ack = 0x10;
[[nodiscard]] bool tcp_is_syn_only(const forwarded_frame_facts& facts) noexcept
{
    return (facts.tcp_flags & tcp_flag_syn) != 0 && (facts.tcp_flags & tcp_flag_ack) == 0;
}

} // namespace

// 入口 DNAT：查/建 redirect 表。表操作与 config 读取在同一把锁内完成；注入在锁外。
forwarded_inbound_plan plan_forwarded_inbound(forwarded_state& state,
                                              const forwarded_frame_facts& facts,
                                              std::uint32_t ingress_ifindex,
                                              std::uint32_t ndis_port,
                                              std::uint32_t upstream_id,
                                              std::uint32_t rule_index) noexcept
{
    forwarded_inbound_plan plan{};
    RtlZeroMemory(&plan, sizeof(plan));

    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    const probe_forwarded_config config = state.config;
    RtlCopyMemory(plan.listener_address, config.listener_address, 16);
    plan.listener_port = config.listener_port;
    const bool configured = config.enabled != 0 && config.listener_port != 0 && config.family == facts.family;
    if (!configured)
    {
        plan.no_config = true;
    }
    else
    {
        forwarded_redirect_lookup lookup = forwarded_redirect_find_ingress(state.redirects,
                                                                           ingress_ifindex,
                                                                           facts.family,
                                                                           facts.source_address,
                                                                           facts.source_port,
                                                                           facts.destination_address,
                                                                           facts.destination_port,
                                                                           plan.entry);
        if (lookup == forwarded_redirect_lookup::expired)
        {
            ++state.stats.redirect_expired;
        }
        if (lookup == forwarded_redirect_lookup::found)
        {
            plan.proceed = true;
        }
        else if (facts.protocol == 6 && facts.tcp_length != 0 && tcp_is_syn_only(facts))
        {
            forwarded_redirect_request create{};
            create.ingress_ifindex = ingress_ifindex;
            create.ndis_port = ndis_port;
            create.upstream_id = upstream_id;
            create.rule_index = rule_index;
            create.generation = state.generation;
            create.family = facts.family;
            create.client_port = facts.source_port;
            create.server_port = facts.destination_port;
            create.listener_port = config.listener_port;
            RtlCopyMemory(create.client_mac, facts.source_mac, 6);
            RtlCopyMemory(create.local_mac, facts.destination_mac, 6);
            RtlCopyMemory(create.client_address, facts.source_address, 16);
            RtlCopyMemory(create.server_address, facts.destination_address, 16);
            plan.proceed = forwarded_redirect_create(state.redirects, create, plan.entry);
            if (plan.proceed)
            {
                plan.created = true;
            }
            else
            {
                plan.capacity = true;
            }
        }
        else
        {
            plan.no_state = true;
        }
    }
    forwarded_unlock(raised, old_irql);
    return plan;
}

void mark_forwarded_redirect_closing(forwarded_state& state,
                                     std::uint8_t family,
                                     std::uint16_t listener_port,
                                     const std::uint8_t* client_address,
                                     std::uint16_t client_port) noexcept
{
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    (void)forwarded_redirect_mark_closing(state.redirects, family, listener_port, client_address, client_port);
    forwarded_unlock(raised, old_irql);
}

} // namespace winforward::wfp::probe
