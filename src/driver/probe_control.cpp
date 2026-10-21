#include "probe_driver.h"

namespace winforward::wfp::probe
{
namespace
{

NTSTATUS apply_config(const void* buffer, ULONG input_length) noexcept
{
    if (buffer == nullptr || input_length < sizeof(probe_config))
    {
        return STATUS_INVALID_PARAMETER;
    }

    probe_config candidate{};
    RtlCopyMemory(&candidate, buffer, sizeof(candidate));

    if (candidate.abi_version != probe_abi_version)
    {
        return STATUS_REVISION_MISMATCH;
    }
    if (candidate.reserved0 != 0 || candidate.self_process_count > max_self_process_ids)
    {
        return STATUS_INVALID_PARAMETER;
    }
    for (std::uint32_t index = 0; index < max_self_process_ids; ++index)
    {
        if (index < candidate.self_process_count && candidate.self_process_ids[index] == 0)
        {
            return STATUS_INVALID_PARAMETER;
        }
        if (index >= candidate.self_process_count && candidate.self_process_ids[index] != 0)
        {
            return STATUS_INVALID_PARAMETER;
        }
    }
    if (candidate.reserved[0] != 0 || candidate.reserved[1] != 0 || candidate.reserved[2] != 0)
    {
        return STATUS_INVALID_PARAMETER;
    }
    if (candidate.enabled != 0 && (candidate.listener_port == 0 || candidate.listener_process_id == 0))
    {
        return STATUS_INVALID_PARAMETER;
    }

    probe_state& state = probe_state_ref();
    const bool was_enabled = state.config.enabled != 0;
    const bool next_enabled = candidate.enabled != 0;
    if (was_enabled != next_enabled)
    {
        // 0↔1 切换要和换表共用一把锁：禁用必须真的撤掉 block filter，启用则按当前表重建。
        KeWaitForSingleObject(&state.rule_mutex, Executive, KernelMode, FALSE, nullptr);
        NTSTATUS status = STATUS_SUCCESS;
        if (!next_enabled)
        {
            probe_delete_block_filters(state.block_filter_ids, state.block_filter_count);
        }
        else
        {
            UINT64 ids[max_block_filters]{};
            std::uint32_t count = 0;
            if (state.rules != nullptr)
            {
                status = probe_install_block_filters(state.rules->view, ids, count);
            }
            if (NT_SUCCESS(status))
            {
                probe_delete_block_filters(state.block_filter_ids, state.block_filter_count);
                for (std::uint32_t index = 0; index < count; ++index)
                {
                    state.block_filter_ids[index] = ids[index];
                }
                state.block_filter_count = count;
            }
        }
        if (!NT_SUCCESS(status))
        {
            KeReleaseMutex(&state.rule_mutex, FALSE);
            return status;
        }
        KIRQL old_irql{};
        const bool raised = probe_lock(old_irql);
        state.config = candidate;
        probe_unlock(raised, old_irql);
        KeReleaseMutex(&state.rule_mutex, FALSE);
        return STATUS_SUCCESS;
    }

    KIRQL old_irql{};
    const bool raised = probe_lock(old_irql);
    state.config = candidate;
    probe_unlock(raised, old_irql);
    return STATUS_SUCCESS;
}

NTSTATUS get_config(void* buffer, ULONG output_length) noexcept
{
    if (buffer == nullptr || output_length < sizeof(probe_config))
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    KIRQL old_irql{};
    const bool raised = probe_lock(old_irql);
    const probe_config snapshot = probe_state_ref().config;
    probe_unlock(raised, old_irql);

    RtlCopyMemory(buffer, &snapshot, sizeof(snapshot));
    return STATUS_SUCCESS;
}

NTSTATUS get_stats(void* buffer, ULONG output_length) noexcept
{
    if (buffer == nullptr || output_length < sizeof(probe_stats))
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    probe_stats snapshot{};
    RtlCopyMemory(&snapshot, &probe_state_ref().stats, sizeof(snapshot));
    RtlCopyMemory(buffer, &snapshot, sizeof(snapshot));
    return STATUS_SUCCESS;
}

NTSTATUS get_rule_info(void* buffer, ULONG output_length) noexcept
{
    if (buffer == nullptr || output_length < sizeof(probe_rule_info))
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    probe_rule_info info{};
    probe_rule_info_snapshot(info);
    RtlCopyMemory(buffer, &info, sizeof(info));
    return STATUS_SUCCESS;
}

NTSTATUS apply_rule_blob(const void* buffer, ULONG input_length) noexcept
{
    probe_state& state = probe_state_ref();
    if (buffer == nullptr || input_length == 0 || input_length > rule_blob_max_size)
    {
        probe_increment(state.stats.rejected_blobs);
        return STATUS_INVALID_BUFFER_SIZE;
    }

    rule_table* table = allocate_rule_table(static_cast<const std::uint8_t*>(buffer), input_length);
    if (table == nullptr)
    {
        probe_increment(state.stats.rejected_blobs);
        return STATUS_INVALID_PARAMETER;
    }

    // 换表与 block filter 的增删必须串行：先把新规则的 block filter 挂上，再换表，最后撤掉
    // 旧 filter。任何窗口里都不会出现「规则可见但没人执行」或「filter 常驻但没有对应规则」。
    KeWaitForSingleObject(&state.rule_mutex, Executive, KernelMode, FALSE, nullptr);

    UINT64 new_ids[max_block_filters]{};
    std::uint32_t new_count = 0;
    const bool enabled = state.config.enabled != 0;
    NTSTATUS status = enabled ? probe_install_block_filters(table->view, new_ids, new_count) : STATUS_SUCCESS;
    if (!NT_SUCCESS(status))
    {
        free_rule_table(table);
        probe_increment(state.stats.rejected_blobs);
        KeReleaseMutex(&state.rule_mutex, FALSE);
        return status;
    }

    probe_replace_rule_table(table);
    probe_increment(state.stats.rules_replaced);
    probe_delete_block_filters(state.block_filter_ids, state.block_filter_count);
    for (std::uint32_t index = 0; index < new_count; ++index)
    {
        state.block_filter_ids[index] = new_ids[index];
    }
    state.block_filter_count = new_count;
    KeReleaseMutex(&state.rule_mutex, FALSE);
    return STATUS_SUCCESS;
}

// 设备 DACL 已经把非管理员挡在 open 之外，这里再做一次调用者校验：即便以后 DACL 被改松，
// 也仍然只有 SYSTEM / Administrators 能下发配置与规则。
[[nodiscard]] bool caller_is_admin() noexcept
{
    // PsReferencePrimaryToken/SeTokenIsAdmin 都是 PASSIVE_LEVEL only；用户态 IOCTL 必然是
    // PASSIVE，但内核调用者打开设备不走 DACL，理论上能在 DISPATCH_LEVEL 发 IOCTL，先挡掉。
    if (KeGetCurrentIrql() != PASSIVE_LEVEL)
    {
        return false;
    }

    PACCESS_TOKEN token = PsReferencePrimaryToken(PsGetCurrentProcess());
    if (token == nullptr)
    {
        return false;
    }
    const BOOLEAN is_admin = SeTokenIsAdmin(token);
    PsDereferencePrimaryToken(token);
    return is_admin != FALSE;
}

} // namespace

_Dispatch_type_(IRP_MJ_CREATE) _Dispatch_type_(IRP_MJ_CLOSE) NTSTATUS
    dispatch_create_close(PDEVICE_OBJECT device_object, PIRP irp) noexcept
{
    UNREFERENCED_PARAMETER(device_object);
    irp->IoStatus.Status = STATUS_SUCCESS;
    irp->IoStatus.Information = 0;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}

_Dispatch_type_(IRP_MJ_DEVICE_CONTROL) NTSTATUS dispatch_device_control(PDEVICE_OBJECT device_object, PIRP irp) noexcept
{
    UNREFERENCED_PARAMETER(device_object);

    if (!caller_is_admin())
    {
        irp->IoStatus.Status = STATUS_ACCESS_DENIED;
        irp->IoStatus.Information = 0;
        IoCompleteRequest(irp, IO_NO_INCREMENT);
        return STATUS_ACCESS_DENIED;
    }

    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(irp);
    void* buffer = irp->AssociatedIrp.SystemBuffer;
    const ULONG input_length = stack->Parameters.DeviceIoControl.InputBufferLength;
    const ULONG output_length = stack->Parameters.DeviceIoControl.OutputBufferLength;
    ULONG_PTR information = 0;
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;

    switch (stack->Parameters.DeviceIoControl.IoControlCode)
    {
    case ioctl_set_config:
        status = apply_config(buffer, input_length);
        break;

    case ioctl_get_config:
        status = get_config(buffer, output_length);
        if (NT_SUCCESS(status))
        {
            information = sizeof(probe_config);
        }
        break;

    case ioctl_get_stats:
        status = get_stats(buffer, output_length);
        if (NT_SUCCESS(status))
        {
            information = sizeof(probe_stats);
        }
        break;

    case ioctl_reset_stats:
        probe_reset_stats();
        status = STATUS_SUCCESS;
        break;

    case ioctl_get_events:
    {
        std::uint32_t event_count = 0;
        status = probe_read_events(buffer, output_length, event_count);
        if (NT_SUCCESS(status))
        {
            information = static_cast<ULONG_PTR>(event_count) * sizeof(probe_event);
        }
        break;
    }

    case ioctl_set_rule_blob:
        status = apply_rule_blob(buffer, input_length);
        break;

    case ioctl_get_rule_info:
        status = get_rule_info(buffer, output_length);
        if (NT_SUCCESS(status))
        {
            information = sizeof(probe_rule_info);
        }
        break;

    case ioctl_measure_process_path:
    {
        // 诊断接口（发布前可移除）：只信任 I/O 管理器给出的缓冲区长度，并在入口重新收紧到本地
        // 数组的边界；不读取、也不信任输入内容里的任何长度字段。admin-only + 只读、无状态。
        constexpr ULONG required_output = sizeof(process_path_measurement);
        if (buffer == nullptr || output_length < required_output || input_length < 2 ||
            input_length > process_path_measure_input_max || (input_length % 2) != 0)
        {
            status = STATUS_INVALID_PARAMETER;
            break;
        }
        std::uint16_t utf16[process_path_measure_input_max / 2]{};
        static_assert(sizeof(utf16) == process_path_measure_input_max, "本地 UTF-16 缓冲区必须与上限一致");
        RtlCopyMemory(utf16, buffer, input_length);
        auto* measurement = static_cast<process_path_measurement*>(buffer);
        RtlZeroMemory(measurement, sizeof(*measurement));
        probe_measure_process_path(utf16, input_length, measurement->utf8, measurement->utf8_length);
        measurement->reserved = 0;
        information = sizeof(process_path_measurement);
        status = STATUS_SUCCESS;
        break;
    }

    default:
        break;
    }

    irp->IoStatus.Status = status;
    irp->IoStatus.Information = information;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return status;
}

void probe_take_snapshot(probe_config& config, const rule_table*& rules) noexcept
{
    probe_state& state = probe_state_ref();

    KIRQL old_irql{};
    const bool raised = probe_lock(old_irql);
    config = state.config;
    rules = state.rules;
    if (rules != nullptr)
    {
        InterlockedIncrement(const_cast<volatile LONG*>(&rules->references));
    }
    probe_unlock(raised, old_irql);
}

void probe_release_rules(const rule_table* rules) noexcept
{
    if (rules != nullptr)
    {
        InterlockedDecrement(const_cast<volatile LONG*>(&rules->references));
    }
}

void probe_increment(std::uint64_t& counter) noexcept
{
    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&counter));
}

