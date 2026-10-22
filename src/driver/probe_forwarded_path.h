#pragma once

#include "probe_driver.h"
#include "probe_forwarded_frame.h"
#include "probe_forwarded_redirect.h"
#include "probe_forwarded_state.h"

namespace winforward::wfp::probe
{

struct forwarded_inbound_plan
{
    bool proceed;
    bool no_config;
    bool no_state;
    bool capacity;
    bool created;
    std::uint8_t listener_address[16];
    std::uint16_t listener_port;
    forwarded_redirect_snapshot entry;
};

[[nodiscard]] forwarded_inbound_plan plan_forwarded_inbound(forwarded_state& state,
                                                            const forwarded_frame_facts& facts,
                                                            std::uint32_t ingress_ifindex,
                                                            std::uint32_t ndis_port,
                                                            std::uint32_t upstream_id,
                                                            std::uint32_t rule_index) noexcept;

void mark_forwarded_redirect_closing(forwarded_state& state,
                                     std::uint8_t family,
                                     std::uint16_t listener_port,
                                     const std::uint8_t* client_address,
                                     std::uint16_t client_port) noexcept;

} // namespace winforward::wfp::probe
