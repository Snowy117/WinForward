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

void set_block(FWPS_CLASSIFY_OUT0* classify_out) noexcept
{
    classify_out->actionType = FWP_ACTION_BLOCK;
    classify_out->rights &= ~FWPS_RIGHT_ACTION_WRITE;
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
// FwpsApplyModifiedLayerData0。我们的 filter 是 FWP_ACTION_CALLOUT_TERMINATING，不存在
// 「留给低权重 callout 再改」的需求，所以放行和拒绝都直接写终态。
void finish_classify(UINT64 classify_handle, void* writable_data, FWPS_CLASSIFY_OUT0* classify_out, bool block) noexcept
{
    if (block)
    {
        set_block(classify_out);
    }
    else
    {
        set_permit(classify_out);
    }
    FwpsApplyModifiedLayerData0(classify_handle, writable_data, 0);
    FwpsReleaseClassifyHandle0(classify_handle);
}

void redirect_classification(probe_state& state,
                             const probe_config& config,
                             const connection_facts& facts,
                             std::uint32_t rule_index,
                             const void* classify_context,
                             const FWPS_FILTER1* filter,
                             FWPS_CLASSIFY_OUT0* classify_out) noexcept
{
    if (config.listener_port == 0 || state.redirect_handle == nullptr)
    {
        // 重定向失败 = 直连（fail-open）：ALE_CONNECT_REDIRECT 不执行拦截，返回 BLOCK 也不会
        // 真的阻断，只会让计数和事实不符。这里记 permit + redirect_failures。
        probe_increment(state.stats.redirect_failures);
        probe_increment(state.stats.permitted);
        count_udp_permit(state, facts.protocol);
        log_decision(
            facts, rule_index, probe_decision::redirect_failed, probe_action::permit, 0, redirect_branch::none);
        set_permit(classify_out);
        return;
    }

    const auto* remote = reinterpret_cast<const SOCKADDR*>(&facts.remote);
    const auto* local = reinterpret_cast<const SOCKADDR*>(&facts.local);
    SOCKADDR_STORAGE target{};
    redirect_branch branch = redirect_branch::none;
    probe_build_redirect_target(remote, local, config.listener_port, target, branch);

    UINT64 classify_handle = 0;
    NTSTATUS status = FwpsAcquireClassifyHandle0(const_cast<void*>(classify_context), 0, &classify_handle);
    if (!NT_SUCCESS(status))
    {
        probe_increment(state.stats.apply_failures);
        probe_increment(state.stats.redirect_failures);
        probe_increment(state.stats.permitted);
        count_udp_permit(state, facts.protocol);
        state.stats.last_status = static_cast<std::uint32_t>(status);
        log_decision(facts, rule_index, probe_decision::redirect_failed, probe_action::permit, 0, branch);
        set_permit(classify_out);
        return;
    }

    void* writable_data = nullptr;
    status = FwpsAcquireWritableLayerDataPointer0(classify_handle, filter->filterId, 0, &writable_data, classify_out);
    if (!NT_SUCCESS(status) || writable_data == nullptr)
    {
        // 拿不到写副本时 classifyOut 可能已经被引擎改成 BLOCK；但这个层不执行拦截，实际效果是
        // 直连。显式恢复成 PERMIT，让 classifyOut、事件与真实行为一致（fail-open）。
        FwpsReleaseClassifyHandle0(classify_handle);
        probe_increment(state.stats.apply_failures);
        probe_increment(state.stats.redirect_failures);
        probe_increment(state.stats.permitted);
        count_udp_permit(state, facts.protocol);
        state.stats.last_status = static_cast<std::uint32_t>(status);
        set_permit(classify_out);
        log_decision(facts, rule_index, probe_decision::redirect_failed, probe_action::permit, 0, branch);
        return;
    }

    auto* request = static_cast<FWPS_CONNECT_REQUEST0*>(writable_data);

    auto* context =
        static_cast<redirect_context*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(redirect_context), pool_tag));
    if (context == nullptr)
    {
        // 分配不到 context 就带不回原始目的地；仍然配对 apply（此时数据还没改），然后放行——
        // 这个层不执行拦截，BLOCK 只会让计数与“连接实际直连”不符（fail-open）。
        finish_classify(classify_handle, writable_data, classify_out, false);
        probe_increment(state.stats.context_alloc_failures);
        probe_increment(state.stats.redirect_failures);
        probe_increment(state.stats.permitted);
        count_udp_permit(state, facts.protocol);
        log_decision(facts,
                     rule_index,
                     probe_decision::context_alloc_failed,
                     probe_action::permit,
                     config.listener_port,
                     branch);
        return;
    }
    probe_increment(state.stats.contexts_allocated);

    original_destination destination{};
    destination.family = facts.family == AF_INET6 ? address_family::ipv6 : address_family::ipv4;
    destination.protocol = facts.protocol == IPPROTO_UDP ? transport_protocol::udp : transport_protocol::tcp;
    destination.port = facts.remote_port;
    std::uint32_t address_length = 0;
    copy_address(remote, destination.address, address_length);
    *context = make_redirect_context(destination, facts.process_id, rule_index);

    // 从 Windows 8 起，这三个字段是本地重定向的全部硬要求：目标的 PID、redirect handle，
    // 以及一块由 ExAllocatePoolWithTag 分配、所有权随后交给 WFP 的 context。
    request->localRedirectTargetPID = config.listener_process_id;
    request->localRedirectHandle = state.redirect_handle;
    request->localRedirectContext = context;
    request->localRedirectContextSize = sizeof(redirect_context);
    RtlCopyMemory(&request->remoteAddressAndPort, &target, sizeof(target));

    finish_classify(classify_handle, writable_data, classify_out, false);
    probe_increment(state.stats.redirected);
    count_udp_redirect(state, facts.protocol);
    log_decision(facts, rule_index, probe_decision::redirected, probe_action::redirect, config.listener_port, branch);
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

    // 闸 1：目标端口就是监听端口。监听端接到入站连接后转发时若再指向自己，会形成回环。
    // <mutation id="gate-listener-port">
    const bool listener_port_hit = config.listener_port != 0 && facts.remote_port == config.listener_port;
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
    // <mutation id="classify-redirect-branch">
    const bool redirect_wanted = result.action == rule_action::redirect;
    // </mutation>
    // <mutation id="classify-block-branch">
    const bool block_wanted = result.action == rule_action::block;
    // </mutation>

    if (redirect_wanted)
    {
        probe_increment(result.matched ? state.stats.matched_redirect : state.stats.fallback_redirect);
        redirect_classification(state, config, facts, rule_index, classify_context, filter, classify_out);
        return;
    }
    if (block_wanted)
    {
        // FWP_ACTION_BLOCK 在 ALE_CONNECT_REDIRECT 不生效（实测见 research）。决策在这里下、
        // 执行由 ALE_AUTH_CONNECT 的 block filter 完成，所以计数与事件留在这一层。
        probe_increment(result.matched ? state.stats.matched_block : state.stats.fallback_block);
        probe_increment(state.stats.blocked);
        probe_increment(state.stats.deferred_blocks);
        count_udp_block(state, facts.protocol);
        log_decision(facts,
                     rule_index,
                     result.matched ? probe_decision::blocked_rule : probe_decision::blocked_fallback,
                     probe_action::block,
                     0,
                     redirect_branch::none);
        set_permit(classify_out);
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
