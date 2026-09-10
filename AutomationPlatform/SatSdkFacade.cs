// =============================================================================
//  SatSdkFacade.cs
//  基于《SIMATIC Automation Tool SDK Windows user guide, V5.0 SP4》
//  第 4 章 "SIMATIC Automation Tool API for .NET framework" 编写的精简封装库。
//
//  和你之前那份 SAT 用户手册（纯 GUI 手册）不同 —— 这份 SDK 文档是真正的
//  .NET 编程接口文档，本文件里的类型名（Network / IProfinetDeviceCollection /
//  IProfinetDevice / ICPU / Result 等）都是 SDK 里**真实存在**的类型，
//  不是我编出来的。方法名、参数名、参数类型均逐字核对自手册原文，
//  并在每个方法注释里标注了对应的章节号和页码，方便你自己再翻手册核实。
//
//  【重要】使用前必须做的事：
//  1. 安装 SIMATIC Automation Tool SDK，在项目里添加它提供的 SDK 程序集引用。
//  2. 把下面 "using SimaticAutomationToolSdk;" 这一行，替换成你在
//     SDK 示例工程里看到的真实命名空间（手册 "Getting started with the API"
//     章节的示例代码顶部会有 using 语句，请以那个为准 —— 我核对到的原文
//     只写了类型名如 Network / ICPU，没有显式给出完整命名空间字符串）。
//  3. 本文件里方法体只做“调用 SDK 原生方法”这一层封装，不包含协议实现，
//     所以只要命名空间和程序集引用对了，就可以直接编译使用。
//
//  【准确性标注】
//  - 无特殊说明的方法：签名已对照手册原文核实（含返回类型、参数名、参数类型）。
//  - 标了"推断"的地方：手册目录确认了方法存在及大致用途，但我在本次抽取范围内
//    没有拿到该方法的完整参数表原文，因此按 SDK 一贯的命名/调用风格做了合理封装。
//    编译前建议用 SDK 自带的 IntelliSense/对象浏览器核对一遍这几处。
// =============================================================================

// TODO: 替换为你项目中实际引用的 SDK 命名空间

using Microsoft.VisualBasic;
using Siemens.Automation.AutomationTool.API;
using Siemens.Automation.OMSPlus.IDs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using static Siemens.Automation.AutomationTool.OSL.OMSManaged;


namespace AutomationPlatform
{
    /// <summary>
    /// SIMATIC Automation Tool .NET API 的精简调用外观（Facade）。
    /// 典型用法：
    ///     var sat = new SatFacade();
    ///     sat.SetNetworkInterfaceByIndex(0);
    ///     var (devices, scanErrors) = sat.ScanNetwork("192.168.0.[1-50]");
    ///     var cpu = sat.FindCpuByIp(devices, "192.168.0.10");
    ///     if (cpu != null)
    ///     {
    ///         cpu.Selected = true;
    ///         sat.SetCpuRun(cpu, run: true);
    ///         cpu.Selected = false;
    ///     }
    /// </summary>
    public class SatFacade
    {
        private readonly Network _network = new Network();

        #region 网络接口选择 —— §4.1.1 Getting started with the API (Page 39)
        public List<String> interfaces = new List<String>();
        /// <summary>
        /// 列出本机所有可用网络接口卡（用于选择用哪张网卡和 PROFINET 网络通信）。
        /// 对应 Network.QueryNetworkInterfaceCards 方法 (Page 76)。
        /// [推断] 手册确认了此方法的存在及作用，具体返回集合的元素类型请以 IntelliSense 为准。
        /// </summary>
        public Result ListNetworkInterfaces() => _network.QueryNetworkInterfaceCards(out interfaces);


        /// <summary>
        /// 设置应用程序后续通信要使用的网络接口。
        /// 对应 Network.SetCurrentNetworkInterface 方法 (Page 77)。[推断，参数类型请核对]
        /// </summary>
        public Result SetNetworkInterface(string networkInterfaceCard) =>
            _network.SetCurrentNetworkInterface(networkInterfaceCard);

