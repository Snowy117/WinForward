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
// 布局（v2，header 56 / rule 32 / process_selector 8 / port_range 4）：
//
//   [0, 56)                                rule_blob_header
//   [rules_offset, ...)                    连续的 32 字节 rule
//   [process_pool_offset, ...)             进程串池：连续的 NUL 结尾字符串
//   [selector_pool_offset, ...)            连续的 8 字节 process_selector
//   [network_pool_offset, ...)             连续的 20 字节 network_prefix
//   [port_pool_offset, ...)                连续的 4 字节 port_range
//
// 每条规则的选择器都是区间（first + count），同一区间内 OR、不同字段之间 AND；
// count == 0 表示任意，且此时 first 必须为 0。进程选择器分文件名与路径两种：
// 文件名匹配连接路径的最后一段，路径匹配“完整路径相等或以 `选择器 + \` 开头”；
// 两者都只折叠 ASCII 大小写。协议 / 地址族用位掩码表达集合，0 表示任意。
//
// 段的位置与长度全部由头部给出，解码不做任何推断。版本号变更即破坏性变更，必须
// 同步 C# 侧的镜像结构、布局测试与驱动。

namespace winforward::wfp
{

inline constexpr std::uint32_t rule_blob_magic = 0x31425257U; // 内存中为 "WRB1"
inline constexpr std::uint16_t rule_blob_version = 2;

// Host v2 只把它用作“未命中任何规则”的规则序号（规则区间以 count == 0 表达任意，
// 不使用索引哨兵）。Forwarded blob/matcher 仍把它当作网段池索引“不限”的哨兵；
// 不要在按 host 语义清理时删除或改值。
inline constexpr std::uint32_t rule_pool_index_any = 0xFFFFFFFFU;

// 协议掩码：bit0 = TCP，bit1 = UDP；0 = 任意（TCP 与 UDP 都命中）。
inline constexpr std::uint8_t rule_protocol_mask_any = 0;
inline constexpr std::uint8_t rule_protocol_mask_tcp = 1U;
inline constexpr std::uint8_t rule_protocol_mask_udp = 2U;
inline constexpr std::uint8_t rule_protocol_mask_all = 3U;

// 地址族掩码：bit0 = IPv4，bit1 = IPv6；0 = 任意，3 = 两者。
inline constexpr std::uint8_t rule_family_mask_any = 0;
inline constexpr std::uint8_t rule_family_mask_ipv4 = 1U;
inline constexpr std::uint8_t rule_family_mask_ipv6 = 2U;
inline constexpr std::uint8_t rule_family_mask_all = 3U;

// 连接事实与网段记录使用的原始值。
inline constexpr std::uint8_t rule_protocol_tcp = 6;
inline constexpr std::uint8_t rule_protocol_udp = 17;
inline constexpr std::uint8_t rule_family_ipv4 = 4;
inline constexpr std::uint8_t rule_family_ipv6 = 6;

// 以下哨兵只服务 Forwarded blob（另一套 ABI）；Host v2 规则用 count == 0 表达任意。
inline constexpr std::uint16_t rule_port_any_begin = 0;
inline constexpr std::uint16_t rule_port_any_end = 0xFFFFU;
inline constexpr std::uint8_t rule_protocol_any = 0;
inline constexpr std::uint8_t rule_family_any = 0;

inline constexpr std::size_t network_address_size = 16;
inline constexpr std::size_t network_prefix_size = 20;
inline constexpr std::size_t process_selector_size = 8;
inline constexpr std::size_t port_range_size = 4;
inline constexpr std::size_t rule_size = 32;
inline constexpr std::size_t rule_blob_header_size = 56;
inline constexpr std::size_t process_selector_text_max_size = 511;

enum class process_selector_kind : std::uint8_t
{
    file_name = 0, // 匹配连接路径的最后一段
    path = 1 // 完整路径相等，或位于该路径之下
};

static_assert(static_cast<std::uint8_t>(process_selector_kind::file_name) == 0,
              "process_selector.kind 的取值是对外 ABI");
static_assert(static_cast<std::uint8_t>(process_selector_kind::path) == 1, "process_selector.kind 的取值是对外 ABI");

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
    std::uint32_t selector_pool_offset;
    std::uint32_t selector_pool_size;
    std::uint32_t network_pool_offset;
    std::uint32_t network_pool_size;
    std::uint32_t port_pool_offset;
    std::uint32_t port_pool_size;
};

