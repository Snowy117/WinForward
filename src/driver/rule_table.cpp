#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

constexpr std::uint32_t retire_wait_rounds = 200; // 10 ms * 200 = 2 s
constexpr std::uint32_t retire_wait_milliseconds = 10;

[[nodiscard]] bool references_drained(const rule_table* table) noexcept
{
    return InterlockedCompareExchange(const_cast<volatile LONG*>(&table->references), 0, 0) == 0;
}

void wait_one_tick() noexcept
{
    LARGE_INTEGER interval{};
    interval.QuadPart = -static_cast<LONGLONG>(retire_wait_milliseconds) * 10000;
    KeDelayExecutionThread(KernelMode, FALSE, &interval);
}

// 把表挂到退役链。只在等不到引用归零时走这条路：读者临界区是有限次比较，2 秒不归零
// 说明有 bug（或者有人把读者改成了阻塞等待），此时宁可少释放一块内存，也不能 UAF。
void push_retired(rule_table* table) noexcept
{
    KIRQL old_irql{};
    const bool raised = probe_lock(old_irql);
    table->retired_next = probe_state_ref().retired_rules;
    probe_state_ref().retired_rules = table;
    probe_unlock(raised, old_irql);
}

} // namespace

namespace
{

[[nodiscard]] constexpr std::uint8_t family_bit(std::uint8_t family) noexcept
{
    if (family == rule_family_ipv4)
    {
        return rule_family_mask_ipv4;
    }
    if (family == rule_family_ipv6)
    {
        return rule_family_mask_ipv6;
    }
    return 0;
}

// 有效族集 = 声明的族掩码（0 视为两者）与规则实际引用网段的族集合之交；没有网段时就是声明的集合。
// 与 C# RuleOrderValidator.EffectiveFamilies 逐条对应。
[[nodiscard]] std::uint8_t effective_families(const blob_view& view, const rule& selected) noexcept
{
    const std::uint8_t families =
        selected.family_mask == rule_family_mask_any ? rule_family_mask_all : selected.family_mask;
    if (selected.network_count == 0)
    {
        return families;
    }

    std::uint8_t network_families = 0;
    for (std::uint32_t index = 0; index < selected.network_count; ++index)
    {
        network_families |= family_bit(view.network_pool[selected.network_first + index].family);
    }
    return static_cast<std::uint8_t>(families & network_families);
}

[[nodiscard]] bool protocols_overlap(const rule& first, const rule& second) noexcept
{
    return first.protocol_mask == rule_protocol_mask_any || second.protocol_mask == rule_protocol_mask_any ||
           (first.protocol_mask & second.protocol_mask) != 0;
}

[[nodiscard]] bool families_overlap(const blob_view& view, const rule& first, const rule& second) noexcept
{
    return (effective_families(view, first) & effective_families(view, second)) != 0;
}

[[nodiscard]] bool ports_overlap(const blob_view& view, const rule& first, const rule& second) noexcept
{
    if (first.port_count == 0 || second.port_count == 0)
    {
        return true;
    }

    for (std::uint32_t left = 0; left < first.port_count; ++left)
    {
        const port_range& first_range = view.port_pool[first.port_first + left];
        for (std::uint32_t right = 0; right < second.port_count; ++right)
        {
            const port_range& second_range = view.port_pool[second.port_first + right];
            if (first_range.start <= second_range.end && second_range.start <= first_range.end)
            {
                return true;
            }
        }
    }
    return false;
}

[[nodiscard]] bool prefixes_intersect(const network_prefix& first, const network_prefix& second) noexcept
{
    const network_prefix& shorter = first.prefix_length <= second.prefix_length ? first : second;
    const network_prefix& longer = first.prefix_length <= second.prefix_length ? second : first;
    const std::uint32_t full_bytes = shorter.prefix_length / 8U;
    const std::uint32_t remaining_bits = shorter.prefix_length % 8U;
    for (std::uint32_t index = 0; index < full_bytes; ++index)
    {
        if (longer.network[index] != shorter.network[index])
        {
            return false;
        }
    }
    if (remaining_bits == 0)
    {
        return true;
    }
    const auto mask = static_cast<std::uint8_t>(0xFFU << (8U - remaining_bits));
    return ((longer.network[full_bytes] ^ shorter.network[full_bytes]) & mask) == 0;
}

[[nodiscard]] bool networks_overlap(const blob_view& view, const rule& first, const rule& second) noexcept
{
    if (first.network_count == 0 || second.network_count == 0)
    {
        return true;
    }

    for (std::uint32_t left = 0; left < first.network_count; ++left)
    {
        const network_prefix& first_prefix = view.network_pool[first.network_first + left];
        for (std::uint32_t right = 0; right < second.network_count; ++right)
        {
            const network_prefix& second_prefix = view.network_pool[second.network_first + right];
            if (first_prefix.family == second_prefix.family && prefixes_intersect(first_prefix, second_prefix))
            {
                return true;
            }
        }
    }
    return false;
}

[[nodiscard]] constexpr std::uint8_t fold_ascii(std::uint8_t byte) noexcept
{
    return byte >= 'A' && byte <= 'Z' ? static_cast<std::uint8_t>(byte + ('a' - 'A')) : byte;
}

[[nodiscard]] bool
ascii_equal(const char* first, std::uint32_t first_length, const char* second, std::uint32_t second_length) noexcept
{
    if (first_length != second_length)
    {
        return false;
    }
    for (std::uint32_t index = 0; index < first_length; ++index)
    {
        if (fold_ascii(static_cast<std::uint8_t>(first[index])) != fold_ascii(static_cast<std::uint8_t>(second[index])))
        {
            return false;
        }
    }
    return true;
}

// try_open_blob 已保证 selector 指向池内 NUL 结尾串；这里只计算串长。
[[nodiscard]] const char*
selector_text(const blob_view& view, const process_selector& selector, std::uint32_t& length) noexcept
{
    const char* text = view.process_pool + selector.string_offset;
    const std::uint32_t limit = view.process_pool_size - selector.string_offset;
    length = 0;
    while (length < limit && text[length] != '\0')
    {
        ++length;
    }
    return text;
}

[[nodiscard]] std::uint32_t last_segment_offset(const char* path, std::uint32_t length) noexcept
{
    std::uint32_t begin = 0;
    for (std::uint32_t index = 0; index < length; ++index)
    {
        if (path[index] == '\\')
        {
            begin = index + 1;
        }
    }
    return begin;
}

// 文件名 vs 路径：目录可能包含任意文件名；文件只可能命中最后一段。与 C# FileNameMatchesPath 一致。
[[nodiscard]] bool file_name_matches_path(const char* name,
                                          std::uint32_t name_length,
                                          std::uint8_t is_directory,
                                          const char* path,
                                          std::uint32_t path_length) noexcept
{
    if (is_directory != 0)
    {
        return true;
    }
    const std::uint32_t begin = last_segment_offset(path, path_length);
    return ascii_equal(name, name_length, path + begin, path_length - begin);
}

// matcher 的目录语义：连接路径以「选择器 + \」开头。顺序校验因此对路径选择器保守。
[[nodiscard]] bool is_path_prefix(const char* candidate,
                                  std::uint32_t candidate_length,
                                  const char* path,
                                  std::uint32_t path_length) noexcept
{
    if (candidate_length >= path_length)
    {
        return false;
    }
    for (std::uint32_t index = 0; index < candidate_length; ++index)
    {
        if (fold_ascii(static_cast<std::uint8_t>(candidate[index])) !=
            fold_ascii(static_cast<std::uint8_t>(path[index])))
        {
            return false;
        }
    }
    return path[candidate_length] == '\\';
}

[[nodiscard]] bool
selectors_overlap(const blob_view& view, const process_selector& first, const process_selector& second) noexcept
{
    std::uint32_t first_length = 0;
    std::uint32_t second_length = 0;
    const char* first_text = selector_text(view, first, first_length);
    const char* second_text = selector_text(view, second, second_length);

    if (first.kind == process_selector_kind::file_name && second.kind == process_selector_kind::file_name)
    {
        return ascii_equal(first_text, first_length, second_text, second_length);
    }
    if (first.kind == process_selector_kind::file_name)
    {
        return file_name_matches_path(first_text, first_length, second.is_directory, second_text, second_length);
    }
    if (second.kind == process_selector_kind::file_name)
    {
        return file_name_matches_path(second_text, second_length, first.is_directory, first_text, first_length);
    }
    return ascii_equal(first_text, first_length, second_text, second_length) ||
           is_path_prefix(first_text, first_length, second_text, second_length) ||
           is_path_prefix(second_text, second_length, first_text, first_length);
}

[[nodiscard]] bool processes_overlap(const blob_view& view, const rule& first, const rule& second) noexcept
{
    if (first.process_count == 0 || second.process_count == 0)
    {
        return true;
    }

    for (std::uint32_t left = 0; left < first.process_count; ++left)
    {
        const process_selector& first_selector = view.selector_pool[first.process_first + left];
        for (std::uint32_t right = 0; right < second.process_count; ++right)
        {
            if (selectors_overlap(view, first_selector, view.selector_pool[second.process_first + right]))
            {
                return true;
            }
        }
    }
    return false;
}

// block 规则只允许精确文件路径：WFP 的 ALE_APP_ID 条件只能做完整 EQUAL，文件名/目录前缀
// 写在 block 规则里会变成「classify 记 block、auth 层拦不住」的假 block。命中时返回 false 并
// 带回规则序号，由调用方 DbgPrint 后整块拒绝。
[[nodiscard]] bool block_expression_valid(const blob_view& view, std::uint32_t& rule_index) noexcept
{
    for (rule_index = 0; rule_index < view.rule_count; ++rule_index)
    {
        const rule& selected = view.rules[rule_index];
        if (selected.action != rule_action::block)
        {
            continue;
        }
        for (std::uint32_t index = 0; index < selected.process_count; ++index)
        {
            const process_selector& selector = view.selector_pool[selected.process_first + index];
            if (selector.kind != process_selector_kind::path || selector.is_directory != 0)
            {
                return false;
            }
        }
    }
    return true;
}

[[nodiscard]] constexpr std::uint32_t block_protocol_count(std::uint8_t protocol_mask) noexcept
{
    return protocol_mask == rule_protocol_mask_any || protocol_mask == rule_protocol_mask_all ? 2U : 1U;
}

[[nodiscard]] bool block_filter_budget_valid(const blob_view& view) noexcept
{
    std::uint64_t total = view.fallback_action == rule_action::block ? 4U : 0U;
    for (std::uint32_t rule_index = 0; rule_index < view.rule_count; ++rule_index)
    {
        const rule& selected = view.rules[rule_index];
        if (selected.action != rule_action::block)
        {
            continue;
        }

        std::uint64_t networks = 0;
        for (std::uint32_t layer = 0; layer < 2; ++layer)
        {
            const std::uint8_t mask_bit = layer == 0 ? rule_family_mask_ipv4 : rule_family_mask_ipv6;
            const std::uint8_t family = layer == 0 ? rule_family_ipv4 : rule_family_ipv6;
            if (selected.family_mask != rule_family_mask_any && (selected.family_mask & mask_bit) == 0)
            {
                continue;
            }
            if (selected.network_count == 0)
            {
                ++networks;
                continue;
            }
            for (std::uint32_t index = 0; index < selected.network_count; ++index)
            {
                if (view.network_pool[selected.network_first + index].family == family)
                {
                    ++networks;
                }
            }
        }

        const std::uint64_t processes = selected.process_count == 0 ? 1U : selected.process_count;
        const std::uint64_t ports = selected.port_count == 0 ? 1U : selected.port_count;
        total += networks * block_protocol_count(selected.protocol_mask) * processes * ports;
    }
    return total <= max_block_filters;
}

// 更早的规则若可能与 block 规则命中同一条连接，它的动作会永远先于 block 生效；而 block 是编译
// 成独立 filter 的，只看条件、不看顺序，会把它拦掉——语义不一致。这里按“可能重叠”拒绝整块 blob：
// 宁可让用户改配置，也不静默改变规则表写下的语义。fallback=block 与任意非 block 规则的组合
// 由 allocate_rule_table 单独拒绝（覆盖全部 TCP/UDP 的 6/17 block filter 会让那些规则永远失效）。
[[nodiscard]] bool rule_order_valid(const blob_view& view, std::uint32_t& earlier, std::uint32_t& later) noexcept
{
    for (later = 0; later < view.rule_count; ++later)
    {
        const rule& blocking = view.rules[later];
        if (blocking.action != rule_action::block)
        {
            continue;
        }
        for (earlier = 0; earlier < later; ++earlier)
        {
            const rule& previous = view.rules[earlier];
            if (previous.action == rule_action::block)
            {
                continue;
            }
            if (protocols_overlap(previous, blocking) && families_overlap(view, previous, blocking) &&
                ports_overlap(view, previous, blocking) && networks_overlap(view, previous, blocking) &&
                processes_overlap(view, previous, blocking))
            {
                return false;
            }
        }
    }
    return true;
}

} // namespace