        #endregion

        #region 扫描网络 / 设备表 —— §4.8.5 ScanNetworkDevices (Page 78), §4.9 IProfinetDeviceCollection

        /// <summary>
        /// 扫描 PROFINET 网络上的设备（发送广播 DCP 命令）。
        /// 对应 §4.8.5 ScanNetworkDevices method (Page 78)，签名已逐字核对：
        ///     IScanErrorCollection ScanNetworkDevices(out IProfinetDeviceCollection baseDevices,
        ///                                              string strFilter = null,
        ///                                              NetworkScanType networkScanType = default)
        /// </summary>
        /// <param name="ipFilter">
        /// 可选 IP 过滤器。例如 "192.168.0.[1-20]" 只扫描 .1~.20；
        /// "192.168.0.[2,5,10]" 只扫描列出的几个地址；多个过滤条件用分号分隔。
        /// 用过滤器能显著缩短大网络的扫描时间。
        /// </param>
        /// <returns>(设备集合, 扫描错误集合)。扫描是否整体成功看 Errors.Succeeded。</returns>
        public (IProfinetDeviceCollection Devices, IScanErrorCollection Errors) ScanNetwork(string ipFilter = null)
        {
            IProfinetDeviceCollection devices;
            IScanErrorCollection errors = _network.ScanNetworkDevices(out devices, ipFilter);
            return (devices, errors);
        }

        /// <summary>按 IP 地址在设备表中查找设备。对应 §4.9.3.1 FindDeviceByIP method (Page 85)。</summary>
        public IProfinetDevice FindByIp(IProfinetDeviceCollection devices, string dottedIp) =>
            devices.FindDeviceByIP(EncodeIp(dottedIp));

        /// <summary>按 IP 地址查找设备并转换为 ICPU；找不到或不是 CPU 类设备则返回 null。</summary>
        public ICPU FindCpuByIp(IProfinetDeviceCollection devices, string dottedIp) =>
            devices.FindDeviceByIP(EncodeIp(dottedIp)) as ICPU;

        /// <summary>
        /// 手动按 IP 插入设备（适用于已知 IP、路由器后面、或不想整网扫描的场景 —— 网络扫描发现不了路由器后面的设备）。
        /// 对应 §4.9.5.2 InsertDeviceByIP method (Page 92)。[推断，具体重载参数个数请核对]
        /// </summary>
        public IScanErrorCollection InsertDeviceByIp(IProfinetDeviceCollection devices, string dottedIp,out  IProfinetDevice insertedDevice){
            var res =devices.InsertDeviceByIP(EncodeIp(dottedIp), 0, 0xffffff00, null, out insertedDevice);
            return res;
        }
          