static_assert(sizeof(rule_blob_header) == rule_blob_header_size, "rule_blob_header 是对外 ABI，尺寸变更必须提升版本");
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
static_assert(offsetof(rule_blob_header, selector_pool_offset) == 32);
static_assert(offsetof(rule_blob_header, selector_pool_size) == 36);
static_assert(offsetof(rule_blob_header, network_pool_offset) == 40);
static_assert(offsetof(rule_blob_header, network_pool_size) == 44);
static_assert(offsetof(rule_blob_header, port_pool_offset) == 48);
static_assert(offsetof(rule_blob_header, port_pool_size) == 52);

struct rule
{
    std::uint32_t process_first; // selector_pool 下标
    std::uint32_t process_count; // 0 = 任意，此时 process_first 必须为 0
    std::uint32_t network_first; // network_pool 下标
    std::uint32_t network_count; // 0 = 任意，此时 network_first 必须为 0
    std::uint32_t port_first; // port_pool 下标
    std::uint32_t port_count; // 0 = 任意，此时 port_first 必须为 0
    std::uint8_t protocol_mask; // rule_protocol_mask_*
    std::uint8_t family_mask; // rule_family_mask_*
    rule_action action;
    std::uint8_t reserved; // 必须为 0
    std::uint32_t reserved2; // 必须为 0
};

static_assert(sizeof(rule) == rule_size, "rule 是对外 ABI，尺寸变更必须提升版本");
static_assert(alignof(rule) == 4);
static_assert(offsetof(rule, process_first) == 0);
static_assert(offsetof(rule, process_count) == 4);
static_assert(offsetof(rule, network_first) == 8);
static_assert(offsetof(rule, network_count) == 12);
static_assert(offsetof(rule, port_first) == 16);
static_assert(offsetof(rule, port_count) == 20);
static_assert(offsetof(rule, protocol_mask) == 24);
static_assert(offsetof(rule, family_mask) == 25);
static_assert(offsetof(rule, action) == 26);
static_assert(offsetof(rule, reserved) == 27);
static_assert(offsetof(rule, reserved2) == 28);

struct process_selector
{
    std::uint32_t string_offset; // process_pool 内的字节偏移，指向串首
    process_selector_kind kind;
    std::uint8_t is_directory; // 仅 kind == path 有意义；0/1
    std::uint8_t reserved[2]; // 必须为 0
};

static_assert(sizeof(process_selector) == process_selector_size, "process_selector 是对外 ABI，尺寸变更必须提升版本");
static_assert(alignof(process_selector) == 4);
static_assert(offsetof(process_selector, string_offset) == 0);
static_assert(offsetof(process_selector, kind) == 4);
static_assert(offsetof(process_selector, is_directory) == 5);
static_assert(offsetof(process_selector, reserved) == 6);

struct port_range
{
    std::uint16_t start; // 闭区间；start <= end
    std::uint16_t end;
};

static_assert(sizeof(port_range) == port_range_size, "port_range 是对外 ABI，尺寸变更必须提升版本");
static_assert(alignof(port_range) == 2);
static_assert(offsetof(port_range, start) == 0);
static_assert(offsetof(port_range, end) == 2);

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
    const char* process_pool; // 进程串池起点
    std::uint32_t process_pool_size; // 进程串池字节数，不含段间对齐填充
    const process_selector* selector_pool;
    std::uint32_t selector_pool_count;
    const network_prefix* network_pool;
    std::uint32_t network_pool_count;
    const port_range* port_pool;
    std::uint32_t port_pool_count;
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

