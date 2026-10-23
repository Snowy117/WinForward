#include "probe_forwarded_redirect.h"

#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

// FNV-1a：短 key、无分配、DISPATCH 安全。两条索引各自带「域分隔」字节，避免跨 key 碰撞。
constexpr std::uint32_t hash_offset_basis = 2166136261U;
constexpr std::uint32_t hash_prime = 16777619U;

void hash_bytes(std::uint32_t& hash, const std::uint8_t* data, std::uint32_t size) noexcept
{
    for (std::uint32_t index = 0; index < size; ++index)
    {
        hash ^= data[index];
        hash *= hash_prime;
    }
}

void hash_byte(std::uint32_t& hash, std::uint8_t value) noexcept
{
    hash ^= value;
    hash *= hash_prime;
}

void hash_u16(std::uint32_t& hash, std::uint16_t value) noexcept
{
    hash_byte(hash, static_cast<std::uint8_t>(value & 0xFFU));
    hash_byte(hash, static_cast<std::uint8_t>((value >> 8) & 0xFFU));
}

void hash_u32(std::uint32_t& hash, std::uint32_t value) noexcept
{
    hash_byte(hash, static_cast<std::uint8_t>(value & 0xFFU));
    hash_byte(hash, static_cast<std::uint8_t>((value >> 8) & 0xFFU));
    hash_byte(hash, static_cast<std::uint8_t>((value >> 16) & 0xFFU));
    hash_byte(hash, static_cast<std::uint8_t>((value >> 24) & 0xFFU));
}

std::uint32_t ingress_hash(std::uint32_t ingress_ifindex,
                           std::uint8_t family,
                           std::uint8_t protocol,
                           const std::uint8_t* client_address,
                           std::uint16_t client_port,
                           const std::uint8_t* server_address,
                           std::uint16_t server_port) noexcept
{
    std::uint32_t hash = hash_offset_basis;
    hash_byte(hash, 0x49); // 'I'
    hash_u32(hash, ingress_ifindex);
    hash_byte(hash, family);
    hash_byte(hash, protocol);
    hash_bytes(hash, client_address, 16);
    hash_u16(hash, client_port);
    hash_bytes(hash, server_address, 16);
    hash_u16(hash, server_port);
    return hash;
}

std::uint32_t return_hash(std::uint8_t family,
                          std::uint8_t protocol,
                          std::uint16_t listener_port,
                          const std::uint8_t* client_address,
                          std::uint16_t client_port) noexcept
{
    std::uint32_t hash = hash_offset_basis;
    hash_byte(hash, 0x52); // 'R'
    hash_byte(hash, family);
    hash_byte(hash, protocol);
    hash_u16(hash, listener_port);
    hash_bytes(hash, client_address, 16);
    hash_u16(hash, client_port);
    return hash;
}

bool same_ingress_key(const forwarded_redirect_entry& entry,
                      std::uint32_t ingress_ifindex,
                      std::uint8_t family,
                      std::uint8_t protocol,
                      const std::uint8_t* client_address,
                      std::uint16_t client_port,
                      const std::uint8_t* server_address,
                      std::uint16_t server_port) noexcept
{
    return entry.ingress_ifindex == ingress_ifindex && entry.family == family && entry.protocol == protocol &&
           entry.client_port == client_port && entry.server_port == server_port &&
           RtlEqualMemory(entry.client_address, client_address, 16) &&
           RtlEqualMemory(entry.server_address, server_address, 16);
}

bool same_return_key(const forwarded_redirect_entry& entry,
                     std::uint8_t family,
                     std::uint8_t protocol,
                     std::uint16_t listener_port,
                     const std::uint8_t* client_address,
                     std::uint16_t client_port) noexcept
{
    return entry.family == family && entry.protocol == protocol && entry.listener_port == listener_port &&
           entry.client_port == client_port && RtlEqualMemory(entry.client_address, client_address, 16);
}

std::uint64_t now_100ns() noexcept
{
    return static_cast<std::uint64_t>(KeQueryInterruptTime());
}

