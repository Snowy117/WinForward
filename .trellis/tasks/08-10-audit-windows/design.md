# Windows 边界审查设计

以两个转换边界审计：(1) NDIS 内部名/handle/MAC 到稳定 Windows adapter identity，(2) IP Helper native rows 到可用的 process attribution。每个边界需要区分可靠标识、可接受 fallback 和必须拒绝猜测的 ambiguity。

纯测试使用 `IpAdapterInfo` 或 ABI projection seam；真实 NetworkInterface、GetExtended*Table、受保护进程和 adapter 变化必须保留为 Windows smoke/hardware 事实。报告不得把已知 OS 限制归类为产品 defect。
