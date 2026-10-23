#pragma once

#include <cstdint>

namespace winforward::wfp::probe
{

inline constexpr std::uint8_t ip_protocol_tcp = 6;
inline constexpr std::uint8_t ip_protocol_udp = 17;

// 校验和只用算术，不依赖 WDK；放在头里让帧解析/注入与将来的其他数据面共用同一份实现。
[[nodiscard]] inline std::uint32_t
checksum_add(std::uint32_t sum, const std::uint8_t* data, std::uint32_t length) noexcept
{
    std::uint32_t index = 0;
    for (; index + 1 < length; index += 2)
    {
        sum += (static_cast<std::uint32_t>(data[index]) << 8) | data[index + 1];
    }
    if ((length & 1U) != 0)
    {
        sum += static_cast<std::uint32_t>(data[length - 1]) << 8;
    }
    return sum;
}

[[nodiscard]] inline std::uint16_t checksum_fold(std::uint32_t sum) noexcept
{
    while ((sum >> 16) != 0)
    {
        sum = (sum & 0xFFFFU) + (sum >> 16);
    }
    return static_cast<std::uint16_t>(~sum & 0xFFFFU);
}

[[nodiscard]] inline std::uint16_t ipv4_header_checksum(const std::uint8_t* header, std::uint32_t length) noexcept
{
    return checksum_fold(checksum_add(0, header, length));
}

[[nodiscard]] inline std::uint16_t
tcp_checksum_ipv4(const std::uint8_t* ip, const std::uint8_t* tcp, std::uint32_t length) noexcept
{
    std::uint32_t sum = 0;
    sum = checksum_add(sum, ip + 12, 8); // source + destination
    sum += ip_protocol_tcp;
    sum += length;
    sum = checksum_add(sum, tcp, length);
    return checksum_fold(sum);
}

[[nodiscard]] inline std::uint16_t
tcp_checksum_ipv6(const std::uint8_t* ip, const std::uint8_t* tcp, std::uint32_t length) noexcept
{
    std::uint32_t sum = 0;
    sum = checksum_add(sum, ip + 8, 32); // source + destination
    sum += (length >> 16) & 0xFFFFU;
    sum += length & 0xFFFFU;
    sum += ip_protocol_tcp;
    sum = checksum_add(sum, tcp, length);
    return checksum_fold(sum);
}

[[nodiscard]] inline std::uint16_t
udp_checksum_ipv4(const std::uint8_t* ip, const std::uint8_t* udp, std::uint32_t length) noexcept
{
    std::uint32_t sum = 0;
    sum = checksum_add(sum, ip + 12, 8); // source + destination
    sum += ip_protocol_udp;
    sum += length;
    sum = checksum_add(sum, udp, length);
    return checksum_fold(sum);
}

[[nodiscard]] inline std::uint16_t
udp_checksum_ipv6(const std::uint8_t* ip, const std::uint8_t* udp, std::uint32_t length) noexcept
{
    std::uint32_t sum = 0;
    sum = checksum_add(sum, ip + 8, 32); // source + destination
    sum += (length >> 16) & 0xFFFFU;
    sum += length & 0xFFFFU;
    sum += ip_protocol_udp;
    sum = checksum_add(sum, udp, length);
    return checksum_fold(sum);
}

} // namespace winforward::wfp::probe