bool expired(const forwarded_redirect_entry& entry, std::uint64_t now) noexcept
{
    if (entry.state == 0)
    {
        return false;
    }

    std::uint64_t timeout = forwarded_redirect_established_timeout;
    if (entry.protocol == 17)
    {
        // UDP 没有连接状态：首个 datagram 建表后 60s 无活动即回收。
        timeout = forwarded_redirect_udp_timeout;
    }
    else if (entry.state == forwarded_redirect_state_syn)
    {
        timeout = forwarded_redirect_syn_timeout;
    }
    else if (entry.state == forwarded_redirect_state_closing)
    {
        timeout = forwarded_redirect_closing_timeout;
    }
    return now > entry.last_activity && (now - entry.last_activity) > timeout;
}

void fill_snapshot(const forwarded_redirect_entry& entry, forwarded_redirect_snapshot& snapshot) noexcept
{
    snapshot.ingress_ifindex = entry.ingress_ifindex;
    snapshot.ndis_port = entry.ndis_port;
    snapshot.upstream_id = entry.upstream_id;
    snapshot.rule_index = entry.rule_index;
    snapshot.generation = entry.generation;
    snapshot.state = entry.state;
    snapshot.family = entry.family;
    snapshot.protocol = entry.protocol;
    snapshot.client_port = entry.client_port;
    snapshot.server_port = entry.server_port;
    snapshot.listener_port = entry.listener_port;
    RtlCopyMemory(snapshot.client_mac, entry.client_mac, 6);
    RtlCopyMemory(snapshot.local_mac, entry.local_mac, 6);
    RtlCopyMemory(snapshot.client_address, entry.client_address, 16);
    RtlCopyMemory(snapshot.server_address, entry.server_address, 16);
}

void rebuild_free_list(forwarded_redirect_table& table) noexcept
{
    table.free_head = table.entries != nullptr ? 1 : 0;
    if (table.entries == nullptr)
    {
        return;
    }

    for (std::uint32_t index = 0; index < forwarded_redirect_capacity; ++index)
    {
        forwarded_redirect_entry& entry = table.entries[index];
        entry.state = 0;
        entry.next_ingress = 0;
        entry.next_return = 0;
        entry.next_free = (index + 1 < forwarded_redirect_capacity) ? index + 2 : 0;
    }
    table.active = 0;
}

void remove_entry(forwarded_redirect_table& table, std::uint32_t index) noexcept
{
    forwarded_redirect_entry& entry = table.entries[index];
    if (entry.state == 0)
    {
        return;
    }

    const std::uint32_t ingress_bucket = ingress_hash(entry.ingress_ifindex,
                                                      entry.family,
                                                      entry.protocol,
                                                      entry.client_address,
                                                      entry.client_port,
                                                      entry.server_address,
                                                      entry.server_port) &
                                         forwarded_redirect_bucket_mask;
    std::uint32_t* link = &table.ingress_buckets[ingress_bucket];
    while (*link != 0)
    {
        const std::uint32_t current = *link - 1;
        if (current == index)
        {
            *link = table.entries[current].next_ingress;
            break;
        }
        link = &table.entries[current].next_ingress;
    }

    const std::uint32_t return_bucket =
        return_hash(entry.family, entry.protocol, entry.listener_port, entry.client_address, entry.client_port) &
        forwarded_redirect_bucket_mask;
    link = &table.return_buckets[return_bucket];
    while (*link != 0)
    {
        const std::uint32_t current = *link - 1;
        if (current == index)
        {
            *link = table.entries[current].next_return;
            break;
        }
        link = &table.entries[current].next_return;
    }

    entry.state = 0;
    entry.next_ingress = 0;
    entry.next_return = 0;
    entry.next_free = table.free_head;
    table.free_head = index + 1;
    if (table.active > 0)
    {
        --table.active;
    }
}

forwarded_redirect_entry* find_ingress_entry(forwarded_redirect_table& table,
                                             std::uint32_t ingress_ifindex,
                                             std::uint8_t family,
                                             std::uint8_t protocol,
                                             const std::uint8_t* client_address,
                                             std::uint16_t client_port,
                                             const std::uint8_t* server_address,
                                             std::uint16_t server_port,
                                             std::uint32_t& index) noexcept
{
    const std::uint32_t bucket =
        ingress_hash(ingress_ifindex, family, protocol, client_address, client_port, server_address, server_port) &
        forwarded_redirect_bucket_mask;
    std::uint32_t link = table.ingress_buckets[bucket];
    while (link != 0)
    {
        const std::uint32_t current = link - 1;
        forwarded_redirect_entry& entry = table.entries[current];
        if (same_ingress_key(
                entry, ingress_ifindex, family, protocol, client_address, client_port, server_address, server_port))
        {
            index = current;
            return &entry;
        }
        link = entry.next_ingress;
    }
    return nullptr;
}

