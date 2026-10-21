#pragma once

#include <cstddef>
#include <cstdint>

// 规则 blob —— 用户态（C#）编译、内核只解码执行的可移植契约。
//
// 本文件位于 src/common，两侧都会编译，因此遵守内核方言：不使用异常、RTTI 与
// STL 容器，也不分配内存；解码结果全部是指向原始缓冲区的指针与偏移。
//
// 所有多字节字段为主机字节序（部署目标只有 Windows x64 小端）。各段起点 4 字节
// 对齐，因此调用方必须提供 4 字节对齐的 data。size 是缓冲区长度，必须等于
// header.total_size。
//
// 布局（v1）：
//
//   [0, 40)                            rule_blob_header
//   [rules_offset, ...)                rule 数组
//   [process_pool_offset, ...)         进程池：连续的 NUL 结尾字符串
//   [network_pool_offset, ...)         网段池：连续的 20 字节网段条目
//
// 段的位置与长度全部由头部给出，解码不做任何推断。v1 的 header_size 固定为
// sizeof(rule_blob_header)（40）；版本号变更即破坏性变更，必须同步 C# 侧的
// 镜像结构与布局测试。

namespace winforward::wfp
{

inline constexpr std::uint32_t rule_blob_magic = 0x31425257U; // 内存中为 "WRB1"
inline constexpr std::uint16_t rule_blob_version = 1;

// 规则里的"任意"哨兵：池内偏移 0xFFFFFFFF 表示不限制，端口 0..0xFFFF 表示任意端口，
// 协议 0 与地址族 0 同样表示不限制。
inline constexpr std::uint32_t rule_pool_index_any = 0xFFFFFFFFU;
inline constexpr std::uint16_t rule_port_any_begin = 0;
inline constexpr std::uint16_t rule_port_any_end = 0xFFFFU;
inline constexpr std::uint8_t rule_protocol_any = 0;
inline constexpr std::uint8_t rule_protocol_tcp = 6;
// 17 是 IPPROTO_UDP 的原始数值。新增协议值属于枚举扩展，规则布局不变，因此
// rule_blob_version 保持 1；旧驱动遇到 17 会按未知协议整块拒绝（fail-closed），不会静默放行。
inline constexpr std::uint8_t rule_protocol_udp = 17;
inline constexpr std::uint8_t rule_family_any = 0;
inline constexpr std::uint8_t rule_family_ipv4 = 4;
inline constexpr std::uint8_t rule_family_ipv6 = 6;

inline constexpr std::size_t network_address_size = 16;
inline constexpr std::size_t network_prefix_size = 20;

enum class rule_action : std::uint8_t
{
    permit = 0,
    block = 1,
    redirect = 2
};

struct rule_blob_header
{
    std::uint32_t magic;
    std::uint16_t version;
    std::uint16_t header_size;
    std::uint32_t total_size;
    std::uint32_t rule_count;
    std::uint32_t fallback_action;
    std::uint32_t rules_offset;
    std::uint32_t process_pool_offset;
    std::uint32_t process_pool_size;
    std::uint32_t network_pool_offset;
    std::uint32_t network_pool_size;
};

static_assert(sizeof(rule_blob_header) == 40, "rule_blob_header 是对外 ABI，尺寸变更必须提升版本");
static_assert(alignof(rule_blob_header) == 4);
static_assert(offsetof(rule_blob_header, magic) == 0);
static_assert(offsetof(rule_blob_header, version) == 4);
static_assert(offsetof(rule_blob_header, header_size) == 6);
static_assert(offsetof(rule_blob_header, total_size) == 8);
static_assert(offsetof(rule_blob_header, rule_count) == 12);
static_assert(offsetof(rule_blob_header, fallback_action) == 16);
static_assert(offsetof(rule_blob_header, rules_offset) == 20);
static_assert(offsetof(rule_blob_header, process_pool_offset) == 24);
static_assert(offsetof(rule_blob_header, process_pool_size) == 28);
static_assert(offsetof(rule_blob_header, network_pool_offset) == 32);
static_assert(offsetof(rule_blob_header, network_pool_size) == 36);

struct rule
{
    std::uint32_t process_index;
    std::uint32_t network_index;
    std::uint16_t remote_port_start;
    std::uint16_t remote_port_end;
    std::uint8_t protocol;
    std::uint8_t family;
    rule_action action;
    std::uint8_t reserved;
};

static_assert(sizeof(rule) == 16, "rule 是对外 ABI，尺寸变更必须提升版本");
static_assert(alignof(rule) == 4);
static_assert(offsetof(rule, process_index) == 0);
static_assert(offsetof(rule, network_index) == 4);
static_assert(offsetof(rule, remote_port_start) == 8);
static_assert(offsetof(rule, remote_port_end) == 10);
static_assert(offsetof(rule, protocol) == 12);
static_assert(offsetof(rule, family) == 13);
static_assert(offsetof(rule, action) == 14);
static_assert(offsetof(rule, reserved) == 15);

struct network_prefix
{
    std::uint8_t prefix_length;
    std::uint8_t family;
    std::uint8_t reserved[2];
    std::uint8_t network[network_address_size];
};

static_assert(sizeof(network_prefix) == network_prefix_size, "network_prefix 是对外 ABI，尺寸变更必须提升版本");
static_assert(alignof(network_prefix) == 1);
static_assert(offsetof(network_prefix, prefix_length) == 0);
static_assert(offsetof(network_prefix, family) == 1);
static_assert(offsetof(network_prefix, reserved) == 2);
static_assert(offsetof(network_prefix, network) == 4);

// try_open_blob 的产物。所有指针指向 data 指向的同一块缓冲区，不拥有内存。
struct blob_view
{
    const std::uint8_t* data; // blob 起点，4 字节对齐
    std::uint32_t size; // 等于 header.total_size
    const rule_blob_header* header; // data 起始处的头部
    rule_action fallback_action; // 未命中任何规则时使用的动作
    const rule* rules; // 规则数组
    std::uint32_t rule_count;
    const char* process_pool; // 进程池起点
    std::uint32_t process_pool_size; // 进程串池字节数，不含段间对齐填充
    const network_prefix* network_pool;
    std::uint32_t network_pool_count;
};

namespace detail
{

[[nodiscard]] constexpr bool ranges_overlap(std::uint64_t first_begin,
                                            std::uint64_t first_end,
                                            std::uint64_t second_begin,
                                            std::uint64_t second_end) noexcept
{
    return first_begin < first_end && second_begin < second_end && first_begin < second_end && second_begin < first_end;
}

[[nodiscard]] constexpr bool is_known_family(std::uint8_t family) noexcept
{
    return family == rule_family_any || family == rule_family_ipv4 || family == rule_family_ipv6;
}

[[nodiscard]] constexpr bool is_known_protocol(std::uint8_t protocol) noexcept
{
    return protocol == rule_protocol_any || protocol == rule_protocol_tcp || protocol == rule_protocol_udp;
}

[[nodiscard]] constexpr bool is_known_action(rule_action action) noexcept
{
    return action == rule_action::permit || action == rule_action::block || action == rule_action::redirect;
}

[[nodiscard]] constexpr bool is_known_action_value(std::uint32_t value) noexcept
{
    return value <= static_cast<std::uint32_t>(rule_action::redirect);
}

[[nodiscard]] constexpr bool is_valid_network_prefix(const network_prefix& prefix) noexcept
{
    if (prefix.reserved[0] != 0 || prefix.reserved[1] != 0)
    {
        return false;
    }
    if (prefix.family == rule_family_ipv4)
    {
        return prefix.prefix_length <= 32;
    }
    if (prefix.family == rule_family_ipv6)
    {
        return prefix.prefix_length <= 128;
    }
    return false;
}

} // namespace detail

// 解码并校验整个 blob。失败返回 false，out 保持不变；成功时才写入有效视图。
//
// 校验覆盖：指针与头部尺寸、magic/版本、header_size、total_size、
// fallback_action、三个段的起点/长度是否越界或未对齐、规则数组与池是否重叠、
// 进程池末尾的 NUL、每条规则的字段与两个池内偏移、网段池的 20 字节整除性与
// 逐条合法性、以及规则族与网段族的一致性。
[[nodiscard]] inline bool try_open_blob(const std::uint8_t* data, std::uint32_t size, blob_view& out) noexcept
{
    if (data == nullptr)
    {
        return false;
    }
    // 头部按结构体直接解引用，对齐是前提：x86 容忍非对齐访问，C++ 不保证。
    if (reinterpret_cast<std::uintptr_t>(data) % alignof(rule_blob_header) != 0)
    {
        return false;
    }
    // 只防"把短缓冲区当 40 字节头读"的越界，不承担语义校验：size < 40 的 blob 在逻辑上
    // 无法同时满足 header_size == 40 与段偏移约束，所以只能靠越界检测（ASan / 守卫页）钉住。
    if (size < sizeof(rule_blob_header))
    {
        return false;
    }

    const auto* header = reinterpret_cast<const rule_blob_header*>(data);
    // 以下都是纯语义检查；能走到这里说明至少 40 字节可读。
    if (header->magic != rule_blob_magic || header->version != rule_blob_version)
    {
        return false;
    }
    // v1 不支持扩展头：必须精确等于 40，否则 rules_offset 的语义会漂移。
    if (header->header_size != sizeof(rule_blob_header))
    {
        return false;
    }
    // 段边界全部以 total_size 为准，它必须与实际缓冲区长度一致。
    if (header->total_size != size)
    {
        return false;
    }
    // 与 rule.action 共用取值白名单。
    if (!detail::is_known_action_value(header->fallback_action))
    {
        return false;
    }

    const std::uint64_t blob_size = header->total_size;
    const std::uint64_t header_size = sizeof(rule_blob_header);

    const std::uint64_t rules_offset = header->rules_offset;
    const std::uint64_t process_offset = header->process_pool_offset;
    const std::uint64_t process_size = header->process_pool_size;
    const std::uint64_t network_offset = header->network_pool_offset;
    const std::uint64_t network_size = header->network_pool_size;

    // 先对齐再判界；规则数组要按 rule 解引用，4 字节对齐是硬前提（另两个段沿用 ABI 约定）。
    if (rules_offset % alignof(rule) != 0 || process_offset % alignof(rule) != 0 || network_offset % alignof(rule) != 0)
    {
        return false;
    }
    // 段不能压到头部上。
    if (rules_offset < header_size || process_offset < header_size || network_offset < header_size)
    {
        return false;
    }
    // 先确认 offset 本身不越过终点，下面两条减法才不会无符号回绕。
    if (rules_offset > blob_size || process_offset > blob_size || network_offset > blob_size)
    {
        return false;
    }
    // 段长以 total_size 为界；通过后各段内部的指针运算才都在缓冲区内。
    if (process_size > blob_size - process_offset || network_size > blob_size - network_offset)
    {
        return false;
    }

    const std::uint64_t rule_count = header->rule_count;
    // 规则数组本身也要落在 blob 内。
    if (rule_count > (blob_size - rules_offset) / sizeof(rule))
    {
        return false;
    }

    const std::uint64_t rules_end = rules_offset + rule_count * sizeof(rule);
    const std::uint64_t process_end = process_offset + process_size;
    const std::uint64_t network_end = network_offset + network_size;
    // 重叠检查只表达布局语义，不承担内存安全：三个段在上面已各自独立判界。
    if (detail::ranges_overlap(rules_offset, rules_end, process_offset, process_end))
    {
        return false;
    }
    if (detail::ranges_overlap(rules_offset, rules_end, network_offset, network_end))
    {
        return false;
    }
    if (detail::ranges_overlap(process_offset, process_end, network_offset, network_end))
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

    const auto* process_pool = reinterpret_cast<const char*>(data + process_offset);
    // 池尾 NUL 保证下面每条被引用串的扫描必然在池内终止；串内那个 end == process_size
    // 分支因此只是纵深防御，删掉这一步它才会成为实际防线。
    if (process_size != 0 && process_pool[process_size - 1] != '\0')
    {
        return false;
    }

    const auto* rules = reinterpret_cast<const rule*>(data + rules_offset);

    for (std::uint32_t index = 0; index < header->rule_count; ++index)
    {
        const rule& current = rules[index];
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

        if (current.process_index != rule_pool_index_any)
        {
            // 越界是内存安全边界；"前一字节为 NUL"保证指向串首，扫描保证 NUL 结尾。
            if (current.process_index >= process_size)
            {
                return false;
            }
            if (current.process_index != 0 && process_pool[current.process_index - 1] != '\0')
            {
                return false;
            }
            std::uint64_t end = current.process_index;
            while (end < process_size && process_pool[end] != '\0')
            {
                ++end;
            }
            if (end == process_size)
            {
                return false;
            }
        }

        if (current.network_index != rule_pool_index_any)
        {
            // 越界是内存安全边界；20 字节对齐是 ABI 语义。记录本身已在上面整体校验。
            if (current.network_index >= network_size)
            {
                return false;
            }
            if (current.network_index % sizeof(network_prefix) != 0)
            {
                return false;
            }
            const network_prefix& prefix = network_pool[current.network_index / sizeof(network_prefix)];
            if (current.family != rule_family_any && current.family != prefix.family)
            {
                return false;
            }
        }
    }

    blob_view view{};
    view.data = data;
    view.size = header->total_size;
    view.header = header;
    view.fallback_action = static_cast<rule_action>(header->fallback_action);
    view.rules = rules;
    view.rule_count = header->rule_count;
    view.process_pool = process_pool;
    view.process_pool_size = header->process_pool_size;
    view.network_pool = network_pool;
    view.network_pool_count = static_cast<std::uint32_t>(network_count);
    out = view;
    return true;
}

} // namespace winforward::wfp
