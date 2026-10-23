#pragma once

// forwarded redirect 表：无 WDK 依赖、无加锁；调用方必须持有 g_forwarded_state.lock。
// 入口索引按流量方向 key；回程有精确（含 server）与候选（不含 server）两条索引。

#include <cstdint>
namespace winforward::wfp::probe
{

inline constexpr std::uint32_t forwarded_redirect_capacity = 4096;

inline constexpr std::uint32_t forwarded_redirect_bucket_count = 2048; // 必须是 2 的幂
inline constexpr std::uint32_t forwarded_redirect_bucket_mask = forwarded_redirect_bucket_count - 1;
// 超时（KeQueryInterruptTime 的 100ns 单位）。入口 SYN 建表后 30s 没等到非 SYN 数据就回收；
// 已建立连接 300s 无活动回收；FIN/RST 之后留 5s grace，让迟到的 ACK/FIN 还能命中。
// UDP 没有连接状态，首个 datagram 建表后 60s 无活动回收。
inline constexpr std::uint64_t forwarded_redirect_syn_timeout = 30ull * 10ull * 1000ull * 1000ull;
inline constexpr std::uint64_t forwarded_redirect_established_timeout = 300ull * 10ull * 1000ull * 1000ull;
inline constexpr std::uint64_t forwarded_redirect_closing_timeout = 5ull * 10ull * 1000ull * 1000ull;
inline constexpr std::uint64_t forwarded_redirect_udp_timeout = 60ull * 10ull * 1000ull * 1000ull;
// state == 0 表示空闲槽位；next_* / next_free 都存「下标 + 1」，0 表示链尾。
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
    std::uint8_t protocol; // 6 = TCP / 17 = UDP
    std::uint8_t client_mac[6];
    std::uint8_t local_mac[6];
    std::uint8_t client_address[16];
    std::uint8_t server_address[16];
    std::uint8_t listener_address[16]; // DNAT 目标 L，也是回程 key 的一部分
    std::uint16_t client_port;
    std::uint16_t server_port;
    std::uint16_t listener_port;
    std::uint16_t reserved0;
    std::uint32_t next_ingress;
    std::uint32_t next_return;
    std::uint32_t next_candidate;
    std::uint32_t next_free;
};

struct forwarded_redirect_table
{
    forwarded_redirect_entry* entries;
    std::uint32_t* ingress_buckets;
    std::uint32_t* return_buckets;
    std::uint32_t* candidate_buckets;
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
    std::uint8_t protocol;
    std::uint8_t client_mac[6];
    std::uint8_t local_mac[6];
    std::uint8_t client_address[16];
    std::uint8_t server_address[16];
    std::uint8_t listener_address[16];
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
    std::uint8_t protocol;
    std::uint8_t client_mac[6];
    std::uint8_t local_mac[6];
    std::uint8_t client_address[16];
    std::uint8_t server_address[16];
    std::uint8_t listener_address[16];
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
    ambiguous = 4, // 候选回程 key 有多条存活表项：调用方 fail-closed
};

[[nodiscard]] bool forwarded_redirect_init(forwarded_redirect_table& table) noexcept;
void forwarded_redirect_free(forwarded_redirect_table& table) noexcept;
void forwarded_redirect_clear(forwarded_redirect_table& table) noexcept;

// 入口 key 查找。expired 表示命中了一个已超时的表项，调用方按 missing 处理。
[[nodiscard]] forwarded_redirect_lookup forwarded_redirect_find_ingress(forwarded_redirect_table& table,
                                                                        std::uint32_t ingress_ifindex,
                                                                        std::uint8_t family,
                                                                        std::uint8_t protocol,
                                                                        const std::uint8_t* client_address,
                                                                        std::uint16_t client_port,
                                                                        const std::uint8_t* server_address,
                                                                        std::uint16_t server_port,
                                                                        forwarded_redirect_snapshot& snapshot) noexcept;

// 建表。候选回程 key 冲突时先移除旧表项（客户端端口复用）。容量满时先扫一遍过期表项；
// 仍然满则返回 false（调用方 block + capacity 计数）。
[[nodiscard]] bool forwarded_redirect_create(forwarded_redirect_table& table,
                                             const forwarded_redirect_request& create,
                                             forwarded_redirect_snapshot& snapshot) noexcept;

// 回程查找：候选索引必须唯一。listener_address 为 nullptr 表示任意 L（只用于 24 字节 v6
// 查询的线性扫描，不在帧热路径）。多条存活表项返回 ambiguous。
[[nodiscard]] forwarded_redirect_lookup forwarded_redirect_find_return(forwarded_redirect_table& table,
                                                                       std::uint8_t family,
                                                                       std::uint8_t protocol,
                                                                       const std::uint8_t* listener_address,
                                                                       std::uint16_t listener_port,
                                                                       const std::uint8_t* client_address,
                                                                       std::uint16_t client_port,
                                                                       forwarded_redirect_snapshot& snapshot) noexcept;

// 候选回程 key 是否已有存活表项（不刷新 last_activity）。多 L 选择用它挑空闲 L。
[[nodiscard]] bool forwarded_redirect_has_return(forwarded_redirect_table& table,
                                                 std::uint8_t family,
                                                 std::uint8_t protocol,
                                                 const std::uint8_t* listener_address,
                                                 std::uint16_t listener_port,
                                                 const std::uint8_t* client_address,
                                                 std::uint16_t client_port) noexcept;

// 标记 closing（FIN/RST），保留 grace 窗口；不立即释放槽位。
[[nodiscard]] bool forwarded_redirect_mark_closing(forwarded_redirect_table& table,
                                                   std::uint8_t family,
                                                   std::uint8_t protocol,
                                                   const std::uint8_t* listener_address,
                                                   std::uint16_t listener_port,
                                                   const std::uint8_t* client_address,
                                                   std::uint16_t client_port) noexcept;

// 回收过期表项；返回回收数量。只在建表失败 / 配置关闭等低频路径调用。
[[nodiscard]] std::uint32_t forwarded_redirect_sweep(forwarded_redirect_table& table) noexcept;

} // namespace winforward::wfp::probe
