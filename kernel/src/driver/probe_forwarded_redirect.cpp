#include "probe_forwarded_redirect.h"

#include "probe_driver.h"
#include "probe_forwarded_redirect_internal.h"

namespace winforward::wfp::probe
{
using namespace detail;

bool forwarded_redirect_init(forwarded_redirect_table& table) noexcept
{
    RtlZeroMemory(&table, sizeof(table));
    table.entries = static_cast<forwarded_redirect_entry*>(ExAllocatePool2(
        POOL_FLAG_NON_PAGED, sizeof(forwarded_redirect_entry) * forwarded_redirect_capacity, rule_pool_tag));
    table.ingress_buckets = static_cast<std::uint32_t*>(
        ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(std::uint32_t) * forwarded_redirect_bucket_count, rule_pool_tag));
    table.return_buckets = static_cast<std::uint32_t*>(
        ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(std::uint32_t) * forwarded_redirect_bucket_count, rule_pool_tag));
    table.candidate_buckets = static_cast<std::uint32_t*>(
        ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(std::uint32_t) * forwarded_redirect_bucket_count, rule_pool_tag));
    if (table.entries == nullptr || table.ingress_buckets == nullptr || table.return_buckets == nullptr ||
        table.candidate_buckets == nullptr)
    {
        forwarded_redirect_free(table);
        return false;
    }

    RtlZeroMemory(table.entries, sizeof(forwarded_redirect_entry) * forwarded_redirect_capacity);
    RtlZeroMemory(table.ingress_buckets, sizeof(std::uint32_t) * forwarded_redirect_bucket_count);
    RtlZeroMemory(table.return_buckets, sizeof(std::uint32_t) * forwarded_redirect_bucket_count);
    RtlZeroMemory(table.candidate_buckets, sizeof(std::uint32_t) * forwarded_redirect_bucket_count);
    rebuild_free_list(table);
    return true;
}

void forwarded_redirect_free(forwarded_redirect_table& table) noexcept
{
    if (table.entries != nullptr)
    {
        ExFreePoolWithTag(table.entries, rule_pool_tag);
        table.entries = nullptr;
    }
    if (table.ingress_buckets != nullptr)
    {
        ExFreePoolWithTag(table.ingress_buckets, rule_pool_tag);
        table.ingress_buckets = nullptr;
    }
    if (table.return_buckets != nullptr)
    {
        ExFreePoolWithTag(table.return_buckets, rule_pool_tag);
        table.return_buckets = nullptr;
    }
    if (table.candidate_buckets != nullptr)
    {
        ExFreePoolWithTag(table.candidate_buckets, rule_pool_tag);
        table.candidate_buckets = nullptr;
    }
    table.active = 0;
    table.high_water = 0;
    table.free_head = 0;
}

void forwarded_redirect_clear(forwarded_redirect_table& table) noexcept
{
    if (table.entries == nullptr)
    {
        return;
    }

    ++table.generation;
    rebuild_free_list(table);
    RtlZeroMemory(table.ingress_buckets, sizeof(std::uint32_t) * forwarded_redirect_bucket_count);
    RtlZeroMemory(table.return_buckets, sizeof(std::uint32_t) * forwarded_redirect_bucket_count);
    RtlZeroMemory(table.candidate_buckets, sizeof(std::uint32_t) * forwarded_redirect_bucket_count);
}

forwarded_redirect_lookup forwarded_redirect_find_ingress(forwarded_redirect_table& table,
                                                          std::uint32_t ingress_ifindex,
                                                          std::uint8_t family,
                                                          std::uint8_t protocol,
                                                          const std::uint8_t* client_address,
                                                          std::uint16_t client_port,
                                                          const std::uint8_t* server_address,
                                                          std::uint16_t server_port,
                                                          forwarded_redirect_snapshot& snapshot) noexcept
{
    if (table.entries == nullptr)
    {
        return forwarded_redirect_lookup::missing;
    }

    std::uint32_t index = 0;
    forwarded_redirect_entry* entry = find_ingress_entry(
        table, ingress_ifindex, family, protocol, client_address, client_port, server_address, server_port, index);
    if (entry == nullptr)
    {
        return forwarded_redirect_lookup::missing;
    }

    const std::uint64_t now = now_100ns();
    if (expired(*entry, now))
    {
        remove_entry(table, index);
        return forwarded_redirect_lookup::expired;
    }

    entry->last_activity = now;
    if (entry->state == forwarded_redirect_state_syn)
    {
        entry->state = forwarded_redirect_state_established;
    }
    fill_snapshot(*entry, snapshot);
    return forwarded_redirect_lookup::found;
}

