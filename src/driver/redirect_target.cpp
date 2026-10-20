#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

[[nodiscard]] bool address_is_any(const SOCKADDR* address) noexcept
{
    if (address->sa_family == AF_INET6)
    {
        const auto* v6 = reinterpret_cast<const SOCKADDR_IN6*>(address);
        for (std::size_t index = 0; index < 16; ++index)
        {
            if (v6->sin6_addr.s6_addr[index] != 0)
            {
                return false;
            }
        }
        return true;
    }
    return reinterpret_cast<const SOCKADDR_IN*>(address)->sin_addr.S_un.S_addr == 0;
}

} // namespace

void probe_build_redirect_target(const SOCKADDR* remote,
                                 const SOCKADDR* local,
                                 std::uint16_t listener_port,
                                 SOCKADDR_STORAGE& target,
                                 redirect_branch& branch) noexcept
{
    RtlZeroMemory(&target, sizeof(target));
    target.ss_family = remote->sa_family;
    if (remote->sa_family == AF_INET6)
    {
        auto* target6 = reinterpret_cast<SOCKADDR_IN6*>(&target);
        if (address_is_any(local))
        {
            target6->sin6_addr.s6_addr[15] = 1; // ::1
            branch = redirect_branch::any_to_loopback;
        }
        else
        {
            RtlCopyMemory(
                target6->sin6_addr.s6_addr, reinterpret_cast<const SOCKADDR_IN6*>(local)->sin6_addr.s6_addr, 16);
            branch = redirect_branch::local_address;
        }
        target6->sin6_port = RtlUshortByteSwap(listener_port);
        return;
    }

    auto* target4 = reinterpret_cast<SOCKADDR_IN*>(&target);
    if (address_is_any(local))
    {
        target4->sin_addr.S_un.S_addr = 0x0100007Fu; // 127.0.0.1
        branch = redirect_branch::any_to_loopback;
    }
    else
    {
        target4->sin_addr.S_un.S_addr = reinterpret_cast<const SOCKADDR_IN*>(local)->sin_addr.S_un.S_addr;
        branch = redirect_branch::local_address;
    }
    target4->sin_port = RtlUshortByteSwap(listener_port);
}
} // namespace winforward::wfp::probe
