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
    // block 的顺序、选择器表达与 filter 预算都不再在换表时判断：block 只参与 classify 的
    // 首个命中，真正的拒绝由 sink 端口上的固定 filter 完成（block_filters.cpp）。
    if (!try_open_blob(table->data, size, table->view))
    {
        ExFreePoolWithTag(table, rule_pool_tag);
        return nullptr;
    }

    // 控制面据此决定是否安装 / 保留固定 sink filter；与 classify 的首个命中判定无关。
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