bool forwarded_redirect_create(forwarded_redirect_table& table,
                               const forwarded_redirect_request& create,
                               forwarded_redirect_snapshot& snapshot) noexcept
{
    if (table.entries == nullptr)
    {
        return false;
    }

    if (table.free_head == 0)
    {
        (void)forwarded_redirect_sweep(table);
    }
    if (table.free_head == 0)
    {
        return false;
    }

    // 客户端端口复用：同一个 (L, listener_port, client) 上只能有一条存活表项，否则回程无法
    // 区分 server。多 L 选择会优先挑空闲 L；只有所有 L 都占用时才会走到这里的替换路径。
    for (;;)
    {
        std::uint32_t conflict = 0;
        if (find_candidate_entry(table,
                                 create.family,
                                 create.protocol,
                                 create.listener_address,
                                 create.listener_port,
                                 create.client_address,
                                 create.client_port,
                                 conflict) == nullptr)
        {
            break;
        }
        remove_entry(table, conflict);
    }

    const std::uint32_t index = table.free_head - 1;
    forwarded_redirect_entry& entry = table.entries[index];
    table.free_head = entry.next_free;

    RtlZeroMemory(&entry, sizeof(entry));
    entry.ingress_ifindex = create.ingress_ifindex;
    entry.ndis_port = create.ndis_port;
    entry.upstream_id = create.upstream_id;
    entry.rule_index = create.rule_index;
    entry.generation = create.generation;
    entry.state = create.protocol == 17 ? forwarded_redirect_state_established : forwarded_redirect_state_syn;
    entry.last_activity = now_100ns();
    entry.family = create.family;
    entry.protocol = create.protocol;
    entry.client_port = create.client_port;
    entry.server_port = create.server_port;
    entry.listener_port = create.listener_port;
    RtlCopyMemory(entry.client_mac, create.client_mac, 6);
    RtlCopyMemory(entry.local_mac, create.local_mac, 6);
    RtlCopyMemory(entry.client_address, create.client_address, 16);
    RtlCopyMemory(entry.server_address, create.server_address, 16);
    RtlCopyMemory(entry.listener_address, create.listener_address, 16);

    const std::uint32_t ingress_bucket = ingress_hash(create.ingress_ifindex,
                                                      create.family,
                                                      create.protocol,
                                                      create.client_address,
                                                      create.client_port,
                                                      create.server_address,
                                                      create.server_port) &
                                         forwarded_redirect_bucket_mask;
    entry.next_ingress = table.ingress_buckets[ingress_bucket];
    table.ingress_buckets[ingress_bucket] = index + 1;

    const std::uint32_t return_bucket = return_hash(create.family,
                                                    create.protocol,
                                                    create.listener_address,
                                                    create.listener_port,
                                                    create.client_address,
                                                    create.client_port,
                                                    create.server_address,
                                                    create.server_port) &
                                        forwarded_redirect_bucket_mask;
    entry.next_return = table.return_buckets[return_bucket];
    table.return_buckets[return_bucket] = index + 1;

    const std::uint32_t candidate_bucket = candidate_hash(create.family,
                                                          create.protocol,
                                                          create.listener_address,
                                                          create.listener_port,
                                                          create.client_address,
                                                          create.client_port) &
                                           forwarded_redirect_bucket_mask;
    entry.next_candidate = table.candidate_buckets[candidate_bucket];
    table.candidate_buckets[candidate_bucket] = index + 1;

    ++table.active;
    if (table.active > table.high_water)
    {
        table.high_water = table.active;
    }

    fill_snapshot(entry, snapshot);
    return true;
}

