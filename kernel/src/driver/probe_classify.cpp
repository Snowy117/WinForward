#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

constexpr std::uint32_t invalid_rule_index = 0xFFFFFFFFU;

static_assert(static_cast<std::uint8_t>(rule_action::permit) == static_cast<std::uint8_t>(probe_action::permit));
static_assert(static_cast<std::uint8_t>(rule_action::block) == static_cast<std::uint8_t>(probe_action::block));
static_assert(static_cast<std::uint8_t>(rule_action::redirect) == static_cast<std::uint8_t>(probe_action::redirect));
static_assert(static_cast<std::uint8_t>(address_family::ipv4) == rule_family_ipv4);
static_assert(static_cast<std::uint8_t>(address_family::ipv6) == rule_family_ipv6);

// UDP 计数点：只在协议为 UDP 时自增，让
// udp_classify_calls == udp_permitted + udp_blocked + udp_redirected 在单次测试的增量上成立。
// <mutation id="udp-permit-counter">
void count_udp_permit(probe_state& state, std::uint8_t protocol) noexcept
{
    if (protocol == IPPROTO_UDP)
    {
        probe_increment(state.stats.udp_permitted);
    }
}
// </mutation>

// <mutation id="udp-block-counter">
void count_udp_block(probe_state& state, std::uint8_t protocol) noexcept
{
    if (protocol == IPPROTO_UDP)
    {
        probe_increment(state.stats.udp_blocked);
    }
}
// </mutation>

// <mutation id="udp-redirect-counter">
void count_udp_redirect(probe_state& state, std::uint8_t protocol) noexcept
{
    if (protocol == IPPROTO_UDP)
    {
        probe_increment(state.stats.udp_redirected);
    }
}
// </mutation>

struct connection_facts
{
    SOCKADDR_STORAGE remote;
    SOCKADDR_STORAGE local;
    std::uint8_t remote_address[16];
    std::uint8_t local_address[16];
    std::uint32_t address_length;
    UINT16 remote_port;
    UINT16 local_port;
    std::uint8_t protocol;
    std::uint8_t family; // AF_INET / AF_INET6
    UINT32 process_id;
    UINT32 condition_flags;
    char process_path[max_process_path_length];
    std::uint32_t process_path_length;
};

[[nodiscard]] bool is_ipv6_layer(const FWPS_INCOMING_VALUES0* values) noexcept
{
    return values->layerId == FWPS_LAYER_ALE_CONNECT_REDIRECT_V6;
}

[[nodiscard]] bool is_redirect_layer(const FWPS_INCOMING_VALUES0* values) noexcept
{
    return values->layerId == FWPS_LAYER_ALE_CONNECT_REDIRECT_V4 ||
           values->layerId == FWPS_LAYER_ALE_CONNECT_REDIRECT_V6;
}

[[nodiscard]] std::uint8_t field_u8(const FWPS_INCOMING_VALUES0* values, UINT16 index) noexcept
{
    if (index >= values->valueCount)
    {
        return 0;
    }
    const FWP_VALUE0& value = values->incomingValue[index].value;
    return value.type == FWP_UINT8 ? value.uint8 : 0;
}

[[nodiscard]] std::uint32_t field_u32(const FWPS_INCOMING_VALUES0* values, UINT16 index) noexcept
{
    if (index >= values->valueCount)
    {
        return 0;
    }
    const FWP_VALUE0& value = values->incomingValue[index].value;
    return value.type == FWP_UINT32 ? value.uint32 : 0;
}

[[nodiscard]] UINT16 host_port(const SOCKADDR* address) noexcept
{
    if (address->sa_family == AF_INET6)
    {
        return RtlUshortByteSwap(reinterpret_cast<const SOCKADDR_IN6*>(address)->sin6_port);
    }
    return RtlUshortByteSwap(reinterpret_cast<const SOCKADDR_IN*>(address)->sin_port);
}

void copy_address(const SOCKADDR* address, std::uint8_t* target, std::uint32_t& length) noexcept
{
    if (address->sa_family == AF_INET6)
    {
        RtlCopyMemory(target, reinterpret_cast<const SOCKADDR_IN6*>(address)->sin6_addr.s6_addr, 16);
        length = 16;
        return;
    }
    RtlCopyMemory(target, &reinterpret_cast<const SOCKADDR_IN*>(address)->sin_addr, 4);
    length = 4;
}