rule_table* allocate_rule_table(const std::uint8_t* data, std::uint32_t size) noexcept
{
    if (data == nullptr || size == 0 || size > rule_blob_max_size)
    {
        return nullptr;
    }

    auto* table =
        static_cast<rule_table*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(rule_table) + size, rule_pool_tag));
    if (table == nullptr)
    {
        return nullptr;
    }

    RtlZeroMemory(table, sizeof(rule_table));
    table->data = reinterpret_cast<std::uint8_t*>(table) + sizeof(rule_table);
    table->data_size = size;
    RtlCopyMemory(table->data, data, size);

    // 校验原始输入还是副本都一样：副本才是之后真正被求值的那块内存，校验它更有意义。
    if (!try_open_blob(table->data, size, table->view))
    {
        ExFreePoolWithTag(table, rule_pool_tag);
        return nullptr;
    }

    // try_open_blob 已保证 selector 的字节形式合法；block 规则还额外要求 WFP 能精确表达进程条件。
    std::uint32_t unsupported_block_rule = 0;
    if (!block_expression_valid(table->view, unsupported_block_rule))
    {
        DbgPrint("wfprobe: block rule %u has a process selector an auth filter cannot express\n",
                 unsupported_block_rule);
        ExFreePoolWithTag(table, rule_pool_tag);
        return nullptr;
    }

    if (!block_filter_budget_valid(table->view))
    {
        DbgPrint("wfprobe: block rules need more than %u AUTH_CONNECT filters; blob rejected\n", max_block_filters);
        ExFreePoolWithTag(table, rule_pool_tag);
        return nullptr;
    }

    // fallback=block 会为 6/17 各生成一条只带协议条件的 block filter（不再是无条件 filter）；
    // 只要表里还有任何非 block 规则，它们就永远执行不到（classify 会报 permitted，但连接在 auth 层被拒）。
    // <mutation id="fallback-block-conflict">
    if (table->view.fallback_action == rule_action::block)
    // </mutation>
    {
        for (std::uint32_t index = 0; index < table->view.rule_count; ++index)
        {
            if (table->view.rules[index].action != rule_action::block)
            {
                DbgPrint("wfprobe: fallback=block conflicts with non-block rule %u\n", index);
                ExFreePoolWithTag(table, rule_pool_tag);
                return nullptr;
            }
        }
    }

    std::uint32_t earlier_rule = 0;
    std::uint32_t blocking_rule = 0;
    // <mutation id="rule-order-conflict">
    if (!rule_order_valid(table->view, earlier_rule, blocking_rule))
    // </mutation>
    {
        DbgPrint("wfprobe: rule %u would shadow block rule %u; blob rejected\n", earlier_rule, blocking_rule);
        ExFreePoolWithTag(table, rule_pool_tag);
        return nullptr;
    }

    table->contains_block = table->view.fallback_action == rule_action::block;
    for (std::uint32_t index = 0; index < table->view.rule_count && !table->contains_block; ++index)
    {
        table->contains_block = table->view.rules[index].action == rule_action::block;
    }
    return table;
}