        /// <summary>把 "192.168.0.10" 这样的点分字符串 IP，转换成 SDK 方法要求的 uint 编码格式。</summary>
        public static uint EncodeIp(string dottedIp)
        {
            var b = dottedIp.Split('.').Select(byte.Parse).ToArray();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        /// <summary>把 SDK 的 uint 编码 IP 转换回 "a.b.c.d" 字符串，便于打印/显示。</summary>
        public static string DecodeIp(uint encoded) =>
            $"{(encoded >> 24) & 0xFF}.{(encoded >> 16) & 0xFF}.{(encoded >> 8) & 0xFF}.{encoded & 0xFF}";

        #endregion

        #region 通用设备操作 —— §4.10 IProfinetDevice interface

        /// <summary>选中/取消选中设备。绝大多数写操作前必须先 Selected=true，操作完再置回 false。</summary>
        public void Select(IProfinetDevice device, bool selected) => device.Selected = selected;

        /// <summary>识别设备（如闪烁指示灯）。对应 §4.10.2.5 Identify method (Page 113)。</summary>
        public Result Identify(IProfinetDevice device) => device.Identify();

        /// <summary>快速 Ping 设备，确认网络可达性。对应 §4.10.2.12 QuickPing method (Page 117)。</summary>
        public Result QuickPing(IProfinetDevice device) => device.QuickPing();

        /// <summary>
        /// 刷新设备完整状态。网络扫描后设备对象只有部分数据，
        /// 必须先对受保护设备调用 SetPassword，再对每个设备调用 RefreshStatus 才能拿到完整信息。
        /// 对应 §4.10.2.13 RefreshStatus method (Page 117)。
        /// </summary>
        public Result RefreshStatus(IProfinetDevice device) => device.RefreshStatus();

        /// <summary>
        /// 设置设备的 IP / 子网掩码 / 网关。对应 §4.10.2.18 SetIP method (Page 121)，签名已逐字核对：
        ///     Result SetIP(uint nIP, uint nSubnet, uint nGateway)
        /// 注意：设备在 STEP 7 项目中的端口配置必须是"IP 地址直接在设备上设置"；不支持路由器后的设备。
        /// </summary>
        public Result SetIp(IProfinetDevice device, string ip, string subnetMask, string gateway) =>
            device.SetIP(EncodeIp(ip), EncodeIp(subnetMask), EncodeIp(gateway));

        /// <summary>设置 PROFINET 设备名称。对应 §4.10.2.19 SetProfinetName method (Page 123)。[推断，参数名请核对]</summary>
        public Result SetProfinetName(IProfinetDevice device, string name) => device.SetProfinetName(name);

        /// <summary>复位设备通信参数。对应 §4.10.2.14 ResetCommunicationParameters method (Page 119)。</summary>
        public Result ResetCommunicationParameters(IProfinetDevice device) =>
            device.ResetCommunicationParameters();

        /// <summary>
        /// 为设备设置密码（访问受保护的 CPU 前必须调用）。
        /// 对应 §4.12.4.24 SetPassword method (Page 165)，签名已核对：Result SetPassword(AuthorizationData password)。
        /// 注意：如果设备本身没有开启密码保护（Protected == false），千万不要调用本方法，
        /// 手册明确说明这样会导致 API 抛出严重错误异常。
        /// </summary>
        public Result SetPassword(ICPU cpu, AuthorizationData password) => cpu.SetPassword(password);

        #endregion

        #region 固件 / 程序更新 —— §4.10.2.1 FirmwareUpdate (Page 105), §4.12.4.15 ProgramUpdate (Page 154)

        /// <summary>
        /// 更新设备固件（CPU、本地/分布式 I/O、显示屏、信号板/模块、通信模块、HMI、SINAMICS 驱动装置均支持）。
        /// 对应 IHardware.SetFirmwareFile (Page 66) + IProfinetDevice.FirmwareUpdate (Page 105) 的组合调用。
        /// </summary>
        public Result UpdateFirmware(IProfinetDevice device, string firmwareFilePath)
        {
            if (device is IHardware hw)
            {
                hw.SetFirmwareFile(firmwareFilePath);
            }
            return device.FirmwareUpdate(device.ID,false);
        }

        /// <summary>
        /// 更新 CPU 程序（整体下载硬件组态+软件，不支持局部下载）。
        /// 对应 §4.12.4.15 ProgramUpdate method (Page 154) 的标准调用步骤：
        ///     1. Selected = true
        ///     2. 若受保护，先 SetPassword（写权限）
        ///     3. SetProgramFolder 指定新程序所在文件夹（用 TIA Portal 的卡浏览器功能生成）
        ///     4. ProgramUpdate()
        /// 注意：执行后 CPU 会进入 STOP 模式。
        /// </summary>
        public Result UpdateProgram(ICPU cpu, string programFolderPath, AuthorizationData writePassword = null)
        {
            if (writePassword != null)
            {
                cpu.SetPassword(writePassword);
            }
            cpu.SetProgramFolder(programFolderPath);
            return cpu.ProgramUpdate();
        }

        #endregion

        #region 备份 / 恢复 —— §4.12.4.1 Backup (Page 138), §4.12.4.17 Restore (Page 159)

        /// <summary>
        /// 备份 CPU（含硬件组态 + 程序，无法只备份部分内容）。
        /// 对应 §4.12.4.1 Backup method (Page 138)，签名已逐字核对：Result Backup(string strFile)。
        /// 限制：只支持通过 CPU 的网络接口备份，不支持通过 CM/CP 接口备份。
        /// </summary>
        public Result BackupCpu(ICPU cpu, string saveToFilePath) => cpu.Backup(saveToFilePath);

        /// <summary>
        /// 从备份文件恢复 CPU。对应 §4.12.4.18 SetBackupFile (Page 161) + §4.12.4.17 Restore (Page 159)。
        /// [推断] 按手册目录顺序（先 SetBackupFile 指定文件，再调用 Restore）封装，请核对具体调用顺序。
        /// </summary>
        public Result RestoreCpu(ICPU cpu, string backupFilePath, string backupFilePassword = null)
        {
            cpu.SetBackupFile(backupFilePath);
            if (!string.IsNullOrEmpty(backupFilePassword))
            {
                cpu.SetBackupFilePassword(new AuthorizationData(ConnectionType.AccessLevel, "",
"WriteAccessBackupFilePassword"));
            }
            return cpu.Restore();
        }

        #endregion

        #region 运行模式 / 时间 —— §4.12.4.23 SetOperatingState (Page 165), §4.12.4.21 SetCurrentDateTime (Page 164)

        /// <summary>
        /// 设置 CPU 运行模式（RUN / STOP）。
        /// 对应 §4.12.4.23 SetOperatingState method (Page 165)，用法已逐字核对手册示例：
        ///     retVal = myCPU.SetOperatingState(OperatingStateREQ.Run);
        /// 调用前请先检查 ICPU.ChangeModeAllowed 属性，某些 CPU 不支持切换模式。
        /// </summary>
        public Result SetCpuRun(ICPU cpu, bool run)
        {
            if (!cpu.ChangeModeAllowed)
            {
                return null; // 按需替换为你项目里表示"不支持"的错误处理方式
            }
            return cpu.SetOperatingState(run ? OperatingStateREQ.Run : OperatingStateREQ.Stop);
        }

        /// <summary>设置 CPU / 驱动装置当前时间。对应 §4.12.4.21 SetCurrentDateTime method (Page 164)。[推断，参数名请核对]</summary>
        public Result SetCpuTime(ICPU cpu, DateTime dateTime) => cpu.SetCurrentDateTime(dateTime);

        /// <summary>读取 CPU / 驱动装置当前时间。对应 §4.12.4.11 GetCurrentDateTime method (Page 151)。[推断，返回方式请核对]</summary>
        public Result GetCpuTime(ICPU cpu,out DateTime datetime) => cpu.GetCurrentDateTime(out datetime);

        #endregion

        #region 存储卡 / 复位 —— §4.12.4 ICPU methods

        /// <summary>格式化存储卡。对应 §4.12.4.10 FormatMemoryCard method (Page 149)。</summary>
        public Result FormatMemoryCard(ICPU cpu) => cpu.FormatMemoryCard();

        /// <summary>CPU 存储器复位（MRES）。对应 §4.12.4.14 MemoryReset method (Page 153)。</summary>
        public Result MemoryReset(ICPU cpu) => cpu.MemoryReset();

        /// <summary>复位为出厂默认设置。对应 §4.12.4.16 ResetToFactoryDefaults method (Page 157)。</summary>
        public Result ResetToFactoryDefaults(ICPU cpu) => cpu.ResetToFactoryDefaults();

        /// <summary>删除数据日志。对应 §4.12.4.6 DeleteDataLog method (Page 142)。</summary>
        public Result DeleteDataLog(ICPU cpu,string strFileName) => cpu.DeleteDataLog(strFileName);

        #endregion

        #region 批量操作与导出 —— §4.9.8 ExecuteOperations (Page 95), §4.9.4 导出方法

        /// <summary>
        /// 对设备表中所有 Selected=true 的设备批量执行事先配置好的操作
        /// （如批量固件更新、批量备份等）。对应 §4.9.8.1 ExecuteOperations method (Page 95)。
        /// 执行前需要先给每台设备设置好相应参数（如 SetFirmwareFile / SetBackupFile 等）。
        /// [推断] 是否需要额外参数指定"要执行哪种操作"请核对手册 Page 95。
        /// </summary>

        public IScanErrorCollection ExecuteBatch(IProfinetDeviceCollection devices, ExecuteOperationType eot) => devices.ExecuteOperations(eot);

        /// <summary>导出设备信息到文件。对应 §4.9.4.3 ExportDeviceInformation method (Page 88)。</summary>
        public void ExportDeviceInfo(IProfinetDeviceCollection devices, string filePath) =>
            devices.ExportDeviceInformation(filePath);

        /// <summary>导出设备诊断信息到文件。对应 §4.9.4.4 ExportDeviceDiagnostics method (Page 89)。</summary>
        public void ExportDeviceDiagnostics(IProfinetDeviceCollection devices, string filePath) =>
            devices.ExportDeviceDiagnostics(filePath, Language.English, TimeFormat.Local);

        #endregion
    }
}

// =============================================================================
//  章节 <-> 封装方法 对照表（* = 签名已逐字核对原文；未标 * = 方法存在已确认，参数按惯例推断）
// -----------------------------------------------------------------------------
//  §4.8.5   ScanNetworkDevices (P78)              *  -> SatFacade.ScanNetwork()
//  §4.9.3.1 FindDeviceByIP (P85)                     -> SatFacade.FindByIp() / FindCpuByIp()
//  §4.9.5.2 InsertDeviceByIP (P92)                   -> SatFacade.InsertDeviceByIp()
//  §4.9.4.3 ExportDeviceInformation (P88)             -> SatFacade.ExportDeviceInfo()
//  §4.9.4.4 ExportDeviceDiagnostics (P89)             -> SatFacade.ExportDeviceDiagnostics()
//  §4.9.8.1 ExecuteOperations (P95)                   -> SatFacade.ExecuteBatch()
//  §4.10.2.5  Identify (P113)                         -> SatFacade.Identify()
//  §4.10.2.12 QuickPing (P117)                        -> SatFacade.QuickPing()
//  §4.10.2.13 RefreshStatus (P117)                    -> SatFacade.RefreshStatus()
//  §4.10.2.14 ResetCommunicationParameters (P119)     -> SatFacade.ResetCommunicationParameters()
//  §4.10.2.18 SetIP (P121)                         *  -> SatFacade.SetIp()
//  §4.10.2.19 SetProfinetName (P123)                  -> SatFacade.SetProfinetName()
//  §4.10.2.1  FirmwareUpdate (P105)                *  -> SatFacade.UpdateFirmware()
//  §4.12.4.1  Backup (P138)                        *  -> SatFacade.BackupCpu()
//  §4.12.4.17 Restore (P159)                          -> SatFacade.RestoreCpu()
//  §4.12.4.15 ProgramUpdate (P154)                 *  -> SatFacade.UpdateProgram()
//  §4.12.4.10 FormatMemoryCard (P149)                 -> SatFacade.FormatMemoryCard()
//  §4.12.4.14 MemoryReset (P153)                      -> SatFacade.MemoryReset()
//  §4.12.4.16 ResetToFactoryDefaults (P157)           -> SatFacade.ResetToFactoryDefaults()
//  §4.12.4.6  DeleteDataLog (P142)                    -> SatFacade.DeleteDataLog()
//  §4.12.4.21 SetCurrentDateTime (P164)               -> SatFacade.SetCpuTime()
//  §4.12.4.11 GetCurrentDateTime (P151)               -> SatFacade.GetCpuTime()
//  §4.12.4.23 SetOperatingState (P165)             *  -> SatFacade.SetCpuRun()
//  §4.12.4.24 SetPassword (P165)                   *  -> SatFacade.SetPassword()
// =============================================================================