[[nodiscard]] constexpr bool is_known_selector_kind(process_selector_kind kind) noexcept
{
    return kind == process_selector_kind::file_name || kind == process_selector_kind::path;
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

// 规则的区间必须整体落在池内；count == 0 是“任意”的规范编码，first 必须为 0。
[[nodiscard]] constexpr bool
is_valid_rule_range(std::uint32_t first, std::uint32_t count, std::uint32_t pool_count) noexcept
{
    if (count == 0)
    {
        return first == 0;
    }
    return first <= pool_count && count <= pool_count - first;
}

// 控制字符按 Unicode 口径判定：C0（含 NUL 之外的 ASCII 控制符）、DEL 与 C1。
// selector 里出现它们只会让路径无法匹配，因此在解码阶段整块拒绝。
[[nodiscard]] constexpr bool is_control_code_point(std::uint32_t code_point) noexcept
{
    return code_point < 0x20U || (code_point >= 0x7FU && code_point <= 0x9FU);
}

// 严格 UTF-8：拒绝超长编码、代理项、> U+10FFFF 与控制字符。长度不含 NUL。
[[nodiscard]] constexpr bool is_valid_utf8_selector(const char* text, std::uint32_t length) noexcept
{
    std::uint32_t index = 0;
    while (index < length)
    {
        const auto lead = static_cast<std::uint8_t>(text[index]);
        if (lead < 0x80U)
        {
            if (is_control_code_point(lead))
            {
                return false;
            }
            ++index;
            continue;
        }

        std::uint32_t extra = 0;
        std::uint32_t code_point = 0;
        if ((lead & 0xE0U) == 0xC0U)
        {
            extra = 1;
            code_point = lead & 0x1FU;
            if (code_point < 2U)
            {
                return false; // overlong
            }
        }
        else if ((lead & 0xF0U) == 0xE0U)
        {
            extra = 2;
            code_point = lead & 0x0FU;
        }
        else if ((lead & 0xF8U) == 0xF0U)
        {
            extra = 3;
            code_point = lead & 0x07U;
        }
        else
        {
            return false;
        }

        ++index;
        for (std::uint32_t continuation_index = 0; continuation_index < extra; ++continuation_index)
        {
            if (index >= length)
            {
                return false;
            }
            const auto continuation = static_cast<std::uint8_t>(text[index++]);
            if ((continuation & 0xC0U) != 0x80U)
            {
                return false;
            }
            code_point = (code_point << 6U) | (continuation & 0x3FU);
        }
        if ((extra == 2U && code_point < 0x800U) ||
            (extra == 3U && (code_point < 0x10000U || code_point > 0x10FFFFU)) ||
            (code_point >= 0xD800U && code_point <= 0xDFFFU) || is_control_code_point(code_point))
        {
            return false;
        }
    }
    return true;
}

// 设备路径契约：路径型 selector 必须以 \device\ 开头（只折叠 ASCII 大小写），否则永远不会命中。
[[nodiscard]] constexpr bool has_device_prefix(const char* text, std::uint32_t length) noexcept
{
    constexpr char prefix[] = "\\device\\";
    for (std::uint32_t index = 0; index < sizeof(prefix) - 1; ++index)
        if (index >= length ||
            (text[index] >= 'A' && text[index] <= 'Z' ? text[index] + 32 : text[index]) != prefix[index])
            return false;
    return true;
}

// 逐条校验 selector 指向的进程串：串首、NUL 终止、长度 1..511、UTF-8/控制字符，
// 以及两种形态各自的分隔符约束（文件名不含 / 与 \；路径以 \device\ 开头且不以 \ 结尾）。
[[nodiscard]] constexpr bool
is_valid_process_selector(const char* pool, std::uint32_t pool_size, const process_selector& selector) noexcept
{
    if (selector.string_offset >= pool_size)
    {
        return false;
    }
    if (selector.string_offset != 0 && pool[selector.string_offset - 1] != '\0')
    {
        return false;
    }

    const char* text = pool + selector.string_offset;
    const std::uint32_t limit = pool_size - selector.string_offset;
    std::uint32_t length = 0;
    while (length < limit && text[length] != '\0')
    {
        ++length;
    }
    // <mutation id="selector-utf8-guard">
    if (length == 0 || length > process_selector_text_max_size || length == limit)
    {
        return false;
    }
    if (!is_valid_utf8_selector(text, length))
    {
        return false;
    }
    // </mutation>

    if (selector.kind == process_selector_kind::file_name)
    {
        for (std::uint32_t index = 0; index < length; ++index)
        {
            if (text[index] == '/' || text[index] == '\\')
            {
                return false;
            }
        }
        return true;
    }
    return has_device_prefix(text, length) && text[length - 1] != '\\';
}

} // namespace detail

