#pragma once

#include <cstddef>
#include <cstdint>

#include "rule_blob.h"

// Forwarded 规则 blob —— 用户态（C#）编译、内核只解码执行的可移植契约。
//
// 与 host 的 rule_blob 是两份独立的 ABI：magic/version/头部/规则结构都不相同，任一侧的
// 解码都不会接受对方的 blob。共享的只有 network_prefix（20 字节网段记录）、rule_action
// 以及协议/地址族常量 —— 它们定义在 rule_blob.h，本文件不复制。
//
// 本文件位于 src/common，遵守内核方言：不使用异常、RTTI 与 STL 容器，也不分配内存；
// 解码结果全部是指向原始缓冲区的指针与偏移。所有多字节字段为主机字节序（部署目标只有
// Windows x64 小端）。各段起点 4 字节对齐，因此调用方必须提供 4 字节对齐的 data；
// size 是缓冲区长度，必须等于 header.total_size。
//
// 布局（v1）：
//
//   [0, 40)                            forwarded_blob_header
//   [rules_offset, ...)                forwarded_blob_rule 数组
//   [network_pool_offset, ...)         network_prefix 数组：连续的 20 字节网段条目
//
// 段的位置与长度全部由头部给出，解码不做任何推断。v1 的 header_size 固定为
// sizeof(forwarded_blob_header)（40）；版本号变更即破坏性变更，必须同步 C# 侧的
// 镜像结构与布局测试。