void free_rule_table(rule_table* table) noexcept
{
    if (table != nullptr)
    {
        ExFreePoolWithTag(table, rule_pool_tag);
    }
}

void probe_replace_rule_table(rule_table* table) noexcept
{
    probe_state& state = probe_state_ref();

    KIRQL old_irql{};
    const bool raised = probe_lock(old_irql);
    rule_table* previous = state.rules;
    state.rules = table;
    ++state.generation;
    probe_unlock(raised, old_irql);

    // 换指针之后不可能再有新读者拿到旧表；等已拿到的那批退出。等待发生在 PASSIVE_LEVEL
    // 的 IOCTL 路径上，classify 只加/减引用，从不阻塞。
    if (previous != nullptr)
    {
        for (std::uint32_t attempt = 0; attempt < retire_wait_rounds; ++attempt)
        {
            if (references_drained(previous))
            {
                free_rule_table(previous);
                previous = nullptr;
                break;
            }
            wait_one_tick();
        }
        if (previous != nullptr)
        {
            push_retired(previous);
        }
    }

    probe_drain_retired_rules();
}

void probe_drain_retired_rules() noexcept
{
    probe_state& state = probe_state_ref();

    KIRQL old_irql{};
    const bool raised = probe_lock(old_irql);
    rule_table* free_list = nullptr;
    rule_table** link = &state.retired_rules;
    while (*link != nullptr)
    {
        rule_table* current = *link;
        if (references_drained(current))
        {
            *link = current->retired_next;
            current->retired_next = free_list;
            free_list = current;
        }
        else
        {
            link = &current->retired_next;
        }
    }
    probe_unlock(raised, old_irql);

    while (free_list != nullptr)
    {
        rule_table* next = free_list->retired_next;
        free_rule_table(free_list);
        free_list = next;
    }
}

