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

// 同一入口可以有多条 listener 记录（同一端口绑多个 L）。优先挑候选回程 key 空闲的那个；
// 全部被占用时退回第一条（客户端端口复用，create 会替换旧表项）。精确 ifindex 记录优先于
// 0xFFFFFFFF 通配记录；只有完全没有精确记录时才使用通配记录。
[[nodiscard]] const forwarded_runtime_endpoint* select_forwarded_endpoint(forwarded_state& state,
                                                                          const forwarded_runtime_config& config,
                                                                          std::uint32_t ingress_ifindex,
                                                                          std::uint8_t family,
                                                                          std::uint8_t protocol,
                                                                          const std::uint8_t* client_address,
                                                                          std::uint16_t client_port,
                                                                          bool& multi_l) noexcept
{
    for (std::uint32_t pass = 0; pass < 2; ++pass)
    {
        const forwarded_runtime_endpoint* first = nullptr;
        const forwarded_runtime_endpoint* free = nullptr;
        for (std::uint32_t index = 0; index < config.ingress_count; ++index)
        {
            const forwarded_runtime_endpoint& endpoint = config.ingresses[index];
            if (endpoint.family != family)
            {
                continue;
            }
            const bool matches =
                pass == 0 ? endpoint.ifindex == ingress_ifindex : endpoint.ifindex == forwarded_ingress_any_ifindex;
            if (!matches)
            {
                continue;
            }
            if (first == nullptr)
            {
                first = &endpoint;
            }
            if (free == nullptr && !forwarded_redirect_has_return(state.redirects,
                                                                  family,
                                                                  protocol,
                                                                  endpoint.listener_address,
                                                                  endpoint.listener_port,
                                                                  client_address,
                                                                  client_port))
            {
                free = &endpoint;
            }
        }

        if (first != nullptr)
        {
            const forwarded_runtime_endpoint* chosen = free != nullptr ? free : first;
            multi_l = chosen != first;
            return chosen;
        }
    }
    return nullptr;
}

} // namespace

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
    const forwarded_runtime_config config = state.config;
    const forwarded_runtime_endpoint* endpoint =
        config.enabled != 0 ? find_forwarded_endpoint(config, ingress_ifindex, facts.family) : nullptr;
    if (config.enabled == 0)
    {
        plan.no_config = true;
    }
    else if (endpoint == nullptr)
    {
        plan.no_ingress = true;
    }
    else
    {
        forwarded_redirect_lookup lookup = forwarded_redirect_find_ingress(state.redirects,
                                                                           ingress_ifindex,
                                                                           facts.family,
                                                                           facts.protocol,
                                                                           facts.source_address,
                                                                           facts.source_port,
                                                                           facts.destination_address,
                                                                           facts.destination_port,
                                                                           plan.entry);
        if (lookup == forwarded_redirect_lookup::expired)
        {
            if (facts.protocol == 17)
            {
                ++state.stats.udp_expired;
            }
            else
            {
                ++state.stats.redirect_expired;
            }
        }
        if (lookup == forwarded_redirect_lookup::found)
        {
            plan.proceed = true;
        }
        else if ((facts.protocol == 6 && facts.transport_length != 0 && tcp_is_syn_only(facts)) || facts.protocol == 17)
        {
            bool multi_l = false;
            const forwarded_runtime_endpoint* chosen = select_forwarded_endpoint(state,
                                                                                 config,
                                                                                 ingress_ifindex,
                                                                                 facts.family,
                                                                                 facts.protocol,
                                                                                 facts.source_address,
                                                                                 facts.source_port,
                                                                                 multi_l);
            if (chosen == nullptr)
            {
                plan.no_ingress = true;
            }
            else
            {
                forwarded_redirect_request create{};
                create.ingress_ifindex = ingress_ifindex;
                create.ndis_port = ndis_port;
                create.upstream_id = upstream_id;
                create.rule_index = rule_index;
                create.generation = state.generation;
                create.family = facts.family;
                create.protocol = facts.protocol;
                create.client_port = facts.source_port;
                create.server_port = facts.destination_port;
                create.listener_port = chosen->listener_port;
                RtlCopyMemory(create.client_mac, facts.source_mac, 6);
                RtlCopyMemory(create.local_mac, facts.destination_mac, 6);
                RtlCopyMemory(create.client_address, facts.source_address, 16);
                RtlCopyMemory(create.server_address, facts.destination_address, 16);
                RtlCopyMemory(create.listener_address, chosen->listener_address, 16);
                plan.proceed = forwarded_redirect_create(state.redirects, create, plan.entry);
                if (plan.proceed)
                {
                    plan.created = true;
                    plan.multi_l = multi_l;
                }
                else
                {
                    plan.capacity = true;
                }
            }
        }
        else
        {
            plan.no_state = true;
        }
    }

    if (plan.proceed)
    {
        RtlCopyMemory(plan.listener_address, plan.entry.listener_address, 16);
        plan.listener_port = plan.entry.listener_port;
    }
    forwarded_unlock(raised, old_irql);
    return plan;
}

void mark_forwarded_redirect_closing(forwarded_state& state,
                                     std::uint8_t family,
                                     std::uint8_t protocol,
                                     const std::uint8_t* listener_address,
                                     std::uint16_t listener_port,
                                     const std::uint8_t* client_address,
                                     std::uint16_t client_port) noexcept
{
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    (void)forwarded_redirect_mark_closing(
        state.redirects, family, protocol, listener_address, listener_port, client_address, client_port);
    forwarded_unlock(raised, old_irql);
}

} // namespace winforward::wfp::probe
