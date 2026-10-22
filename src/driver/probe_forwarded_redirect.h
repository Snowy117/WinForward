#pragma once

// forwarded redirect 表的纯数据结构与操作。没有 WDK 依赖，也不做加锁：所有函数都要求调用方
// 已持有 g_forwarded_state.lock（classify 在 DISPATCH_LEVEL，IOCTL 在 PASSIVE_LEVEL）。
//
// 两条索引指向同一批表项：
//   - 入口 key：(ingress_ifindex, family, client_ip, client_port, server_ip, server_port)
//   - 回程 key：(family, listener_port, client_ip, client_port)
// 回程 key 必须唯一：同一个 listener 端口上，一个客户端 (ip, port) 只可能属于一条被重定向的连接。
// 建表时若发现回程 key 已存在，旧表项先被移除（客户端端口复用），再插入新表项。

#include <cstdint>

namespace winforward::wfp::probe
{

inline constexpr std::uint32_t forwarded_redirect_capacity = 4096;
inline constexpr std::uint32_t forwarded_redirect_bucket_count = 2048; // 必须是 2 的幂
inline constexpr std::uint32_t forwarded_redirect_bucket_mask = forwarded_redirect_bucket_count - 1;

// 超时（KeQueryInterruptTime 的 100ns 单位）。入口 SYN 建表后 30s 没等到非 SYN 数据就回收；
// 已建立连接 300s 无活动回收；FIN/RST 之后留 5s grace，让迟到的 ACK/FIN 还能命中。
inline constexpr std::uint64_t forwarded_redirect_syn_timeout = 30ull * 10ull * 1000ull * 1000ull;
inline constexpr std::uint64_t forwarded_redirect_established_timeout = 300ull * 10ull * 1000ull * 1000ull;
inline constexpr std::uint64_t forwarded_redirect_closing_timeout = 5ull * 10ull * 1000ull * 1000ull;

// 表项内部布局。state == 0 表示空闲槽位；next_* / next_free 都存「下标 + 1」，0 表示链尾。
struct forwarded_redirect_entry
{
    std::uint32_t ingress_ifindex;
    std::uint32_t ndis_port;
    std::uint32_t upstream_id;
    std::uint32_t rule_index;
    std::uint32_t generation;
    std::uint32_t state;
    std::uint64_t last_activity;
    std::uint8_t family;
    std::uint8_t client_mac[6];
    std::uint8_t local_mac[6];
    std::uint8_t client_address[16];
    std::uint8_t server_address[16];
    std::uint16_t client_port;
    std::uint16_t server_port;
    std::uint16_t listener_port;
    std::uint16_t reserved0;
    std::uint32_t next_ingress;
    std::uint32_t next_return;
    std::uint32_t next_free;
};

struct forwarded_redirect_table
{
    forwarded_redirect_entry* entries;
    std::uint32_t* ingress_buckets;
    std::uint32_t* return_buckets;
    std::uint32_t free_head; // 下标 + 1
    std::uint32_t active;
    std::uint32_t high_water;
    std::uint32_t generation;
};

// 建表输入。地址均为 16 字节存储，IPv4 只用前 4 字节，其余补零。
struct forwarded_redirect_request
{
    std::uint32_t ingress_ifindex;
    std::uint32_t ndis_port;
    std::uint32_t upstream_id;
    std::uint32_t rule_index;
    std::uint32_t generation;
    std::uint8_t family;
    std::uint8_t client_mac[6];
    std::uint8_t local_mac[6];
    std::uint8_t client_address[16];
    std::uint8_t server_address[16];
    std::uint16_t client_port;
    std::uint16_t server_port;
    std::uint16_t listener_port;
};

// 查表 / 查询 IOCTL 的拷贝快照。表项可能在 classify 释放锁之后被清表，所以只带值不带指针。
struct forwarded_redirect_snapshot
{
    std::uint32_t ingress_ifindex;
    std::uint32_t ndis_port;
    std::uint32_t upstream_id;
    std::uint32_t rule_index;
    std::uint32_t generation;
    std::uint32_t state;
    std::uint8_t family;
    std::uint8_t client_mac[6];
    std::uint8_t local_mac[6];
    std::uint8_t client_address[16];
    std::uint8_t server_address[16];
    std::uint16_t client_port;
    std::uint16_t server_port;
    std::uint16_t listener_port;
};

enum class forwarded_redirect_lookup : std::uint8_t
{
    found = 0,
    missing = 1,
    expired = 2,
    remove_conflict = 3,
};

[[nodiscard]] bool forwarded_redirect_init(forwarded_redirect_table& table) noexcept;
void forwarded_redirect_free(forwarded_redirect_table& table) noexcept;
void forwarded_redirect_clear(forwarded_redirect_table& table) noexcept;

// 返回值：found / missing / expired。expired 表示命中了一个已超时的表项，调用方按 missing 处理。
[[nodiscard]] forwarded_redirect_lookup forwarded_redirect_find_ingress(forwarded_redirect_table& table,
                                                                        std::uint32_t ingress_ifindex,
                                                                        std::uint8_t family,
                                                                        const std::uint8_t* client_address,
                                                                        std::uint16_t client_port,
                                                                        const std::uint8_t* server_address,
                                                                        std::uint16_t server_port,
                                                                        forwarded_redirect_snapshot& snapshot) noexcept;

// SYN 建表。容量满时先扫一遍过期表项；仍然满则返回 false（调用方 block + capacity 计数）。
[[nodiscard]] bool forwarded_redirect_create(forwarded_redirect_table& table,
                                             const forwarded_redirect_request& create,
                                             forwarded_redirect_snapshot& snapshot) noexcept;

[[nodiscard]] forwarded_redirect_lookup forwarded_redirect_find_return(forwarded_redirect_table& table,
                                                                       std::uint8_t family,
                                                                       std::uint16_t listener_port,
                                                                       const std::uint8_t* client_address,
                                                                       std::uint16_t client_port,
                                                                       forwarded_redirect_snapshot& snapshot) noexcept;

// 标记 closing（FIN/RST），保留 grace 窗口；不立即释放槽位。返回 true 表示确实找到了表项。
[[nodiscard]] bool forwarded_redirect_mark_closing(forwarded_redirect_table& table,
                                                   std::uint8_t family,
                                                   std::uint16_t listener_port,
                                                   const std::uint8_t* client_address,
                                                   std::uint16_t client_port) noexcept;

// 回收过期表项；返回回收数量。只在建表失败 / 配置关闭等低频路径调用。
[[nodiscard]] std::uint32_t forwarded_redirect_sweep(forwarded_redirect_table& table) noexcept;

} // namespace winforward::wfp::probe