void probe_release_all_rules() noexcept
{
    probe_state& state = probe_state_ref();

    KIRQL old_irql{};
    const bool raised = probe_lock(old_irql);
    rule_table* current = state.rules;
    rule_table* retired = state.retired_rules;
    state.rules = nullptr;
    state.retired_rules = nullptr;
    probe_unlock(raised, old_irql);

    while (retired != nullptr)
    {
        rule_table* next = retired->retired_next;
        free_rule_table(retired);
        retired = next;
    }
    free_rule_table(current);
}

void probe_rule_info_snapshot(probe_rule_info& info) noexcept
{
    probe_state& state = probe_state_ref();

    KIRQL old_irql{};
    const bool raised = probe_lock(old_irql);
    info = probe_rule_info{};
    info.abi_version = probe_abi_version;
    info.installed = state.rules != nullptr ? 1U : 0U;
    info.generation = state.generation;
    if (state.rules != nullptr)
    {
        info.rule_count = state.rules->view.rule_count;
        info.fallback_action = static_cast<std::uint32_t>(state.rules->view.fallback_action);
        info.total_size = state.rules->view.size;
        info.process_pool_size = state.rules->view.process_pool_size;
        info.network_pool_count = state.rules->view.network_pool_count;
    }
    probe_unlock(raised, old_irql);
}

} // namespace winforward::wfp::probe