[[nodiscard]] bool gather_facts(const FWPS_INCOMING_VALUES0* values,

                                const FWPS_INCOMING_METADATA_VALUES0* metadata,
                                void* layer_data,
                                connection_facts& facts) noexcept
{
    if (values == nullptr || layer_data == nullptr || !is_redirect_layer(values))
    {
        return false;
    }

    RtlZeroMemory(&facts, sizeof(facts));
    const bool ipv6 = is_ipv6_layer(values);
    const auto* request = static_cast<const FWPS_CONNECT_REQUEST0*>(layer_data);
    RtlCopyMemory(&facts.remote, &request->remoteAddressAndPort, sizeof(SOCKADDR_STORAGE));
    RtlCopyMemory(&facts.local, &request->localAddressAndPort, sizeof(SOCKADDR_STORAGE));

    const auto* remote = reinterpret_cast<const SOCKADDR*>(&facts.remote);
    const auto* local = reinterpret_cast<const SOCKADDR*>(&facts.local);
    if (ipv6)
    {
        if (remote->sa_family != AF_INET6 || local->sa_family != AF_INET6)
        {
            return false;
        }
    }
    else if (remote->sa_family != AF_INET || local->sa_family != AF_INET)
    {
        return false;
    }

    facts.family = static_cast<std::uint8_t>(remote->sa_family);
    facts.remote_port = host_port(remote);
    facts.local_port = host_port(local);
    copy_address(remote, facts.remote_address, facts.address_length);
    std::uint32_t local_length = 0;
    copy_address(local, facts.local_address, local_length);

    const UINT16 protocol_field =
        ipv6 ? FWPS_FIELD_ALE_CONNECT_REDIRECT_V6_IP_PROTOCOL : FWPS_FIELD_ALE_CONNECT_REDIRECT_V4_IP_PROTOCOL;
    const UINT16 flags_field =
        ipv6 ? FWPS_FIELD_ALE_CONNECT_REDIRECT_V6_FLAGS : FWPS_FIELD_ALE_CONNECT_REDIRECT_V4_FLAGS;
    const UINT16 app_id_field =
        ipv6 ? FWPS_FIELD_ALE_CONNECT_REDIRECT_V6_ALE_APP_ID : FWPS_FIELD_ALE_CONNECT_REDIRECT_V4_ALE_APP_ID;
    facts.protocol = field_u8(values, protocol_field);
    facts.condition_flags = field_u32(values, flags_field);

    if (metadata != nullptr && FWPS_IS_METADATA_FIELD_PRESENT(metadata, FWPS_METADATA_FIELD_PROCESS_ID))
    {
        facts.process_id = static_cast<UINT32>(metadata->processId);
    }
    if (metadata != nullptr && FWPS_IS_METADATA_FIELD_PRESENT(metadata, FWPS_METADATA_FIELD_PROCESS_PATH))
    {
        // 诊断计数：官方 metadata 表说 redirect 层没有 PROCESS_PATH，实测应为 0。
        probe_increment(probe_state_ref().stats.process_path_metadata_seen);
    }

    if (app_id_field < values->valueCount)
    {
        const FWP_VALUE0& value = values->incomingValue[app_id_field].value;
        if (value.type == FWP_BYTE_BLOB_TYPE && value.byteBlob != nullptr)
        {
            collect_process_path(
                value.byteBlob->data, value.byteBlob->size, facts.process_path, facts.process_path_length);
        }
    }
    return true;
}

[[nodiscard]] bool process_id_in_set(const probe_config& config, UINT32 process_id) noexcept
{
    if (process_id == 0)
    {
        return false;
    }
    for (std::uint32_t index = 0; index < config.self_process_count && index < max_self_process_ids; ++index)
    {
        if (config.self_process_ids[index] == process_id)
        {
            return true;
        }
    }
    return false;
}

