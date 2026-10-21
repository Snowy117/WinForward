#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

constexpr std::uint32_t invalid_rule_index = 0xFFFFFFFFU;

void copy_address(const SOCKADDR* address, std::uint8_t* target, std::uint32_t& length) noexcept
{
    if (address->sa_family == AF_INET6)
    {
        RtlCopyMemory(target, reinterpret_cast<const SOCKADDR_IN6*>(address)->sin6_addr.s6_addr, 16);
        length = 16;
        return;
    }
    RtlCopyMemory(target, &reinterpret_cast<const SOCKADDR_IN*>(address)->sin_addr, 4);
    length = 4;
}

void finish_redirect(UINT64 classify_handle, void* writable_data, FWPS_CLASSIFY_OUT0* classify_out) noexcept
{
    classify_out->actionType = FWP_ACTION_PERMIT;
    classify_out->rights &= ~FWPS_RIGHT_ACTION_WRITE;
    FwpsApplyModifiedLayerData0(classify_handle, writable_data, 0);
    FwpsReleaseClassifyHandle0(classify_handle);
}

void log_redirect(std::uint32_t process_id,
                  const SOCKADDR_STORAGE& local,
                  const SOCKADDR_STORAGE& remote,
                  std::uint16_t local_port,
                  std::uint16_t remote_port,
                  std::uint16_t redirect_port,
                  std::uint8_t family,
                  redirect_branch branch) noexcept
{
    probe_event event{};
    event.timestamp_100ns = KeQueryInterruptTime();
    event.process_id = process_id;
    std::uint32_t length = 0;
    const auto* remote_address = reinterpret_cast<const SOCKADDR*>(&remote);
    const auto* local_address = reinterpret_cast<const SOCKADDR*>(&local);
    copy_address(remote_address, event.remote_address, length);
    copy_address(local_address, event.local_address, length);
    event.remote_port = remote_port;
    event.local_port = local_port;
    event.redirect_port = redirect_port;
    event.decision = static_cast<std::uint8_t>(probe_decision::redirected);
    event.family = family;
    event.action = static_cast<std::uint8_t>(probe_action::redirect);
    event.branch = static_cast<std::uint8_t>(branch);
    event.matched_rule = invalid_rule_index;
    probe_log_event(event);
}

} // namespace

bool udp_probe_try_redirect(const SOCKADDR_STORAGE& local,
                            const SOCKADDR_STORAGE& remote,
                            std::uint8_t family,
                            std::uint32_t process_id,
                            const void* classify_context,
                            const FWPS_FILTER1* filter,
                            FWPS_CLASSIFY_OUT0* classify_out) noexcept
{
    udp_probe_note_seen();

    const std::uint16_t remote_port = RtlUshortByteSwap(reinterpret_cast<const SOCKADDR_IN*>(&remote)->sin_port);
    const std::uint16_t port_filter = udp_probe_remote_port_filter();
    if (port_filter != 0 && remote_port != port_filter)
    {
        return false;
    }

    const std::uint32_t listener_pid = udp_probe_listener_pid();
    const std::uint16_t listener_port = udp_probe_listener_port();
    probe_state& state = probe_state_ref();
    if (listener_pid == 0 || listener_port == 0 || state.redirect_handle == nullptr || !udp_probe_map_has_room())
    {
        udp_probe_note_redirect(false);
        return false;
    }

    SOCKADDR_STORAGE target{};
    redirect_branch branch = redirect_branch::none;
    const auto* remote_address = reinterpret_cast<const SOCKADDR*>(&remote);
    const auto* local_address = reinterpret_cast<const SOCKADDR*>(&local);
    if (udp_probe_redirect_target() != 0)
    {
        std::uint8_t address[16]{};
        if (udp_probe_target_address(address))
        {
            RtlZeroMemory(&target, sizeof(target));
            target.ss_family = remote_address->sa_family;
            if (remote_address->sa_family == AF_INET6)
            {
                auto* target6 = reinterpret_cast<SOCKADDR_IN6*>(&target);
                RtlCopyMemory(target6->sin6_addr.s6_addr, address, 16);
                target6->sin6_port = RtlUshortByteSwap(listener_port);
            }
            else
            {
                auto* target4 = reinterpret_cast<SOCKADDR_IN*>(&target);
                RtlCopyMemory(&target4->sin_addr, address, 4);
                target4->sin_port = RtlUshortByteSwap(listener_port);
            }
            branch = redirect_branch::local_address;
        }
    }
    if (branch == redirect_branch::none)
    {
        probe_build_redirect_target(remote_address, local_address, listener_port, target, branch);
    }

    UINT64 classify_handle = 0;
    NTSTATUS status = FwpsAcquireClassifyHandle0(const_cast<void*>(classify_context), 0, &classify_handle);
    if (!NT_SUCCESS(status))
    {
        udp_probe_note_redirect(false);
        return false;
    }

    void* writable_data = nullptr;
    status = FwpsAcquireWritableLayerDataPointer0(classify_handle, filter->filterId, 0, &writable_data, classify_out);
    if (!NT_SUCCESS(status) || writable_data == nullptr)
    {
        FwpsReleaseClassifyHandle0(classify_handle);
        udp_probe_note_redirect(false);
        return false;
    }

    auto* context =
        static_cast<redirect_context*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(redirect_context), pool_tag));
    if (context == nullptr)
    {
        finish_redirect(classify_handle, writable_data, classify_out);
        probe_increment(state.stats.context_alloc_failures);
        probe_increment(state.stats.redirect_failures);
        probe_increment(state.stats.permitted);
        udp_probe_note_redirect(false);
        return true;
    }

    original_destination destination{};
    destination.family = family == AF_INET6 ? address_family::ipv6 : address_family::ipv4;
    destination.protocol = transport_protocol::udp;
    destination.port = remote_port;
    std::uint32_t address_length = 0;
    copy_address(remote_address, destination.address, address_length);
    *context = make_redirect_context(destination, process_id, invalid_rule_index);

    auto* request = static_cast<FWPS_CONNECT_REQUEST0*>(writable_data);
    request->localRedirectTargetPID = listener_pid;
    request->localRedirectHandle = state.redirect_handle;
    request->localRedirectContext = context;
    request->localRedirectContextSize = sizeof(redirect_context);
    RtlCopyMemory(&request->remoteAddressAndPort, &target, sizeof(target));

    const bool mapped = udp_probe_map_insert(local, remote);
    finish_redirect(classify_handle, writable_data, classify_out);
    probe_increment(state.stats.contexts_allocated);
    probe_increment(state.stats.redirected);
    udp_probe_note_redirect(mapped);
    log_redirect(process_id,
                 local,
                 remote,
                 RtlUshortByteSwap(reinterpret_cast<const SOCKADDR_IN*>(&local)->sin_port),
                 remote_port,
                 listener_port,
                 family,
                 branch);
    return true;
}

} // namespace winforward::wfp::probe
