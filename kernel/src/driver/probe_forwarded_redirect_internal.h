#pragma once

// forwarded redirect 表的哈希/键比较/表项维护内部工具。无 WDK 依赖之外的副作用：
// 调用方必须持有 g_forwarded_state.lock。

#include "probe_forwarded_redirect.h"

namespace winforward::wfp::probe
{
namespace detail
{

// FNV-1a：短 key、无分配、DISPATCH 安全。三条索引各自带「域分隔」字节，避免跨 key 碰撞。
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
                          const std::uint8_t* listener_address,
                          std::uint16_t listener_port,
                          const std::uint8_t* client_address,
                          std::uint16_t client_port,
                          const std::uint8_t* server_address,
                          std::uint16_t server_port) noexcept
{
    std::uint32_t hash = hash_offset_basis;
    hash_byte(hash, 0x52); // 'R'
    hash_byte(hash, family);
    hash_byte(hash, protocol);
    hash_bytes(hash, listener_address, 16);
    hash_u16(hash, listener_port);
    hash_bytes(hash, client_address, 16);
    hash_u16(hash, client_port);
    hash_bytes(hash, server_address, 16);
    hash_u16(hash, server_port);
    return hash;
}

std::uint32_t candidate_hash(std::uint8_t family,
                             std::uint8_t protocol,
                             const std::uint8_t* listener_address,
                             std::uint16_t listener_port,
                             const std::uint8_t* client_address,
                             std::uint16_t client_port) noexcept
{
    std::uint32_t hash = hash_offset_basis;
    hash_byte(hash, 0x43); // 'C'
    hash_byte(hash, family);
    hash_byte(hash, protocol);
    hash_bytes(hash, listener_address, 16);
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
                     const std::uint8_t* listener_address,
                     std::uint16_t listener_port,
                     const std::uint8_t* client_address,
                     std::uint16_t client_port,
                     const std::uint8_t* server_address,
                     std::uint16_t server_port) noexcept
{
    return entry.family == family && entry.protocol == protocol && entry.listener_port == listener_port &&
           entry.client_port == client_port && entry.server_port == server_port &&
           RtlEqualMemory(entry.listener_address, listener_address, 16) &&
           RtlEqualMemory(entry.client_address, client_address, 16) &&
           RtlEqualMemory(entry.server_address, server_address, 16);
}

bool same_candidate_key(const forwarded_redirect_entry& entry,
                        std::uint8_t family,
                        std::uint8_t protocol,
                        const std::uint8_t* listener_address,
                        std::uint16_t listener_port,
                        const std::uint8_t* client_address,
                        std::uint16_t client_port) noexcept
{
    return entry.family == family && entry.protocol == protocol && entry.listener_port == listener_port &&
           entry.client_port == client_port && RtlEqualMemory(entry.listener_address, listener_address, 16) &&
           RtlEqualMemory(entry.client_address, client_address, 16);
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
    RtlCopyMemory(snapshot.listener_address, entry.listener_address, 16);
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
        entry.next_candidate = 0;
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

    const std::uint32_t return_bucket = return_hash(entry.family,
                                                    entry.protocol,
                                                    entry.listener_address,
                                                    entry.listener_port,
                                                    entry.client_address,
                                                    entry.client_port,
                                                    entry.server_address,
                                                    entry.server_port) &
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

    const std::uint32_t candidate_bucket = candidate_hash(entry.family,
                                                          entry.protocol,
                                                          entry.listener_address,
                                                          entry.listener_port,
                                                          entry.client_address,
                                                          entry.client_port) &
                                           forwarded_redirect_bucket_mask;
    link = &table.candidate_buckets[candidate_bucket];
    while (*link != 0)
    {
        const std::uint32_t current = *link - 1;
        if (current == index)
        {
            *link = table.entries[current].next_candidate;
            break;
        }
        link = &table.entries[current].next_candidate;
    }

    entry.state = 0;
    entry.next_ingress = 0;
    entry.next_return = 0;
    entry.next_candidate = 0;
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
// cppcheck-suppress unusedFunction
forwarded_redirect_entry* find_return_entry(forwarded_redirect_table& table,
                                            std::uint8_t family,
                                            std::uint8_t protocol,
                                            const std::uint8_t* listener_address,
                                            std::uint16_t listener_port,
                                            const std::uint8_t* client_address,
                                            std::uint16_t client_port,
                                            const std::uint8_t* server_address,
                                            std::uint16_t server_port,
                                            std::uint32_t& index) noexcept
{
    const std::uint32_t bucket = return_hash(family,
                                             protocol,
                                             listener_address,
                                             listener_port,
                                             client_address,
                                             client_port,
                                             server_address,
                                             server_port) &
                                 forwarded_redirect_bucket_mask;
    std::uint32_t link = table.return_buckets[bucket];
    while (link != 0)
    {
        const std::uint32_t current = link - 1;
        forwarded_redirect_entry& entry = table.entries[current];
        if (same_return_key(entry,
                            family,
                            protocol,
                            listener_address,
                            listener_port,
                            client_address,
                            client_port,
                            server_address,
                            server_port))
        {
            index = current;
            return &entry;
        }
        link = entry.next_return;
    }
    return nullptr;
}

forwarded_redirect_entry* find_candidate_entry(forwarded_redirect_table& table,
                                               std::uint8_t family,
                                               std::uint8_t protocol,
                                               const std::uint8_t* listener_address,
                                               std::uint16_t listener_port,
                                               const std::uint8_t* client_address,
                                               std::uint16_t client_port,
                                               std::uint32_t& index) noexcept
{
    const std::uint32_t bucket =
        candidate_hash(family, protocol, listener_address, listener_port, client_address, client_port) &
        forwarded_redirect_bucket_mask;
    std::uint32_t link = table.candidate_buckets[bucket];
    while (link != 0)
    {
        const std::uint32_t current = link - 1;
        forwarded_redirect_entry& entry = table.entries[current];
        if (same_candidate_key(entry, family, protocol, listener_address, listener_port, client_address, client_port))
        {
            index = current;
            return &entry;
        }
        link = entry.next_candidate;
    }
    return nullptr;
}

// 清掉候选链上的过期表项，返回移除数量。调用方随后再数存活候选。
std::uint32_t prune_expired_candidates(forwarded_redirect_table& table,
                                       std::uint8_t family,
                                       std::uint8_t protocol,
                                       const std::uint8_t* listener_address,
                                       std::uint16_t listener_port,
                                       const std::uint8_t* client_address,
                                       std::uint16_t client_port,
                                       std::uint64_t now) noexcept
{
    const std::uint32_t bucket =
        candidate_hash(family, protocol, listener_address, listener_port, client_address, client_port) &
        forwarded_redirect_bucket_mask;
    std::uint32_t removed = 0;
    for (;;)
    {
        std::uint32_t link = table.candidate_buckets[bucket];
        bool dropped = false;
        while (link != 0)
        {
            const std::uint32_t current = link - 1;
            forwarded_redirect_entry& entry = table.entries[current];
            if (expired(entry, now))
            {
                remove_entry(table, current);
                ++removed;
                dropped = true;
                break;
            }
            link = entry.next_candidate;
        }

        if (!dropped)
        {
            return removed;
        }
    }
}

} // namespace detail
} // namespace winforward::wfp::probe
