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

[[nodiscard]] std::uint8_t effective_family(const blob_view& view, const rule& selected) noexcept
{
    if (selected.family != rule_family_any)
    {
        return selected.family;
    }
    if (selected.network_index == rule_pool_index_any)
    {
        return rule_family_any;
    }
    return view.network_pool[selected.network_index / sizeof(network_prefix)].family;
}

[[nodiscard]] bool protocols_overlap(const rule& first, const rule& second) noexcept
{
    return first.protocol == rule_protocol_any || second.protocol == rule_protocol_any ||
           first.protocol == second.protocol;
}

[[nodiscard]] bool families_overlap(const blob_view& view, const rule& first, const rule& second) noexcept
{
    const std::uint8_t first_family = effective_family(view, first);
    const std::uint8_t second_family = effective_family(view, second);
    return first_family == rule_family_any || second_family == rule_family_any || first_family == second_family;
}

[[nodiscard]] bool ports_overlap(const rule& first, const rule& second) noexcept
{
    return first.remote_port_start <= second.remote_port_end && second.remote_port_start <= first.remote_port_end;
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
    if (first.network_index == rule_pool_index_any || second.network_index == rule_pool_index_any)
    {
        return true;
    }
    return prefixes_intersect(view.network_pool[first.network_index / sizeof(network_prefix)],
                              view.network_pool[second.network_index / sizeof(network_prefix)]);
}

// 进程选择器必须是合法 UTF-8，且 UTF-8 字节数 ≤511（不含 NUL）——和 collect_process_path
// 的边界一致。非法/超长的 selector 在 permit/redirect 规则里会永久静默不命中，必须整块拒绝。
[[nodiscard]] bool valid_utf8_selector(const char* text) noexcept
{
    std::uint32_t length = 0;
    while (text[length] != '\0')
    {
        ++length;
    }
    if (length == 0 || length > 511)
    {
        return false;
    }

    std::uint32_t index = 0;
    while (index < length)
    {
        const auto lead = static_cast<std::uint8_t>(text[index]);
        if (lead < 0x80)
        {
            ++index;
            continue;
        }

        std::uint32_t extra = 0;
        std::uint32_t code_point = 0;
        if ((lead & 0xE0U) == 0xC0U)
        {
            extra = 1;
            code_point = lead & 0x1FU;
            if (code_point < 2)
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
            code_point = (code_point << 6) | (continuation & 0x3FU);
        }
        if ((extra == 2 && code_point < 0x800) || (extra == 3 && (code_point < 0x10000 || code_point > 0x10FFFF)) ||
            (code_point >= 0xD800 && code_point <= 0xDFFF))
        {
            return false;
        }
    }
    return true;
}

[[nodiscard]] std::uint8_t fold_ascii(std::uint8_t byte) noexcept
{
    return byte >= 'A' && byte <= 'Z' ? static_cast<std::uint8_t>(byte + ('a' - 'A')) : byte;
}

[[nodiscard]] bool processes_overlap(const blob_view& view, const rule& first, const rule& second) noexcept
{
    if (first.process_index == rule_pool_index_any || second.process_index == rule_pool_index_any)
    {
        return true;
    }
    // 与 matcher 一致：只折叠 ASCII 大小写，避免仅大小写不同的两条规则被判成“不重叠”。
    const char* first_text = view.process_pool + first.process_index;
    const char* second_text = view.process_pool + second.process_index;
    for (std::uint32_t index = 0;; ++index)
    {
        if (fold_ascii(static_cast<std::uint8_t>(first_text[index])) !=
            fold_ascii(static_cast<std::uint8_t>(second_text[index])))
        {
            return false;
        }
        if (first_text[index] == '\0')
        {
            return true;
        }
    }
}

// 更早的规则若可能与 block 规则命中同一条连接，它的动作会永远先于 block 生效；而 block 是编译
// 成独立 filter 的，只看条件、不看顺序，会把它拦掉——语义不一致。这里按“可能重叠”拒绝整块 blob：
// 宁可让用户改配置，也不静默改变规则表写下的语义。fallback=block 与任意非 block 规则的组合
// 由 allocate_rule_table 单独拒绝（无条件 filter 会让那些规则永远失效）。
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
                ports_overlap(previous, blocking) && networks_overlap(view, previous, blocking) &&
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

    for (std::uint32_t index = 0; index < table->view.rule_count; ++index)
    {
        const rule& selected = table->view.rules[index];
        // <mutation id="selector-utf8-guard">
        if (selected.process_index != rule_pool_index_any &&
            !valid_utf8_selector(table->view.process_pool + selected.process_index))
        // </mutation>
        {
            DbgPrint("wfprobe: rule %u has an invalid or over-long process selector\n", index);
            ExFreePoolWithTag(table, rule_pool_tag);
            return nullptr;
        }
    }

    // fallback=block 会生成无条件 block filter；只要表里还有任何非 block 规则，它们就永远
    // 执行不到（classify 会报 permitted，但连接在 auth 层被拒）。这种矛盾配置整块拒绝。
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

    // 临时（B2 删除）：classify 尚未匹配 UDP，block_filters 却会按 protocol=17 下发 auth filter，
    // 结果是报文被静默丢弃但 classify 没有 blocked/事件记录（spike S3）。B2 转正后删除这段，
    // 并补一条「UDP block 规则可加载」的 VM 用例。
    // <mutation id="udp-block-temporary-guard">
    for (std::uint32_t index = 0; index < table->view.rule_count; ++index)
    {
        const rule& selected = table->view.rules[index];
        if (selected.action == rule_action::block && selected.protocol == rule_protocol_udp)
        {
            DbgPrint("wfprobe: rule %u blocks UDP, but classify does not match UDP yet; blob rejected"
                     " (the B2 change removes this guard)\n",
                     index);
            ExFreePoolWithTag(table, rule_pool_tag);
            return nullptr;
        }
    }
    // </mutation>

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
