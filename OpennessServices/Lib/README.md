# TIA Portal Openness C# 封装库 (TiaOpennessLibrary)

对 Siemens **TIA Portal Openness API**(`Siemens.Engineering.*`)的轻量封装，
把常见的工程组态自动化操作收敛成"一行代码"级别的方法，方便快速上手，
适合批量处理、CI/CD、工厂验收测试(FAT)等自动化脚本场景。

参考文档：[TIA Portal Openness 用于工程组态工作流自动化的 API](https://docs.tia.siemens.cloud/r/zh-cn/v20/tia-portal-openness-%E7%94%A8%E4%BA%8E%E5%B7%A5%E7%A8%8B%E7%BB%84%E6%80%81%E5%B7%A5%E4%BD%9C%E6%B5%81%E8%87%AA%E5%8A%A8%E5%8C%96%E7%9A%84-api/tia-portal-openness-api)

---

## 1. 环境准备(必须做，否则一定报错)

| 项目 | 要求 |
|---|---|
| TIA Portal 安装 | 安装时勾选 **"Openness"** 组件 |
| Windows 用户组 | 当前登录账户需加入本机 **"Siemens TIA Openness"** 用户组(控制面板 -> 计算机管理 -> 本地用户和组) |
| 项目框架 | .NET Framework **4.8**(需与所装 TIA Portal 大版本匹配，务必与官方文档核实对应关系) |
| 生成平台 | 建议设为 **x64**，AnyCPU 需关闭 "Prefer 32-bit" |
| 引用程序集 | `Siemens.Engineering.dll`、`Siemens.Engineering.Hmi.dll`(如需 HMI) |
| 程序集路径示例 | `C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20.0.0.0\` |
| License | 只需正常的 TIA Portal 授权，Openness 本身不需要单独许可证 |

> 提示：不同 TIA Portal 版本(V17/V18/V19/V20…)的 PublicAPI 文件夹版本号不同，
> 引用时注意选择与实际安装版本一致的 DLL，否则运行时会抛出版本不匹配的异常。

## 2. 文件说明

| 文件 | 作用 |
|---|---|
| `TiaOpennessHelper.cs` | 核心库，`TiaOpennessClient` 类，本文档主要围绕它 |
| `Example_Program.cs` | 一个完整的控制台调用示例 |

把这两个 `.cs` 文件加入你的 C# 项目(控制台 / WinForms / 服务均可)，添加好上面的程序集引用即可使用。

## 3. 快速开始

```csharp
using (var tia = new TiaOpennessClient(withUserInterface: true))
{
    tia.Logger = Console.WriteLine;

    tia.OpenProject(@"D:\Projects\MyProject\MyProject.ap20");
    var plc = tia.GetPlcSoftware("PLC_1");

    var result = tia.CompilePlcSoftware(plc);
    Console.WriteLine(result.State);

    tia.ExportAllBlocks(plc, @"D:\Export\Blocks");
    tia.SaveProject();
    tia.CloseProject();
}
```

`using` 结束时会自动调用 `Dispose()`，关闭工程并退出 TIA Portal 会话，无需手动清理。

## 4. API 速查表

### 会话 / 工程管理
| 方法 | 说明 |
|---|---|
| `new TiaOpennessClient(bool withUserInterface)` | 启动新会话(建议调试时用 `true`，无人值守用 `false`) |
| `TiaOpennessClient.AttachToRunningInstance()` | 附加到已手动打开的 TIA Portal |
| `OpenProject(path, withUpgrade)` | 打开工程，旧版本工程设 `withUpgrade=true` 自动升级 |
| `CreateProject(dir, name)` | 新建空工程 |
| `SaveProject()` / `SaveProjectAs(dir, name)` | 保存 / 另存为 |
| `CloseProject()` | 关闭当前工程(不退出会话) |
| `Dispose()` | 关闭工程并退出会话(建议用 `using`) |

### 设备 / PLC 定位
| 方法 | 说明 |
|---|---|
| `ListDeviceNames()` | 列出工程内所有一级设备名 |
| `GetDevice(name)` | 按名称取 `Device` 对象 |
| `GetPlcSoftware(deviceName)` | 递归定位 CPU 的软件容器(`PlcSoftware`) |
| `ListAllPlcSoftware()` | 取工程中全部 PLC 软件容器 |

### 编译
| 方法 | 说明 |
|---|---|
| `CompileDevice(deviceName)` | 编译整个站点(硬件+软件) |
| `CompilePlcSoftware(plcSoftware)` | 只编译软件(更快，最常用) |
| 返回值 `CompileResult` | `Success` / `State` / `Messages`(带时间戳的编译日志) |

### 程序块导入导出
| 方法 | 说明 |
|---|---|
| `ExportBlock(plc, blockName, path)` | 导出单个块为 XML |
| `ExportAllBlocks(plc, folder)` | 递归导出所有块，按分组生成子文件夹 |
| `ImportBlock(plc, path)` | 导入/覆盖更新单个块 |
| `ImportAllBlocks(plc, folder)` | 批量导入文件夹内所有 XML 块 |

### 变量表导入导出
| 方法 | 说明 |
|---|---|
| `ExportTagTable(plc, tableName, path)` | 导出变量表为 XML |
| `ImportTagTable(plc, path)` | 导入/更新变量表 |
| `ListTags(plc, tableName)` | 读取变量清单(名称/数据类型/地址)，便于校验或导出 Excel |

### 硬件组态 / 在线(高级，版本相关)
| 方法 | 说明 |
|---|---|
| `ExportDeviceAsAml(device, path)` | 导出设备硬件组态为 AutomationML |
| `GoOnline(device)` / `GoOffline(device)` | 切换在线/离线状态 |

> ⚠️ **关于在线/下载(Download to PLC)功能**：这是 Openness API 中版本差异最大、
> 交互性最强的部分(通常需要实现 `IDownloadPreviewDelegate` 等回调来处理确认框、
> 密码输入等弹窗)。本库只提供了 `GoOnline`/`GoOffline` 的基础封装作为起点；
> 真正的"一键下载程序到 CPU"建议先在 TIA Portal 安装目录里找到与你版本匹配的
> Openness 官方示例工程(通常在文档或 SIOS 下载中心提供)，参照其 Download
> 示例代码调整后再合入本库，以确保签名与你的 TIA Portal 版本完全一致。

## 5. 常见坑

1. **权限问题**：没加入 "Siemens TIA Openness" 用户组时，`new TiaPortal(...)` 会直接抛异常。
2. **位数不匹配**：项目生成平台与 TIA Portal 位数不一致会导致加载 DLL 失败。
3. **版本不匹配**：引用的 `Siemens.Engineering.dll` 版本必须和实际运行的 TIA Portal 版本一致。
4. **工程被占用**：同一工程不能被两个 TIA Portal 实例同时打开，包括手动打开的界面。
5. **Openness API 逐版本演进**：个别方法签名(尤其 Online/Download、AML 导出选项)在新版本中可能变化，
   编译不过时优先看 IntelliSense 提示或对照当前版本的官方 chm 帮助文档。

## 6. 扩展建议

这个库覆盖的是最常用的"工程/PLC/编译/导入导出"场景。如果你后续需要：
- HMI 画面批量生成/替换
- 硬件模块批量插入(如批量加 IO 模块)
- 多用户工程(Multiuser Engineering)相关操作
- OPC UA / S7 通信做运行时数据读写(注意：这**不属于** Openness API 范畴，
  Openness 只管"工程组态"，不做"运行时数据访问"，需要用 S7comm/OPC UA 等其他协议库)

可以在 `TiaOpennessClient` 类里继续按同样的风格增加方法即可。