void log_decision(const connection_facts& facts,
                  std::uint32_t rule_index,
                  probe_decision decision,
                  probe_action action,
                  std::uint16_t redirect_port,
                  redirect_branch branch) noexcept
{
    probe_event event{};
    event.timestamp_100ns = KeQueryInterruptTime();
    event.process_id = facts.process_id;
    RtlCopyMemory(event.remote_address, facts.remote_address, sizeof(event.remote_address));
    RtlCopyMemory(event.local_address, facts.local_address, sizeof(event.local_address));
    event.remote_port = facts.remote_port;
    event.local_port = facts.local_port;
    event.redirect_port = redirect_port;
    event.decision = static_cast<std::uint8_t>(decision);
    event.family = facts.family;
    event.action = static_cast<std::uint8_t>(action);
    event.branch = static_cast<std::uint8_t>(branch);
    event.protocol = facts.protocol;
    event.matched_rule = rule_index;
    probe_log_event(event);
}

void set_permit(FWPS_CLASSIFY_OUT0* classify_out) noexcept
{
    classify_out->actionType = FWP_ACTION_PERMIT;
    classify_out->rights &= ~FWPS_RIGHT_ACTION_WRITE;
}

// 匹配之后一次 classify 的输入与输出；rule_index 已确定为命中规则下标或 0xFFFFFFFF。
struct classify_call
{
    probe_state& state;
    const probe_config& config;
    const connection_facts& facts;
    std::uint32_t rule_index;
    const void* classify_context;
    const FWPS_FILTER1* filter;
    FWPS_CLASSIFY_OUT0* classify_out;
};

// redirect 层不执行拦截，改道失败的真实效果是直连；按 PERMIT 记账，并用 redirect_failures
// 与独立 decision 暴露这次失败，不让计数假装拦住了。
struct redirect_failure
{
    probe_decision decision;
    std::uint16_t port;
    redirect_branch branch;
    std::uint32_t status; // acquire / apply 的 NTSTATUS；0 表示还没走到改道
};

void record_redirect_failure(const classify_call& call, const redirect_failure& failure) noexcept
{
    probe_increment(call.state.stats.redirect_failures);
    probe_increment(call.state.stats.permitted);
    count_udp_permit(call.state, call.facts.protocol);
    if (failure.status != 0)
    {
        probe_increment(call.state.stats.apply_failures);
        call.state.stats.last_status = failure.status;
    }
    log_decision(call.facts, call.rule_index, failure.decision, probe_action::permit, failure.port, failure.branch);
    set_permit(call.classify_out);
}

void permit_classification(probe_state& state,
                           const connection_facts& facts,
                           std::uint32_t rule_index,
                           probe_decision decision,
                           FWPS_CLASSIFY_OUT0* classify_out) noexcept
{
    set_permit(classify_out);
    probe_increment(state.stats.permitted);
    count_udp_permit(state, facts.protocol);
    log_decision(facts, rule_index, decision, probe_action::permit, 0, redirect_branch::none);
}

// 依据 FwpsAcquireWritableLayerDataPointer0 的 Remarks：该函数会把 classifyOut->actionType 置为
// FWP_ACTION_BLOCK、清掉 FWPS_RIGHT_ACTION_WRITE，并且每次调用都必须配对一次
// FwpsApplyModifiedLayerData0。classify 是 terminating，redirect 层又不执行拦截，
// 所以终态只可能是 PERMIT。apply 之前失败时 context 仍归调用方。
[[nodiscard]] NTSTATUS
apply_redirect(const classify_call& call, const SOCKADDR_STORAGE& target, redirect_context* context) noexcept
{
    UINT64 classify_handle = 0;
    NTSTATUS status = FwpsAcquireClassifyHandle0(const_cast<void*>(call.classify_context), 0, &classify_handle);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    void* writable_data = nullptr;
    status = FwpsAcquireWritableLayerDataPointer0(
        classify_handle, call.filter->filterId, 0, &writable_data, call.classify_out);
    if (!NT_SUCCESS(status) || writable_data == nullptr)
    {
        FwpsReleaseClassifyHandle0(classify_handle);
        return NT_SUCCESS(status) ? STATUS_UNSUCCESSFUL : status;
    }

    auto* request = static_cast<FWPS_CONNECT_REQUEST0*>(writable_data);
    request->localRedirectTargetPID = call.config.listener_process_id;
    request->localRedirectHandle = call.state.redirect_handle;
    request->localRedirectContext = context;
    request->localRedirectContextSize = context == nullptr ? 0 : sizeof(redirect_context);
    RtlCopyMemory(&request->remoteAddressAndPort, &target, sizeof(target));

    set_permit(call.classify_out);
    FwpsApplyModifiedLayerData0(classify_handle, request, 0);
    FwpsReleaseClassifyHandle0(classify_handle);
    return STATUS_SUCCESS;
}