forwarded_redirect_lookup forwarded_redirect_find_return(forwarded_redirect_table& table,
                                                         std::uint8_t family,
                                                         std::uint8_t protocol,
                                                         const std::uint8_t* listener_address,
                                                         std::uint16_t listener_port,
                                                         const std::uint8_t* client_address,
                                                         std::uint16_t client_port,
                                                         forwarded_redirect_snapshot& snapshot) noexcept
{
    if (table.entries == nullptr)
    {
        return forwarded_redirect_lookup::missing;
    }

    const std::uint64_t now = now_100ns();
    if (listener_address == nullptr)
    {
        // v6 查询没有 L：控制面线性扫描。帧路径永远带 L，不走这里。
        std::uint32_t match = 0;
        std::uint32_t matches = 0;
        for (std::uint32_t index = 0; index < forwarded_redirect_capacity; ++index)
        {
            const forwarded_redirect_entry& entry = table.entries[index];
            if (entry.state == 0 || entry.family != family || entry.protocol != protocol ||
                entry.listener_port != listener_port || entry.client_port != client_port ||
                !RtlEqualMemory(entry.client_address, client_address, 16) || expired(entry, now))
            {
                continue;
            }

            match = index;
            if (++matches > 1)
            {
                return forwarded_redirect_lookup::ambiguous;
            }
        }

        if (matches == 0)
        {
            return forwarded_redirect_lookup::missing;
        }

        table.entries[match].last_activity = now;
        fill_snapshot(table.entries[match], snapshot);
        return forwarded_redirect_lookup::found;
    }

    const std::uint32_t expired_removed = prune_expired_candidates(
        table, family, protocol, listener_address, listener_port, client_address, client_port, now);

    std::uint32_t match = 0;
    std::uint32_t matches = 0;
    const std::uint32_t bucket =
        candidate_hash(family, protocol, listener_address, listener_port, client_address, client_port) &
        forwarded_redirect_bucket_mask;
    std::uint32_t link = table.candidate_buckets[bucket];
    while (link != 0)
    {
        const std::uint32_t index = link - 1;
        forwarded_redirect_entry& entry = table.entries[index];
        if (same_candidate_key(entry, family, protocol, listener_address, listener_port, client_address, client_port))
        {
            match = index;
            ++matches;
        }
        link = entry.next_candidate;
    }

    if (matches == 0)
    {
        return expired_removed > 0 ? forwarded_redirect_lookup::expired : forwarded_redirect_lookup::missing;
    }
    if (matches > 1)
    {
        return forwarded_redirect_lookup::ambiguous;
    }

    table.entries[match].last_activity = now;
    fill_snapshot(table.entries[match], snapshot);
    return forwarded_redirect_lookup::found;
}

bool forwarded_redirect_has_return(forwarded_redirect_table& table,
                                   std::uint8_t family,
                                   std::uint8_t protocol,
                                   const std::uint8_t* listener_address,
                                   std::uint16_t listener_port,
                                   const std::uint8_t* client_address,
                                   std::uint16_t client_port) noexcept
{
    if (table.entries == nullptr)
    {
        return false;
    }

    const std::uint64_t now = now_100ns();
    const std::uint32_t bucket =
        candidate_hash(family, protocol, listener_address, listener_port, client_address, client_port) &
        forwarded_redirect_bucket_mask;
    std::uint32_t link = table.candidate_buckets[bucket];
    while (link != 0)
    {
        const std::uint32_t index = link - 1;
        const forwarded_redirect_entry& entry = table.entries[index];
        if (same_candidate_key(entry, family, protocol, listener_address, listener_port, client_address, client_port) &&
            !expired(entry, now))
        {
            return true;
        }
        link = entry.next_candidate;
    }
    return false;
}

bool forwarded_redirect_mark_closing(forwarded_redirect_table& table,

                                     std::uint8_t family,
                                     std::uint8_t protocol,
                                     const std::uint8_t* listener_address,
                                     std::uint16_t listener_port,
                                     const std::uint8_t* client_address,
                                     std::uint16_t client_port) noexcept
{
    if (table.entries == nullptr)
    {
        return false;
    }

    forwarded_redirect_snapshot snapshot{};
    if (forwarded_redirect_find_return(
            table, family, protocol, listener_address, listener_port, client_address, client_port, snapshot) !=
        forwarded_redirect_lookup::found)
    {
        return false;
    }

    // find_return 已经刷新 last_activity；这里只需要把状态推进到 closing。
    const std::uint32_t bucket =
        candidate_hash(family, protocol, listener_address, listener_port, client_address, client_port) &
        forwarded_redirect_bucket_mask;
    std::uint32_t link = table.candidate_buckets[bucket];
    while (link != 0)
    {
        const std::uint32_t index = link - 1;
        forwarded_redirect_entry& entry = table.entries[index];
        if (same_candidate_key(entry, family, protocol, listener_address, listener_port, client_address, client_port))
        {
            entry.state = forwarded_redirect_state_closing;
            return true;
        }
        link = entry.next_candidate;
    }
    return false;
}

std::uint32_t forwarded_redirect_sweep(forwarded_redirect_table& table) noexcept
{
    if (table.entries == nullptr)
    {
        return 0;
    }

    const std::uint64_t now = now_100ns();
    std::uint32_t removed = 0;
    for (std::uint32_t index = 0; index < forwarded_redirect_capacity; ++index)
    {
        if (table.entries[index].state != 0 && expired(table.entries[index], now))
        {
            remove_entry(table, index);
            ++removed;
        }
    }
    return removed;
}

} // namespace winforward::wfp::probe
