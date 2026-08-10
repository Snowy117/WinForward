# NDISAPI 审查设计

以 native 调用的一次生命周期为单位：声明布局 -> request 构造 -> driver open/read/send -> Win32 error -> SafeHandle close。每条 handle 必须标记来源：枚举 adapter request handle、捕获 buffer 的观察值、或 driver lifetime handle；三者不能被名称相近而互换。

Linux 只验证 layout、封送边界与可注入 seam。涉及真实 `ndisapi.dll`、admin、adapter 或 IOCTL 的结论必须在 Windows 验证记录中区分“已执行”和“pending”。
