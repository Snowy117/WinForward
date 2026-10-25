#pragma once

#include <cstdint>

#include "rule_blob.h"

namespace winforward::wfp::probe
{

// 规则表：一块池内存里的 blob 副本 + 引用计数。
//
// 这里不用 EX_RUNDOWN_REF：classify 在 DISPATCH_LEVEL 运行，而 ExAcquireRundownProtection
// 只允许 IRQL <= APC_LEVEL。等价语义由「自旋锁保护指针 + 每个读者一份引用」承担：
// 读者在锁内取指针并加引用，写者在同一把锁内换指针，之后等旧表的引用归零再释放。
// 于是「换表之后不可能再有人拿到旧表」是锁保证的，「换表之前拿到的读者」由引用计数等到。
struct rule_table
{
    std::uint8_t* data; // 池块内的 blob 副本，4 字节对齐
    std::uint32_t data_size;
    blob_view view; // 指向 data，不拥有内存
    volatile LONG references; // 正在使用本表的 classify 数量
    rule_table* retired_next; // 引用迟迟不归零时挂到退役链
    bool contains_block; // 是否需要固定 sink filter：fallback = block，或任一规则 action = block
};

static_assert(sizeof(rule_table) % 4 == 0, "blob 副本必须紧跟在结构体后面且保持 4 字节对齐");

// 校验并复制一份 blob。失败返回 nullptr，调用方据此保持当前表不变。
[[nodiscard]] rule_table* allocate_rule_table(const std::uint8_t* data, std::uint32_t size) noexcept;
void free_rule_table(rule_table* table) noexcept;

// 原子换表：新表就位、generation+1，旧表在引用归零后释放（超时则进退役链）。
void probe_replace_rule_table(rule_table* table) noexcept;
void probe_drain_retired_rules() noexcept;

// 卸载路径：callout 已注销，不可能再有读者，直接释放当前表与全部退役表。
void probe_release_all_rules() noexcept;

void probe_rule_info_snapshot(probe_rule_info& info) noexcept;

} // namespace winforward::wfp::probe
