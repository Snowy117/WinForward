#pragma once

#include <cstddef>
#include <cstdint>

#include "redirect_context.h"

// 双模 socket（IPv6 socket + DualMode）连 IPv4 字面量时，WFP 把连接交给
// ALE_CONNECT_REDIRECT_V6，远端地址是 v4-mapped 的 ::ffff:a.b.c.d。这条连接在语义上、以及
// 在包路径眼里都是 IPv4；host classify 必须在交给 matcher 与写 redirect context 之前把它还原
// 成 IPv4，否则 IPv4 网段 / 族规则漏掉它、IPv6 规则反而命中。
//
// 本文件位于 src/common，两侧都会编译，因此遵守内核方言：无 STL、无异常、无 RTTI、无分配，
// DISPATCH_LEVEL 可用。family 用 ABI 与规则 blob 的编码（4 = IPv4、6 = IPv6），不是
// AF_INET / AF_INET6。

namespace winforward::wfp
{

// ::ffff:0:0/96 的形状：字节 0..9 为零，字节 10..11 为 0xFF，后 4 字节是 IPv4 地址。
inline constexpr std::size_t v4_mapped_zero_prefix_size = 10;
inline constexpr std::size_t v4_mapped_marker_offset = 10;
inline constexpr std::size_t v4_mapped_address_offset = 12;
inline constexpr std::size_t v4_mapped_address_size = 4;
inline constexpr std::uint8_t v4_mapped_marker = 0xFF;

static_assert(address_storage_size == 16, "v4-mapped 归一按 16 字节地址存储清零写出");

// 命中时写出 family 4、清零的 16 字节缓冲与前 4 字节 IPv4 地址并返回 true；否则完全不碰
// out_family / out_address 并返回 false，调用方可以原样保留自己的缺省值。
[[nodiscard]] inline bool try_normalize_v4_mapped(std::uint8_t family,
                                                  const std::uint8_t address[16],
                                                  std::uint8_t& out_family,
                                                  std::uint8_t out_address[16]) noexcept
{
    if (family != static_cast<std::uint8_t>(address_family::ipv6))
    {
        return false;
    }
    for (std::size_t index = 0; index < v4_mapped_zero_prefix_size; ++index)
    {
        if (address[index] != 0)
        {
            return false;
        }
    }
    if (address[v4_mapped_marker_offset] != v4_mapped_marker ||
        address[v4_mapped_marker_offset + 1] != v4_mapped_marker)
    {
        return false;
    }

    // 先取到局部缓冲再写 out_address：两者允许指向同一块 16 字节存储，先清零会把 v4 字节抹掉。
    std::uint8_t ipv4[v4_mapped_address_size];
    for (std::size_t index = 0; index < v4_mapped_address_size; ++index)
    {
        ipv4[index] = address[v4_mapped_address_offset + index];
    }

    for (std::size_t index = 0; index < address_storage_size; ++index)
    {
        out_address[index] = 0;
    }
    for (std::size_t index = 0; index < v4_mapped_address_size; ++index)
    {
        out_address[index] = ipv4[index];
    }
    out_family = static_cast<std::uint8_t>(address_family::ipv4);
    return true;
}

} // namespace winforward::wfp
