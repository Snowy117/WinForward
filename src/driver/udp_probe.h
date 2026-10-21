#pragma once

#include <cstdint>

namespace winforward::wfp::probe
{

// 实验探针：Host UDP 路线 A。本模块不是正式 ABI，归档时删除或转正。

void udp_probe_start(HANDLE engine_handle) noexcept;
void udp_probe_stop() noexcept;

void udp_probe_apply_config(const udp_probe_config& config) noexcept;
void udp_probe_snapshot(udp_probe_stats& stats) noexcept;

[[nodiscard]] bool udp_probe_enabled() noexcept;
[[nodiscard]] std::uint32_t udp_probe_listener_pid() noexcept;
[[nodiscard]] std::uint16_t udp_probe_listener_port() noexcept;
[[nodiscard]] std::uint16_t udp_probe_remote_port_filter() noexcept;
[[nodiscard]] std::uint8_t udp_probe_redirect_target() noexcept;
[[nodiscard]] bool udp_probe_target_address(std::uint8_t* address) noexcept;
[[nodiscard]] bool udp_probe_map_has_room() noexcept;
[[nodiscard]] bool udp_probe_map_insert(const SOCKADDR_STORAGE& local, const SOCKADDR_STORAGE& remote) noexcept;

[[nodiscard]] bool udp_probe_try_redirect(const SOCKADDR_STORAGE& local,
                                          const SOCKADDR_STORAGE& remote,
                                          std::uint8_t family,
                                          std::uint32_t process_id,
                                          const void* classify_context,
                                          const FWPS_FILTER1* filter,
                                          FWPS_CLASSIFY_OUT0* classify_out) noexcept;
void udp_probe_note_seen() noexcept;
void udp_probe_note_redirect(bool success) noexcept;
void udp_probe_note_ippacket(bool matched) noexcept;
[[nodiscard]] bool udp_probe_map_lookup(std::uint16_t port, std::uint32_t address) noexcept;

void udp_ippacket_start(HANDLE engine_handle) noexcept;
void udp_ippacket_stop() noexcept;
void udp_inject_start() noexcept;
void udp_inject_stop() noexcept;
[[nodiscard]] bool udp_probe_return_packet(const udp_probe_return_header& header, const std::uint8_t* payload) noexcept;
void udp_probe_note_return_status(std::uint32_t status) noexcept;

} // namespace winforward::wfp::probe
