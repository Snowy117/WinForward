#include "probe_driver.h"
#include "probe_forwarded.h"
#include "probe_forwarded_state.h"

namespace winforward::wfp::probe
{
namespace
{

bool listener_address_valid(const std::uint8_t* address, std::uint8_t family) noexcept
{
    const std::uint32_t address_size = family == 4 ? 4U : 16U;
    bool present = false;
    for (std::uint32_t index = 0; index < 16; ++index)
    {
        if (index < address_size)
        {
            present = present || address[index] != 0;
        }
        else if (address[index] != 0)
        {
            return false;
        }
    }
    return present;
}

bool same_runtime_mapping(const forwarded_runtime_config& left, const forwarded_runtime_config& right) noexcept
{
    if (left.enabled != right.enabled || left.ingress_count != right.ingress_count)
    {
        return false;
    }

    for (std::uint32_t index = 0; index < left.ingress_count; ++index)
    {
        const forwarded_runtime_endpoint& first = left.ingresses[index];
        const forwarded_runtime_endpoint& second = right.ingresses[index];
        if (first.ifindex != second.ifindex || first.listener_port != second.listener_port ||
            first.family != second.family || first.reserved0 != second.reserved0 ||
            first.reserved1 != second.reserved1 || !RtlEqualMemory(first.listener_address, second.listener_address, 16))
        {
            return false;
        }
    }
    return true;
}

bool endpoint_duplicate(const forwarded_runtime_config& config,
                        const forwarded_runtime_endpoint& endpoint,
                        std::uint32_t up_to) noexcept
{
    for (std::uint32_t index = 0; index < up_to; ++index)
    {
        const forwarded_runtime_endpoint& other = config.ingresses[index];
        if (other.ifindex == endpoint.ifindex && other.listener_port == endpoint.listener_port &&
            other.family == endpoint.family && RtlEqualMemory(other.listener_address, endpoint.listener_address, 16))
        {
            return true;
        }
    }
    return false;
}

// 配置变化或关闭时清空 redirect 表：表项里的 (L, listener_port) 必须与当前映射一致，旧表项
// 跨配置保留会让回程用已经不再监听的端点去 SNAT。只换 blob 不走这里（热替换保留旧表项）。
void commit_runtime_config(forwarded_state& state, const forwarded_runtime_config& candidate) noexcept
{
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    const bool changed = !same_runtime_mapping(state.config, candidate);
    state.config = candidate;
    if (!candidate.enabled || changed)
    {
        forwarded_redirect_clear(state.redirects);
    }
    state.stats.last_status = static_cast<std::uint32_t>(STATUS_SUCCESS);
    forwarded_unlock(raised, old_irql);
}

} // namespace

NTSTATUS probe_forwarded_apply_config(const void* buffer, ULONG input_length) noexcept
{
    if (buffer == nullptr || input_length < sizeof(probe_forwarded_config))
    {
        return STATUS_INVALID_PARAMETER;
    }

    probe_forwarded_config legacy{};
    RtlCopyMemory(&legacy, buffer, sizeof(legacy));
    if (legacy.reserved0 != 0 || legacy.reserved1 != 0)
    {
        return STATUS_INVALID_PARAMETER;
    }
    if (legacy.family != 0 && legacy.family != 4 && legacy.family != 6)
    {
        return STATUS_INVALID_PARAMETER;
    }

    forwarded_runtime_config candidate{};
    candidate.enabled = legacy.enabled != 0;
    const bool has_listener = legacy.listener_port != 0 && (legacy.family == 4 || legacy.family == 6) &&
                              listener_address_valid(legacy.listener_address, legacy.family);
    if (candidate.enabled && !has_listener)
    {
        return STATUS_INVALID_PARAMETER;
    }
    if (has_listener)
    {
        forwarded_runtime_endpoint& endpoint = candidate.ingresses[0];
        endpoint.ifindex = forwarded_ingress_any_ifindex;
        endpoint.listener_port = legacy.listener_port;
        endpoint.family = legacy.family;
        RtlCopyMemory(endpoint.listener_address, legacy.listener_address, 16);
        candidate.ingress_count = 1;
    }

    commit_runtime_config(g_forwarded_state, candidate);
    return STATUS_SUCCESS;
}

NTSTATUS probe_forwarded_apply_ingress_config(const void* buffer, ULONG input_length) noexcept
{
    if (buffer == nullptr || input_length < sizeof(probe_forwarded_ingress_config))
    {
        return STATUS_INVALID_PARAMETER;
    }

    probe_forwarded_ingress_config header{};
    RtlCopyMemory(&header, buffer, sizeof(header));
    if (header.abi_version != probe_abi_version)
    {
        return STATUS_REVISION_MISMATCH;
    }
    if (header.reserved0 != 0 || header.ingress_count > forwarded_ingress_capacity)
    {
        return STATUS_INVALID_PARAMETER;
    }

    const ULONG required = sizeof(header) + header.ingress_count * sizeof(probe_forwarded_ingress);
    if (input_length != required)
    {
        return STATUS_INVALID_BUFFER_SIZE;
    }

    forwarded_runtime_config candidate{};
    candidate.enabled = header.enabled != 0;
    candidate.ingress_count = header.ingress_count;
    const auto* rows = static_cast<const std::uint8_t*>(buffer) + sizeof(header);
    for (std::uint32_t index = 0; index < header.ingress_count; ++index)
    {
        probe_forwarded_ingress row{};
        RtlCopyMemory(&row, rows + (index * sizeof(row)), sizeof(row));
        if (row.reserved0 != 0 || row.reserved1 != 0 || (row.family != 4 && row.family != 6) ||
            row.listener_port == 0 || !listener_address_valid(row.listener_address, row.family))
        {
            return STATUS_INVALID_PARAMETER;
        }

        forwarded_runtime_endpoint& endpoint = candidate.ingresses[index];
        endpoint.ifindex = row.ifindex;
        endpoint.listener_port = row.listener_port;
        endpoint.family = row.family;
        RtlCopyMemory(endpoint.listener_address, row.listener_address, 16);
        if (endpoint_duplicate(candidate, endpoint, index))
        {
            return STATUS_INVALID_PARAMETER;
        }
    }

    commit_runtime_config(g_forwarded_state, candidate);
    return STATUS_SUCCESS;
}

NTSTATUS probe_forwarded_get_config(void* buffer, ULONG output_length) noexcept
{
    if (buffer == nullptr || output_length < sizeof(probe_forwarded_config))
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    forwarded_state& state = g_forwarded_state;
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    const forwarded_runtime_config snapshot = state.config;
    forwarded_unlock(raised, old_irql);

    probe_forwarded_config legacy{};
    legacy.enabled = snapshot.enabled;
    if (snapshot.ingress_count > 0)
    {
        const forwarded_runtime_endpoint& endpoint = snapshot.ingresses[0];
        legacy.listener_port = endpoint.listener_port;
        legacy.family = endpoint.family;
        RtlCopyMemory(legacy.listener_address, endpoint.listener_address, 16);
    }
    RtlCopyMemory(buffer, &legacy, sizeof(legacy));
    return STATUS_SUCCESS;
}

NTSTATUS probe_forwarded_get_ingress_config(void* buffer, ULONG output_length, ULONG& written) noexcept
{
    written = 0;
    if (buffer == nullptr || output_length < sizeof(probe_forwarded_ingress_config))
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    forwarded_state& state = g_forwarded_state;
    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    const forwarded_runtime_config snapshot = state.config;
    forwarded_unlock(raised, old_irql);

    written = sizeof(probe_forwarded_ingress_config) +
              (snapshot.ingress_count * static_cast<ULONG>(sizeof(probe_forwarded_ingress)));
    if (output_length < written)
    {
        written = 0;
        return STATUS_BUFFER_TOO_SMALL;
    }

    probe_forwarded_ingress_config header{};
    header.abi_version = probe_abi_version;
    header.enabled = snapshot.enabled;
    header.ingress_count = snapshot.ingress_count;
    auto* bytes = static_cast<std::uint8_t*>(buffer);
    RtlCopyMemory(bytes, &header, sizeof(header));
    for (std::uint32_t index = 0; index < snapshot.ingress_count; ++index)
    {
        const forwarded_runtime_endpoint& endpoint = snapshot.ingresses[index];
        probe_forwarded_ingress row{};
        row.ifindex = endpoint.ifindex;
        row.listener_port = endpoint.listener_port;
        row.family = endpoint.family;
        RtlCopyMemory(row.listener_address, endpoint.listener_address, 16);
        RtlCopyMemory(bytes + sizeof(header) + (index * sizeof(row)), &row, sizeof(row));
    }
    return STATUS_SUCCESS;
}

NTSTATUS
probe_forwarded_query_redirect(const void* buffer, ULONG input_length, void* output, ULONG output_length) noexcept
{
    if (buffer == nullptr || output == nullptr || input_length < probe_forwarded_redirect_query_v1_size ||
        output_length < probe_forwarded_redirect_v1_size)
    {
        return STATUS_INVALID_PARAMETER;
    }

    probe_forwarded_redirect_query query{};
    const ULONG input_copy = input_length < sizeof(query) ? input_length : static_cast<ULONG>(sizeof(query));
    RtlCopyMemory(&query, buffer, input_copy);
    const std::uint8_t protocol = query.protocol == 0 ? 6 : query.protocol;
    if (query.reserved0 != 0 || query.reserved1 != 0 || query.listener_port == 0 || query.client_port == 0 ||
        (query.family != 4 && query.family != 6) || (protocol != 6 && protocol != 17))
    {
        return STATUS_INVALID_PARAMETER;
    }

    const std::uint8_t* listener_address = nullptr;
    if (input_copy >= sizeof(query) && listener_address_valid(query.listener_address, query.family))
    {
        listener_address = query.listener_address;
    }

    forwarded_state& state = g_forwarded_state;
    forwarded_redirect_snapshot snapshot{};
    forwarded_redirect_lookup lookup = forwarded_redirect_lookup::missing;

    KIRQL old_irql{};
    const bool raised = forwarded_lock(old_irql);
    // 控制面查询不增加数据面回程计数。legacy 24B 查询会做 wildcard 线性扫描，可能同时看到
    // 多个 L 的表项；这是“查询不唯一”，不是帧路径歧义。ambiguous 只由 TCP/UDP 回程路径计数。
    lookup = forwarded_redirect_find_return(state.redirects,

                                            query.family,
                                            protocol,
                                            listener_address,
                                            query.listener_port,
                                            query.client_address,
                                            query.client_port,
                                            snapshot);
    if (lookup == forwarded_redirect_lookup::expired)
    {
        if (protocol == 17)
        {
            ++state.stats.udp_expired;
        }
        else
        {
            ++state.stats.redirect_expired;
        }
    }
    forwarded_unlock(raised, old_irql);

    if (lookup != forwarded_redirect_lookup::found)
    {
        return STATUS_NOT_FOUND;
    }

    probe_forwarded_redirect result{};
    result.magic = forwarded_redirect_magic;
    result.version =
        output_length >= sizeof(result) ? forwarded_redirect_version_with_listener : forwarded_redirect_version;
    result.family = snapshot.family;
    result.protocol = snapshot.protocol;
    result.client_port = snapshot.client_port;
    result.server_port = snapshot.server_port;
    result.listener_port = snapshot.listener_port;
    result.ingress_ifindex = snapshot.ingress_ifindex;
    result.upstream_id = snapshot.upstream_id;
    result.rule_index = snapshot.rule_index;
    result.state = snapshot.state;
    result.generation = snapshot.generation;
    RtlCopyMemory(result.client_address, snapshot.client_address, 16);
    RtlCopyMemory(result.server_address, snapshot.server_address, 16);
    RtlCopyMemory(result.listener_address, snapshot.listener_address, 16);

    const ULONG written = output_length >= sizeof(result) ? sizeof(result) : probe_forwarded_redirect_v1_size;
    RtlCopyMemory(output, &result, written);
    return STATUS_SUCCESS;
}

} // namespace winforward::wfp::probe