void redirect_classification(const classify_call& call) noexcept
{
    const probe_config& config = call.config;
    if (config.listener_port == 0 || call.state.redirect_handle == nullptr)
    {
        const redirect_failure failure{ probe_decision::redirect_failed, 0, redirect_branch::none, 0 };
        record_redirect_failure(call, failure);
        return;
    }

    const auto* remote = reinterpret_cast<const SOCKADDR*>(&call.facts.remote);
    const auto* local = reinterpret_cast<const SOCKADDR*>(&call.facts.local);
    SOCKADDR_STORAGE target{};
    redirect_branch branch = redirect_branch::none;
    probe_build_redirect_target(remote, local, config.listener_port, target, branch);

    // 拿不到 context 就带不回原始目的地；此时还没 acquire，直接按 fail-open 放行。
    auto* context =
        static_cast<redirect_context*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(redirect_context), pool_tag));
    if (context == nullptr)
    {
        probe_increment(call.state.stats.context_alloc_failures);
        const redirect_failure failure{ probe_decision::context_alloc_failed, config.listener_port, branch, 0 };
        record_redirect_failure(call, failure);
        return;
    }
    probe_increment(call.state.stats.contexts_allocated);

    original_destination destination{};
    destination.family = call.facts.family == AF_INET6 ? address_family::ipv6 : address_family::ipv4;
    destination.protocol = call.facts.protocol == IPPROTO_UDP ? transport_protocol::udp : transport_protocol::tcp;
    destination.port = call.facts.remote_port;
    std::uint32_t address_length = 0;
    copy_address(remote, destination.address, address_length);
    *context = make_redirect_context(destination, call.facts.process_id, call.rule_index);

    const NTSTATUS status = apply_redirect(call, target, context);
    if (!NT_SUCCESS(status))
    {
        // apply 之前失败：所有权没转给 WFP，context 由驱动释放。
        ExFreePoolWithTag(context, pool_tag);
        const redirect_failure failure{
            probe_decision::redirect_failed, 0, branch, static_cast<std::uint32_t>(status)
        };
        record_redirect_failure(call, failure);
        return;
    }

    probe_increment(call.state.stats.redirected);
    count_udp_redirect(call.state, call.facts.protocol);
    log_decision(
        call.facts, call.rule_index, probe_decision::redirected, probe_action::redirect, config.listener_port, branch);
}

// block 命中后把连接改道到 block_sink_port：redirect 层不执行拦截，拒绝由 AUTH_CONNECT 上
// 的固定 filter（协议 + 远端端口 == sink + IS_CONNECTION_REDIRECTED）完成。
void sink_classification(const classify_call& call, probe_decision decision) noexcept
{
    const auto sink_port = static_cast<std::uint16_t>(call.config.block_sink_port);
    if (sink_port == 0 || call.state.redirect_handle == nullptr)
    {
        const redirect_failure failure{ probe_decision::block_enforcement_failed, 0, redirect_branch::none, 0 };
        record_redirect_failure(call, failure);
        return;
    }

    const auto* remote = reinterpret_cast<const SOCKADDR*>(&call.facts.remote);
    const auto* local = reinterpret_cast<const SOCKADDR*>(&call.facts.local);
    SOCKADDR_STORAGE target{};
    redirect_branch branch = redirect_branch::none;
    probe_build_redirect_target(remote, local, sink_port, target, branch);

    const NTSTATUS status = apply_redirect(call, target, nullptr);
    if (!NT_SUCCESS(status))
    {
        const redirect_failure failure{
            probe_decision::block_enforcement_failed, sink_port, branch, static_cast<std::uint32_t>(status)
        };
        record_redirect_failure(call, failure);
        return;
    }

    probe_increment(call.state.stats.blocked);
    probe_increment(call.state.stats.deferred_blocks);
    count_udp_block(call.state, call.facts.protocol);
    log_decision(call.facts, call.rule_index, decision, probe_action::block, sink_port, branch);
}

// 规则表的引用守卫：classify 的每个返回路径都要归还引用，交给析构而不是手写 release。
class rules_guard
{
  public:
    explicit rules_guard(const rule_table* table) noexcept
        : table_(table)
    {
    }