forwarded_redirect_entry* find_return_entry(forwarded_redirect_table& table,
                                            std::uint8_t family,
                                            std::uint8_t protocol,
                                            std::uint16_t listener_port,
                                            const std::uint8_t* client_address,
                                            std::uint16_t client_port,
                                            std::uint32_t& index) noexcept
{
    const std::uint32_t bucket =
        return_hash(family, protocol, listener_port, client_address, client_port) & forwarded_redirect_bucket_mask;
    std::uint32_t link = table.return_buckets[bucket];
    while (link != 0)
    {
        const std::uint32_t current = link - 1;
        forwarded_redirect_entry& entry = table.entries[current];
        if (same_return_key(entry, family, protocol, listener_port, client_address, client_port))
        {
            index = current;
            return &entry;
        }
        link = entry.next_return;
    }
    return nullptr;
}

} // namespace

bool forwarded_redirect_init(forwarded_redirect_table& table) noexcept
{
    RtlZeroMemory(&table, sizeof(table));
    table.entries = static_cast<forwarded_redirect_entry*>(ExAllocatePool2(
        POOL_FLAG_NON_PAGED, sizeof(forwarded_redirect_entry) * forwarded_redirect_capacity, rule_pool_tag));
    table.ingress_buckets = static_cast<std::uint32_t*>(
        ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(std::uint32_t) * forwarded_redirect_bucket_count, rule_pool_tag));
    table.return_buckets = static_cast<std::uint32_t*>(
        ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(std::uint32_t) * forwarded_redirect_bucket_count, rule_pool_tag));
    if (table.entries == nullptr || table.ingress_buckets == nullptr || table.return_buckets == nullptr)
    {
        forwarded_redirect_free(table);
        return false;
    }

    RtlZeroMemory(table.entries, sizeof(forwarded_redirect_entry) * forwarded_redirect_capacity);
    RtlZeroMemory(table.ingress_buckets, sizeof(std::uint32_t) * forwarded_redirect_bucket_count);
    RtlZeroMemory(table.return_buckets, sizeof(std::uint32_t) * forwarded_redirect_bucket_count);
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

    // 客户端端口复用：回程 key 是唯一的，旧表项必须先删掉，否则回程会命中旧连接。
    std::uint32_t conflict = 0;
    if (find_return_entry(table,
                          create.family,
                          create.protocol,
                          create.listener_port,
                          create.client_address,
                          create.client_port,
                          conflict) != nullptr)
    {
        remove_entry(table, conflict);
        if (table.free_head == 0)
        {
            return false;
        }
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

    const std::uint32_t return_bucket =
        return_hash(create.family, create.protocol, create.listener_port, create.client_address, create.client_port) &
        forwarded_redirect_bucket_mask;
    entry.next_return = table.return_buckets[return_bucket];
    table.return_buckets[return_bucket] = index + 1;

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
                                                         std::uint16_t listener_port,
                                                         const std::uint8_t* client_address,
                                                         std::uint16_t client_port,
                                                         forwarded_redirect_snapshot& snapshot) noexcept
{
    if (table.entries == nullptr)
    {
        return forwarded_redirect_lookup::missing;
    }

    std::uint32_t index = 0;
    forwarded_redirect_entry* entry =
        find_return_entry(table, family, protocol, listener_port, client_address, client_port, index);
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
    fill_snapshot(*entry, snapshot);
    return forwarded_redirect_lookup::found;
}

bool forwarded_redirect_mark_closing(forwarded_redirect_table& table,
                                     std::uint8_t family,
                                     std::uint8_t protocol,
                                     std::uint16_t listener_port,
                                     const std::uint8_t* client_address,
                                     std::uint16_t client_port) noexcept
{
    if (table.entries == nullptr)
    {
        return false;
    }

    std::uint32_t index = 0;
    forwarded_redirect_entry* entry =
        find_return_entry(table, family, protocol, listener_port, client_address, client_port, index);
    if (entry == nullptr)
    {
        return false;
    }

    entry->state = forwarded_redirect_state_closing;
    entry->last_activity = now_100ns();
    return true;
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
