#pragma once

#include <cstddef>
#include <cstdint>

// 内核与用户态之间唯一的用途数据。内核在 ALE_CONNECT_REDIRECT 的 classify 里把它挂到
// FWPS_CONNECT_REQUEST0::localRedirectContext，用户态用
// SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT 读回原始目的地。
//
// 本文件位于 src/common，两侧都会编译，因此遵守内核方言：
//   - 不使用异常、RTTI、STL 容器；
//   - 只依赖 <cstddef> 与 <cstdint>。
//
// 所有多字节字段为主机字节序。部署目标只有 Windows x64 小端，显式依赖比隐式假设更好读。
// 任何字段调整都必须同时提升 redirect_context_version，并同步 C# 侧的镜像结构与布局测试。

namespace winforward::wfp
{

inline constexpr std::uint32_t redirect_context_magic = 0x31434657U; // 内存中为 "WFC1"
inline constexpr std::uint16_t redirect_context_version = 1;
inline constexpr std::size_t address_storage_size = 16;

enum class address_family : std::uint8_t
{
    ipv4 = 4,
    ipv6 = 6,
};

enum class transport_protocol : std::uint8_t
{
    tcp = 6,
    udp = 17,
};

struct redirect_context
{
    std::uint32_t magic;
    std::uint32_t rule_index; // 命中的规则序号，仅用于诊断
    std::uint32_t process_id;
    std::uint16_t version;
    std::uint16_t original_port;
    address_family family;
    transport_protocol protocol;
    std::uint8_t reserved[2];
    std::uint8_t original_address[address_storage_size]; // IPv4 只用前 4 字节
};

static_assert(sizeof(redirect_context) == 36, "redirect_context 是对外 ABI，尺寸变更必须提升版本");
static_assert(offsetof(redirect_context, magic) == 0);
static_assert(offsetof(redirect_context, rule_index) == 4);
static_assert(offsetof(redirect_context, process_id) == 8);
static_assert(offsetof(redirect_context, version) == 12);
static_assert(offsetof(redirect_context, original_port) == 14);
static_assert(offsetof(redirect_context, family) == 16);
static_assert(offsetof(redirect_context, protocol) == 17);
static_assert(offsetof(redirect_context, original_address) == 20);

struct original_destination
{
    address_family family;
    transport_protocol protocol;
    std::uint16_t port;
    std::uint8_t address[address_storage_size];
};

[[nodiscard]] constexpr bool is_redirect_context_valid(const redirect_context& context) noexcept
{
    return context.magic == redirect_context_magic && context.version == redirect_context_version;
}

[[nodiscard]] constexpr redirect_context make_redirect_context(const original_destination& destination,
                                                               std::uint32_t process_id,
                                                               std::uint32_t rule_index) noexcept
{
    redirect_context context{};
    context.magic = redirect_context_magic;
    context.version = redirect_context_version;
    context.rule_index = rule_index;
    context.process_id = process_id;
    context.family = destination.family;
    context.protocol = destination.protocol;
    context.original_port = destination.port;
    for (std::size_t index = 0; index < address_storage_size; ++index)
    {
        context.original_address[index] = destination.address[index];
    }
    return context;
}

// 只被宿主机测试（tests/host/redirect_context_test.cpp）使用；测试 TU 含 gtest 宏，
// cppcheck 解析不了那个翻译单元，所以把抑制写在定义处，而不是维护一张集中白名单。
// cppcheck-suppress unusedFunction
[[nodiscard]] constexpr bool try_read_original_destination(const redirect_context& context,
                                                           original_destination& destination) noexcept
{
    if (!is_redirect_context_valid(context))
    {
        return false;
    }
    if (context.family != address_family::ipv4 && context.family != address_family::ipv6)
    {
        return false;
    }
    if (context.protocol != transport_protocol::tcp)
    {
        return false;
    }

    destination.family = context.family;
    destination.protocol = context.protocol;
    destination.port = context.original_port;
    for (std::size_t index = 0; index < address_storage_size; ++index)
    {
        destination.address[index] = context.original_address[index];
    }
    return true;
}

} // namespace winforward::wfp