namespace winforward::wfp
{

inline constexpr std::uint32_t forwarded_blob_magic = 0x31424657U; // 内存中为 "WFB1"
inline constexpr std::uint16_t forwarded_blob_version = 1;

// 入口适配器的 0xFFFFFFFF 是"任意入口"，不是 0：0 是一个合法的具体适配器编号。
// upstream 的 0xFFFFFFFF 是"无上游"，只会出现在非 redirect 规则上。
inline constexpr std::uint32_t forwarded_adapter_any = 0xFFFFFFFFU;
inline constexpr std::uint32_t forwarded_upstream_none = 0xFFFFFFFFU;

struct forwarded_blob_header
{
    std::uint32_t magic;
    std::uint16_t version;
    std::uint16_t header_size;
    std::uint32_t total_size;
    std::uint32_t rule_count;
    std::uint32_t fallback_action; // 只允许 permit(0) / block(1)
    std::uint32_t rules_offset;
    std::uint32_t network_pool_offset;
    std::uint32_t network_pool_size; // 必须是 20 的倍数
    std::uint32_t reserved; // 必须为 0
    std::uint32_t reserved2; // 必须为 0
};

static_assert(sizeof(forwarded_blob_header) == 40, "forwarded_blob_header 是对外 ABI，尺寸变更必须提升版本");
static_assert(alignof(forwarded_blob_header) == 4);
static_assert(offsetof(forwarded_blob_header, magic) == 0);
static_assert(offsetof(forwarded_blob_header, version) == 4);
static_assert(offsetof(forwarded_blob_header, header_size) == 6);
static_assert(offsetof(forwarded_blob_header, total_size) == 8);
static_assert(offsetof(forwarded_blob_header, rule_count) == 12);
static_assert(offsetof(forwarded_blob_header, fallback_action) == 16);
static_assert(offsetof(forwarded_blob_header, rules_offset) == 20);
static_assert(offsetof(forwarded_blob_header, network_pool_offset) == 24);
static_assert(offsetof(forwarded_blob_header, network_pool_size) == 28);
static_assert(offsetof(forwarded_blob_header, reserved) == 32);
static_assert(offsetof(forwarded_blob_header, reserved2) == 36);

struct forwarded_blob_rule
{
    std::uint32_t ingress_adapter_id; // 0xFFFFFFFF = 任意入口
    std::uint32_t client_network_index; // 0xFFFFFFFF = 不限；否则是网段池字节偏移
    std::uint32_t remote_network_index; // 同上
    std::uint16_t remote_port_start; // 含
    std::uint16_t remote_port_end; // 含；开始 > 结束即非法
    std::uint32_t upstream_id; // 0xFFFFFFFF = 无；redirect 必须有，非 redirect 必须无
    std::uint8_t protocol; // 0 any / 6 TCP / 17 UDP（复用 rule_protocol_*）
    std::uint8_t family; // 0 any / 4 IPv4 / 6 IPv6（复用 rule_family_*）
    rule_action action; // 复用 rule_action：permit(0) / block(1) / redirect(2)
    std::uint8_t reserved; // 必须为 0
};

static_assert(sizeof(forwarded_blob_rule) == 24, "forwarded_blob_rule 是对外 ABI，尺寸变更必须提升版本");
static_assert(alignof(forwarded_blob_rule) == 4);
static_assert(offsetof(forwarded_blob_rule, ingress_adapter_id) == 0);
static_assert(offsetof(forwarded_blob_rule, client_network_index) == 4);
static_assert(offsetof(forwarded_blob_rule, remote_network_index) == 8);
static_assert(offsetof(forwarded_blob_rule, remote_port_start) == 12);
static_assert(offsetof(forwarded_blob_rule, remote_port_end) == 14);
static_assert(offsetof(forwarded_blob_rule, upstream_id) == 16);
static_assert(offsetof(forwarded_blob_rule, protocol) == 20);
static_assert(offsetof(forwarded_blob_rule, family) == 21);
static_assert(offsetof(forwarded_blob_rule, action) == 22);
static_assert(offsetof(forwarded_blob_rule, reserved) == 23);

// try_open_forwarded_blob 的产物。所有指针指向 data 指向的同一块缓冲区，不拥有内存。
struct forwarded_blob_view
{
    const std::uint8_t* data; // blob 起点，4 字节对齐
    std::uint32_t size; // 等于 header.total_size
    const forwarded_blob_header* header; // data 起始处的头部
    rule_action fallback_action; // 未命中任何规则时使用的动作
    const forwarded_blob_rule* rules; // 规则数组
    std::uint32_t rule_count;
    const network_prefix* network_pool; // 网段池起点
    std::uint32_t network_pool_count;
};

namespace detail
{

// fallback_action 是 u32：必须先按原始宽度做白名单，不能先转成 uint8 再比较（高位会被截断）。
[[nodiscard]] constexpr bool is_valid_forwarded_fallback(std::uint32_t action) noexcept
{
    return action == static_cast<std::uint32_t>(rule_action::permit) ||
           action == static_cast<std::uint32_t>(rule_action::block);
}

// redirect 必须携带具体上游；其余动作必须不带上游（0xFFFFFFFF 是"无"）。
[[nodiscard]] constexpr bool is_valid_forwarded_upstream(rule_action action, std::uint32_t upstream_id) noexcept
{
    if (action == rule_action::redirect)
    {
        return upstream_id != forwarded_upstream_none;
    }
    return upstream_id == forwarded_upstream_none;
}

// 规则族为 any 时不限制网段族；具体族必须与网段记录一致。
[[nodiscard]] constexpr bool forwarded_family_matches(std::uint8_t rule_family, std::uint8_t network_family) noexcept
{
    return rule_family == rule_family_any || rule_family == network_family;
}

// 解析一条规则的网段索引：任意索引返回 true 且 out 为 nullptr；越界/未对齐/族不一致返回 false。
[[nodiscard]] inline bool try_read_forwarded_network(const network_prefix* network_pool,
                                                     std::uint64_t network_size,
                                                     std::uint8_t rule_family,
                                                     std::uint32_t network_index,
                                                     const network_prefix*& out) noexcept
{
    out = nullptr;
    if (network_index == rule_pool_index_any)
    {
        return true;
    }
    // 越界是内存安全边界；20 字节对齐是 ABI 语义。记录本身已在网段池整体校验时验过。
    if (network_index >= network_size)
    {
        return false;
    }
    if (network_index % sizeof(network_prefix) != 0)
    {
        return false;
    }

    const network_prefix& prefix = network_pool[network_index / sizeof(network_prefix)];
    if (!forwarded_family_matches(rule_family, prefix.family))
    {
        return false;
    }
    out = &prefix;
    return true;
}

} // namespace detail

// 解码并校验整个 forwarded blob。失败返回 false，out 保持不变；成功时才写入有效视图。
//
// 校验覆盖：指针与头部尺寸、magic/版本、header_size、total_size、fallback_action、
// 头部保留位、两段的起点/长度是否越界或未对齐、规则数组越界、段重叠、网段池的 20 字节
// 整除性与逐条合法性、每条规则的字段（保留位/动作/协议/族/端口区间/upstream 与动作的
// 组合/两个网段索引）、以及规则族与网段族的一致性。
// header-only 公共 API：src/common 当前没有引用它的翻译单元，cppcheck 的 unusedFunction
// 是误报；消费方是测试与后续驱动。
[[nodiscard]] inline bool
// cppcheck-suppress unusedFunction
try_open_forwarded_blob(const std::uint8_t* data, std::uint32_t size, forwarded_blob_view& out) noexcept
{
    if (data == nullptr)
    {
        return false;
    }
    // 头部按结构体直接解引用，对齐是前提：x86 容忍非对齐访问，C++ 不保证。
    if (reinterpret_cast<std::uintptr_t>(data) % alignof(forwarded_blob_header) != 0)
    {
        return false;
    }
    // 只防"把短缓冲区当 40 字节头读"的越界：逻辑上的段约束在 size < 40 时都无法满足。
    if (size < sizeof(forwarded_blob_header))
    {
        return false;
    }

    const auto* header = reinterpret_cast<const forwarded_blob_header*>(data);
    if (header->magic != forwarded_blob_magic || header->version != forwarded_blob_version)
    {
        return false;
    }
    // v1 不支持扩展头：必须精确等于 40，否则段偏移的语义会漂移。
    if (header->header_size != sizeof(forwarded_blob_header))
    {
        return false;
    }
    // 段边界全部以 total_size 为准，它必须与实际缓冲区长度一致。
    if (header->total_size != size)
    {
        return false;
    }
    if (!detail::is_valid_forwarded_fallback(header->fallback_action))
    {
        return false;
    }
    if (header->reserved != 0 || header->reserved2 != 0)
    {
        return false;
    }

    const std::uint64_t blob_size = header->total_size;
    const std::uint64_t header_size = sizeof(forwarded_blob_header);
    const std::uint64_t rules_offset = header->rules_offset;
    const std::uint64_t network_offset = header->network_pool_offset;
    const std::uint64_t network_size = header->network_pool_size;

    // 先对齐再判界；规则数组要按 forwarded_blob_rule 解引用，4 字节对齐是硬前提。
    if (rules_offset % alignof(forwarded_blob_rule) != 0 || network_offset % alignof(forwarded_blob_rule) != 0)
    {
        return false;
    }
    // 段不能压到头部上。
    if (rules_offset < header_size || network_offset < header_size)
    {
        return false;
    }
    // 先确认 offset 本身不越过终点，下面两条减法才不会无符号回绕。
    if (rules_offset > blob_size || network_offset > blob_size)
    {
        return false;
    }
    if (network_size > blob_size - network_offset)
    {
        return false;
    }
    // 规则数组本身也要落在 blob 内。
    if (header->rule_count > (blob_size - rules_offset) / sizeof(forwarded_blob_rule))
    {
        return false;
    }

    const std::uint64_t rules_end = rules_offset + (header->rule_count * sizeof(forwarded_blob_rule));
    const std::uint64_t network_end = network_offset + network_size;
    // 重叠检查只表达布局语义，不承担内存安全：两段在上面已各自独立判界。
    if (detail::ranges_overlap(rules_offset, rules_end, network_offset, network_end))
    {
        return false;
    }

    // 网段池必须由完整记录组成；逐条校验覆盖未被规则引用的条目。
    if (network_size % sizeof(network_prefix) != 0)
    {
        return false;
    }
    const std::uint64_t network_count = network_size / sizeof(network_prefix);
    const auto* network_pool = reinterpret_cast<const network_prefix*>(data + network_offset);
    for (std::uint64_t index = 0; index < network_count; ++index)
    {
        if (!detail::is_valid_network_prefix(network_pool[index]))
        {
            return false;
        }
    }

    const auto* rules = reinterpret_cast<const forwarded_blob_rule*>(data + rules_offset);
    for (std::uint32_t index = 0; index < header->rule_count; ++index)
    {
        const forwarded_blob_rule& current = rules[index];
        if (current.reserved != 0)
        {
            return false;
        }
        if (!detail::is_known_action(current.action))
        {
            return false;
        }
        if (!detail::is_known_protocol(current.protocol))
        {
            return false;
        }
        if (!detail::is_known_family(current.family))
        {
            return false;
        }
        if (current.remote_port_start > current.remote_port_end)
        {
            return false;
        }
        if (!detail::is_valid_forwarded_upstream(current.action, current.upstream_id))
        {
            return false;
        }

        const network_prefix* remote = nullptr;
        if (!detail::try_read_forwarded_network(
                network_pool, network_size, current.family, current.remote_network_index, remote))
        {
            return false;
        }
        const network_prefix* client = nullptr;
        if (!detail::try_read_forwarded_network(
                network_pool, network_size, current.family, current.client_network_index, client))
        {
            return false;
        }
        // 两个网段都具体时，即使规则族是 any，内核匹配也只能落在同一个地址族上。
        if (remote != nullptr && client != nullptr && remote->family != client->family)
        {
            return false;
        }
    }

    forwarded_blob_view view{};
    view.data = data;
    view.size = header->total_size;
    view.header = header;
    // 上面的 u32 白名单已保证 fallback_action 只能是 0 / 1，这里窄化成枚举是安全的。
    view.fallback_action = static_cast<rule_action>(header->fallback_action);
    view.rules = rules;
    view.rule_count = header->rule_count;
    view.network_pool = network_pool;
    view.network_pool_count = static_cast<std::uint32_t>(network_count);
    out = view;
    return true;
}

} // namespace winforward::wfp