// 解码并校验整个 blob。失败返回 false，out 保持不变；成功时才写入有效视图。
//
// 校验覆盖：指针与头部尺寸、magic/版本、header_size、total_size、fallback_action、
// 五个段（rules / process / selector / network / port）的起点、长度、对齐与互不重叠、
// 三个定长池的整除性、进程池末尾的 NUL、每个 selector 的 kind / is_directory /
// reserved / 串首 / NUL 终止 / UTF-8 / 长度 / 分隔符、每个端口区间的 start <= end、
// 每个网段记录逐条合法、每条规则的区间 / 掩码 / 动作 / reserved，以及单族掩码与
// 网段族的一致性。
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
    // 只防“把短缓冲区当 56 字节头读”的越界；逻辑层无法用空 buffer 满足其余约束，
    // 因此这条只能靠越界检测（ASan / 守卫页）钉住。
    if (size < sizeof(rule_blob_header))
    {
        return false;
    }

    const auto* header = reinterpret_cast<const rule_blob_header*>(data);
    if (header->magic != rule_blob_magic || header->version != rule_blob_version)
    {
        return false;
    }
    // v2 不支持扩展头：必须精确等于 56，否则 rules_offset 的语义会漂移。
    if (header->header_size != sizeof(rule_blob_header))
    {
        return false;
    }
    // 段边界全部以 total_size 为准，它必须与实际缓冲区长度一致。
    if (header->total_size != size)
    {
        return false;
    }
    if (!detail::is_known_action_value(header->fallback_action))
    {
        return false;
    }

    const std::uint64_t blob_size = header->total_size;
    const std::uint64_t header_size = sizeof(rule_blob_header);

    // 五个段按布局顺序排列，便于循环做对齐 / 判界 / 互不重叠。
    const std::uint64_t offsets[] = { header->rules_offset,
                                      header->process_pool_offset,
                                      header->selector_pool_offset,
                                      header->network_pool_offset,
                                      header->port_pool_offset };
    const std::uint64_t sizes[] = { static_cast<std::uint64_t>(header->rule_count) * sizeof(rule),
                                    header->process_pool_size,
                                    header->selector_pool_size,
                                    header->network_pool_size,
                                    header->port_pool_size };
    constexpr std::size_t segment_count = sizeof(offsets) / sizeof(offsets[0]);

    for (std::size_t index = 0; index < segment_count; ++index)
    {
        // 段起点统一 4 字节对齐；规则数组还要按 rule 解引用。
        if (offsets[index] % alignof(rule) != 0 || offsets[index] < header_size || offsets[index] > blob_size)
        {
            return false;
        }
        // 段长以 total_size 为界；通过后各段内部的指针运算才都在缓冲区内。
        if (sizes[index] > blob_size - offsets[index])
        {
            return false;
        }
    }
    for (std::size_t first = 0; first < segment_count; ++first)
    {
        for (std::size_t second = first + 1; second < segment_count; ++second)
        {
            if (detail::ranges_overlap(
                    offsets[first], offsets[first] + sizes[first], offsets[second], offsets[second] + sizes[second]))
            {
                return false;
            }
        }
    }

    // 定长池必须由完整记录组成；进程串池长度自由，由 NUL 终止约束。
    if (header->selector_pool_size % sizeof(process_selector) != 0 ||
        header->network_pool_size % sizeof(network_prefix) != 0 || header->port_pool_size % sizeof(port_range) != 0)
    {
        return false;
    }

    const auto* process_pool = reinterpret_cast<const char*>(data + header->process_pool_offset);
    // 池尾 NUL 保证 selector 串扫描必然在池内终止；串内 length == limit 分支因此只是
    // 纵深防御，删掉这一步它才会成为实际防线。
    if (header->process_pool_size != 0 && process_pool[header->process_pool_size - 1] != '\0')
    {
        return false;
    }

    // 网段池逐条校验，覆盖未被规则引用的条目。
    const std::uint32_t network_count = static_cast<std::uint32_t>(header->network_pool_size / sizeof(network_prefix));
    const auto* network_pool = reinterpret_cast<const network_prefix*>(data + header->network_pool_offset);
    for (std::uint32_t index = 0; index < network_count; ++index)
    {
        if (!detail::is_valid_network_prefix(network_pool[index]))
        {
            return false;
        }
    }

    // selector 池逐条校验；字符串本身在进程串池里。
    const std::uint32_t selector_count =
        static_cast<std::uint32_t>(header->selector_pool_size / sizeof(process_selector));
    const auto* selector_pool = reinterpret_cast<const process_selector*>(data + header->selector_pool_offset);
    for (std::uint32_t index = 0; index < selector_count; ++index)
    {
        const process_selector& selector = selector_pool[index];
        if (!detail::is_known_selector_kind(selector.kind) || selector.is_directory > 1)
        {
            return false;
        }
        // 规范化编码：is_directory 只对路径型有意义，文件名型必须写 0。
        if (selector.kind == process_selector_kind::file_name && selector.is_directory != 0)
        {
            return false;
        }
        if (selector.reserved[0] != 0 || selector.reserved[1] != 0)
        {
            return false;
        }
        if (!detail::is_valid_process_selector(process_pool, header->process_pool_size, selector))
        {
            return false;
        }
    }

    const std::uint32_t port_count = static_cast<std::uint32_t>(header->port_pool_size / sizeof(port_range));
    const auto* port_pool = reinterpret_cast<const port_range*>(data + header->port_pool_offset);
    for (std::uint32_t index = 0; index < port_count; ++index)
    {
        if (port_pool[index].start > port_pool[index].end)
        {
            return false;
        }
    }

    const auto* rules = reinterpret_cast<const rule*>(data + header->rules_offset);
    for (std::uint32_t index = 0; index < header->rule_count; ++index)
    {
        const rule& current = rules[index];
        if (current.reserved != 0 || current.reserved2 != 0)
        {
            return false;
        }
        if (!detail::is_known_action(current.action))
        {
            return false;
        }
        if (current.protocol_mask > rule_protocol_mask_all || current.family_mask > rule_family_mask_all)
        {
            return false;
        }
        if (!detail::is_valid_rule_range(current.process_first, current.process_count, selector_count) ||
            !detail::is_valid_rule_range(current.network_first, current.network_count, network_count) ||
            !detail::is_valid_rule_range(current.port_first, current.port_count, port_count))
        {
            return false;
        }
        // 规则只允许一个族时，它引用的每个网段都必须是该族，否则规则永远不可能命中。
        if (current.family_mask == rule_family_mask_ipv4 || current.family_mask == rule_family_mask_ipv6)
        {
            const std::uint8_t expected_family =
                current.family_mask == rule_family_mask_ipv4 ? rule_family_ipv4 : rule_family_ipv6;
            for (std::uint32_t network_index = 0; network_index < current.network_count; ++network_index)
            {
                if (network_pool[current.network_first + network_index].family != expected_family)
                {
                    return false;
                }
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
    view.selector_pool = selector_pool;
    view.selector_pool_count = selector_count;
    view.network_pool = network_pool;
    view.network_pool_count = network_count;
    view.port_pool = port_pool;
    view.port_pool_count = port_count;
    out = view;
    return true;
}

} // namespace winforward::wfp
