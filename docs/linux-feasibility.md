# Linux 移植可行性

这是一份基于本机已分析协议和 Linux 官方接口的工程评估，尚未做 Linux 实机验收。发行版不是主要限制，机型驱动、显卡驱动和桌面环境更关键。当前发布的两个 EXE 仍是 Windows 程序。

| 功能 | 评估 | 关键条件 |
|---|---|---|
| 键盘、顶灯、灯带的硬件灯效、颜色、亮度及应用/保存 | 移植把握高 | 沿用已分析的 USB HID 协议，通过 hidraw 通信；需设备权限及本机验证 |
| BIOS 阶段的灯光保留 | 原理上不依赖操作系统 | 使用控制器保存命令；本机实际重启保留仍待验收，不能把收到保存确认等同于已通过冷启动测试 |
| 柔光 | 移植把握高 | 用户态计算颜色并写 HID，不依赖特定桌面 |
| 光音视界 | 有成熟实现路径 | 使用 PipeWire 等音频接口采集播放或输入音频，再输出 HID；需正确处理所选音源和格式 |
| 光影随行 | 可实现，桌面相关 | Wayland 下使用 ScreenCast portal/PipeWire，受授权及桌面后端影响，后台无交互恢复不能一概保证 |
| CPU/RAM 基础监控、支持的 NVIDIA GPU 指标 | 有成熟实现路径 | proc/sysfs、hwmon、NVIDIA NVML；具体 GPU 字段仍取决于驱动支持 |
| 两个风扇转速及本机私有传感器 | 需要机型适配 | 若未通过 hwmon 暴露，需移植已分析的 WMI 传感器接口 |
| 护眼、ICC 配置管理 | 可可靠实现，需桌面适配 | 使用桌面夜间色温功能及色彩管理服务；ICC 文件可复用，但全局生效/HDR 行为要验证 |
| 显示器热插拔列表、界面、多语言、托盘、自启动、配置清理 | 有成熟实现路径 | Linux 桌面及会话接口；窗口外观需重做，原生材质随桌面变化 |
| F7/F8 自定义及系统快捷动作 | 可实现，需确认按键事件 | 标准输入事件可直接绑定；本机只经 WMI 发出的 OEM 按键需驱动转为输入事件 |
| Fn/Win Lock、Fn+Esc/Fn+K 状态同步 | 需机型 WMI 驱动和验证 | 固件原生动作可能保留，完整读取、设置、通知必须在 Linux 验证 |
| 四档性能、启动恢复、插电自动全速 | 策略成熟，真实切档需驱动 | Linux 有电源事件及 platform_profile 框架，但需调用本机四档固件接口；只改 CPU 调速器不足以覆盖这四档 |
| 屏幕响应加速、独显直连/混合与 BIOS 状态同步 | 可研究实现，暂不能承诺可靠 | 私有 WMI/ACPI 接口，需专用驱动、显卡驱动和实际重启验收 |

## 依据与边界

Linux 的 [hidraw](https://docs.kernel.org/hid/hidraw.html) 支持用户态原始 HID 报文通信，因此灯光是最直接的移植部分。硬件保存由控制器执行；具体非易失保留效果仍需本机重启验证。

[WMI 驱动开发指南](https://docs.kernel.org/wmi/driver-development-guide.html) 提供机型 WMI 设备绑定、方法和通知的接入路径。已知 Windows 协议能减少逆向工作量，但不能代替 Linux 驱动与实测。[platform_profile](https://docs.kernel.org/userspace-api/sysfs-platform_profile.html) 需要底层驱动映射实际档位；[power_supply](https://docs.kernel.org/power/power_supply_class.html) 可用于检测电源变化。

基础监控可利用 [hwmon](https://docs.kernel.org/hwmon/sysfs-interface.html) 与 [NVML](https://docs.nvidia.com/deploy/nvml-api/latest/index.html)。风扇编号和私有读数需要机型对应，不能直接用任意传感器编号充当 CPU/GPU 风扇。

音频可用 [PipeWire streams](https://docs.pipewire.org/page_streams.html)。屏幕同步可用 [ScreenCast portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.ScreenCast.html)，但授权和后台恢复能力随桌面后端而异。

ICC 配置可交给 [colord](https://www.freedesktop.org/software/colord/intro.html)，护眼可接入例如 [GNOME Night Light](https://help.gnome.org/gnome-help/display-night-light.html)。最终显示效果仍受桌面色彩管理和 HDR 管线影响。

独显直连属于硬件输出路径控制。内核的 [VGA Switcheroo 文档](https://docs.kernel.org/gpu/vga-switcheroo.html) 区分了带 MUX 的切换与无 MUX 的渲染卸载；实现本机 BIOS 独显直连需验证真实 MUX 状态和重启后的路径。

建议的移植顺序是先完成 HID 灯光、基础监控、界面与桌面功能，再实现机型 WMI 驱动及性能/锁键/传感器，最后验收显示加速和 MUX。此评估不表示已经发布 Linux 版本。