void probe_log_event(const probe_event& event) noexcept
{
    KIRQL old_irql{};
    const bool raised = probe_lock(old_irql);
    probe_event entry = event;
    entry.sequence = static_cast<std::uint32_t>(probe_state_ref().event_total);
    probe_state_ref().events[probe_state_ref().event_write_index] = entry;
    probe_state_ref().event_write_index = (probe_state_ref().event_write_index + 1) % event_capacity;
    ++probe_state_ref().event_total;
    probe_unlock(raised, old_irql);
}

NTSTATUS probe_read_events(void* buffer, std::uint32_t buffer_size, std::uint32_t& event_count) noexcept
{
    event_count = 0;
    if (buffer == nullptr || buffer_size < sizeof(probe_event))
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    auto* target = static_cast<probe_event*>(buffer);
    const std::uint32_t capacity = buffer_size / sizeof(probe_event);

    KIRQL old_irql{};
    const bool raised = probe_lock(old_irql);
    const std::uint32_t available = probe_state_ref().event_total < event_capacity
                                        ? static_cast<std::uint32_t>(probe_state_ref().event_total)
                                        : event_capacity;
    const std::uint32_t wanted = available < capacity ? available : capacity;
    const std::uint32_t first = (probe_state_ref().event_write_index + event_capacity - wanted) % event_capacity;
    for (std::uint32_t index = 0; index < wanted; ++index)
    {
        target[index] = probe_state_ref().events[(first + index) % event_capacity];
    }
    probe_unlock(raised, old_irql);

    event_count = wanted;
    return STATUS_SUCCESS;
}

void probe_reset_stats() noexcept
{
    KIRQL old_irql{};
    const bool raised = probe_lock(old_irql);
    RtlZeroMemory(&probe_state_ref().stats, sizeof(probe_state_ref().stats));
    probe_state_ref().stats.abi_version = probe_abi_version;
    probe_state_ref().event_total = 0;
    probe_state_ref().event_write_index = 0;
    RtlZeroMemory(probe_state_ref().events, sizeof(probe_state_ref().events));
    probe_unlock(raised, old_irql);
}
} // namespace winforward::wfp::probe