    ~rules_guard()
    {
        probe_release_rules(table_);
    }

    rules_guard(const rules_guard&) = delete;
    rules_guard& operator=(const rules_guard&) = delete;

  private:
    const rule_table* table_;
};

} // namespace

void NTAPI probe_classify_fn(const FWPS_INCOMING_VALUES0* in_fixed_values,
                             const FWPS_INCOMING_METADATA_VALUES0* in_meta_values,
                             void* layer_data,
                             const void* classify_context,
                             const FWPS_FILTER1* filter,
                             UINT64 flow_context,
                             FWPS_CLASSIFY_OUT0* classify_out) noexcept
{
    UNREFERENCED_PARAMETER(flow_context);

    probe_state& state = probe_state_ref();
    probe_increment(state.stats.classify_calls);

    if (classify_out == nullptr || (classify_out->rights & FWPS_RIGHT_ACTION_WRITE) == 0)
    {
        probe_increment(state.stats.permitted_no_write_right);
        return;
    }

    connection_facts facts{};
    if (!gather_facts(in_fixed_values, in_meta_values, layer_data, facts))
    {
        probe_increment(state.stats.permitted_unsupported);
        set_permit(classify_out);
        return;
    }

    // <mutation id="udp-classify-counter">
    if (facts.protocol == IPPROTO_UDP)
    {
        probe_increment(state.stats.udp_classify_calls);
    }
    // </mutation>

    probe_config config{};
    const rule_table* rules = nullptr;
    probe_take_snapshot(config, rules);
    const rules_guard guard(rules);

    if (config.enabled == 0)
    {
        probe_increment(state.stats.permitted_disabled);
        count_udp_permit(state, facts.protocol);
        set_permit(classify_out);
        return;
    }
    if (rules == nullptr)
    {
        probe_increment(state.stats.permitted_no_rules);
        count_udp_permit(state, facts.protocol);
        set_permit(classify_out);
        return;
    }
    // <mutation id="classify-udp-branch">
    const bool protocol_is_tcp_or_udp = facts.protocol == IPPROTO_TCP || facts.protocol == IPPROTO_UDP;
    // </mutation>
    if (!protocol_is_tcp_or_udp)
    {
        // 既非 TCP 也非 UDP：classify 不接管，保持直连语义放行。
        probe_increment(state.stats.permitted_non_tcp);
        set_permit(classify_out);
        return;
    }

    // 闸 1：目标端口是 listener 或 block sink 端口。监听端接到入站连接后转发时若再指向自己，
    // 会形成回环；sink 端口上的连接已经过改道，不能再进入 classify。
    // <mutation id="gate-listener-port">
    const bool listener_port_hit = (config.listener_port != 0 && facts.remote_port == config.listener_port) ||
                                   (config.block_sink_port != 0 && facts.remote_port == config.block_sink_port);
    // </mutation>
    if (listener_port_hit)
    {
        probe_increment(state.stats.permitted_listener_port);
        permit_classification(state, facts, invalid_rule_index, probe_decision::permitted_listener_port, classify_out);
        return;
    }

    // 别的 callout 已经把这条连接改写过（previousVersion 里带 localRedirectHandle）时直接放行。
    // 只读 layerData，不需要 writable 副本，所以放在 acquire 之前，也就没有配对 apply 的问题。
    const auto* request = static_cast<const FWPS_CONNECT_REQUEST0*>(layer_data);
    if (request->previousVersion != nullptr && request->previousVersion->localRedirectHandle != nullptr)
    {
        probe_increment(state.stats.permitted_already_redirected);
        permit_classification(
            state, facts, invalid_rule_index, probe_decision::permitted_already_redirected, classify_out);
        return;
    }

    // 闸 2：FwpsQueryConnectionRedirectState0 —— 文档要求的防无限重定向检查。
    // 代理的出站 socket 带上原始连接的 redirect records 之后，这里会返回 BY_SELF。
    FWPS_CONNECTION_REDIRECT_STATE redirect_state = FWPS_CONNECTION_NOT_REDIRECTED;
    if (FWPS_IS_METADATA_FIELD_PRESENT(in_meta_values, FWPS_METADATA_FIELD_REDIRECT_RECORD_HANDLE) &&
        state.redirect_handle != nullptr)
    {
        redirect_state =
            FwpsQueryConnectionRedirectState0(in_meta_values->redirectRecords, state.redirect_handle, nullptr);
    }
    // <mutation id="gate-redirect-state">
    const bool redirect_state_hit = redirect_state == FWPS_CONNECTION_REDIRECTED_BY_SELF ||
                                    redirect_state == FWPS_CONNECTION_PREVIOUSLY_REDIRECTED_BY_SELF;
    // </mutation>
    if (redirect_state_hit)
    {
        probe_increment(state.stats.permitted_redirect_state);
        permit_classification(state, facts, invalid_rule_index, probe_decision::permitted_redirect_state, classify_out);
        return;
    }

    // 闸 3：条件标志。FWP_CONDITION_FLAG_IS_PROXY_CONNECTION 是 ALE_CONNECT_REDIRECT 层上
    // 真正可能置位的那个（带 previous redirect records 的代理连接）；IS_CONNECTION_REDIRECTED
    // 按文档只适用于 ALE_AUTH_CONNECT，这里一并检查作为纵深防御。
    // <mutation id="gate-condition-flags">
    const bool condition_flags_hit = (facts.condition_flags & (FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED |
                                                               FWP_CONDITION_FLAG_IS_PROXY_CONNECTION)) != 0;
    // </mutation>
    if (condition_flags_hit)
    {
        probe_increment(state.stats.permitted_condition_flags);
        permit_classification(
            state, facts, invalid_rule_index, probe_decision::permitted_condition_flags, classify_out);
        return;
    }

    // 闸 4：self-PID 集合。代理自己的出站连接（新 socket，没有 redirect records）靠这一道。
    // <mutation id="gate-self-process">
    const bool self_process_hit = process_id_in_set(config, facts.process_id);
    // </mutation>
    if (self_process_hit)
    {
        probe_increment(state.stats.permitted_self_process);
        permit_classification(state, facts, invalid_rule_index, probe_decision::permitted_self_process, classify_out);
        return;
    }

    connection_view connection{};
    connection.process_path = facts.process_path_length == 0 ? nullptr : facts.process_path;
    connection.process_path_length = facts.process_path_length;
    RtlCopyMemory(connection.remote_address, facts.remote_address, sizeof(connection.remote_address));
    connection.remote_port = facts.remote_port;
    connection.protocol = facts.protocol;
    connection.family = facts.family == AF_INET6 ? address_family::ipv6 : address_family::ipv4;

    const match_result result = match(rules->view, connection);
    const std::uint32_t rule_index = result.matched ? result.rule_index : invalid_rule_index;
    const classify_call call{ state, config, facts, rule_index, classify_context, filter, classify_out };
    // <mutation id="classify-redirect-branch">
    const bool redirect_wanted = result.action == rule_action::redirect;
    // </mutation>
    // <mutation id="classify-block-branch">
    const bool block_wanted = result.action == rule_action::block;
    // </mutation>

    if (redirect_wanted)
    {
        probe_increment(result.matched ? state.stats.matched_redirect : state.stats.fallback_redirect);
        redirect_classification(call);
        return;
    }
    if (block_wanted)
    {
        // block 的执行在 ALE_AUTH_CONNECT 的固定 sink filter；redirect 层只负责把连接改道到
        // block_sink_port，blocked / deferred_blocks 只在改道成功时增长。
        probe_increment(result.matched ? state.stats.matched_block : state.stats.fallback_block);
        sink_classification(call, result.matched ? probe_decision::blocked_rule : probe_decision::blocked_fallback);
        return;
    }

    // <mutation id="permit-counter">
    probe_increment(result.matched ? state.stats.matched_permit : state.stats.fallback_permit);
    // </mutation>
    // <mutation id="permit-branch">
    permit_classification(state,
                          facts,
                          rule_index,
                          result.matched ? probe_decision::permitted_rule : probe_decision::permitted_fallback,
                          classify_out);
    // </mutation>
}

NTSTATUS NTAPI probe_notify_fn(FWPS_CALLOUT_NOTIFY_TYPE notify_type,
                               const GUID* filter_key,
                               FWPS_FILTER1* filter) noexcept
{
    UNREFERENCED_PARAMETER(notify_type);
    UNREFERENCED_PARAMETER(filter_key);
    UNREFERENCED_PARAMETER(filter);
    return STATUS_SUCCESS;
}

} // namespace winforward::wfp::probe
