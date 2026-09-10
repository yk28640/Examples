using Siemens.Engineering;
using Siemens.Engineering.Compare;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OpennessServices
{
    /// <summary>
    /// 对 Siemens TIA Portal Openness API 的轻量封装。
    ///
    /// 目标：把常见的工程组态自动化操作(打开/新建工程 -> 定位PLC -> 编译
    /// -> 导入导出块/变量表 -> 保存关闭)收敛成"一行代码"级别的方法，方便快速集成到
    /// 批量处理、CI/CD、工厂验收测试等自动化脚本中。
    ///
    /// 依赖程序集(需在 TIA Portal 安装目录下查找并添加引用，例如):
    ///   C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20.0.0.0\Siemens.Engineering.dll
    ///   C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20.0.0.0\Siemens.Engineering.Hmi.dll
    ///
    /// 使用前提:
    ///   1. 安装 TIA Portal 时勾选了 "Openness" 选项。
    ///   2. 当前 Windows 账户已加入本机用户组 "Siemens TIA Openness"。
    ///   3. 目标项目为 .NET Framework 4.8 (需与所装 TIA Portal 版本匹配，不同大版本可能要求不同 .NET 版本)。
    ///   4. 项目"生成"平台需与 TIA Portal 位数一致(通常为 x64，且不要用 AnyCPU 的 "Prefer 32-bit")。
    ///
    /// 注意: Online/Download 相关 API 在不同 TIA Portal 版本间差异较大，
    /// 且常需要实现回调接口来处理确认对话框，本库中对应方法仅为通用模板，
    /// 实际项目请结合所用版本的 SDK 帮助(chm)/IntelliSense 核实签名。
    /// </summary>
    public class TiaOpennessClient : IDisposable
    {
        /// <summary>底层 TiaPortal 会话对象，如需使用原生 API 可直接访问。</summary>
        public TiaPortal Portal { get; private set; }

        /// <summary>当前打开/创建的工程。</summary>
        public Project CurrentProject { get; private set; }

        /// <summary>日志回调，默认输出到 Debug 窗口，可重定向到 Console.WriteLine 或日志框架。</summary>
        public Action<string> Logger { get; set; } = msg => System.Diagnostics.Debug.WriteLine(msg);  // System.Diagnostics.Debug.

        private void Log(string msg) => Logger?.Invoke($"[TIA-Openness] {DateTime.Now:HH:mm:ss} {msg}");

        private TiaOpennessClient() { }
        
        /// <summary>
        /// 启动一个新的 TIA Portal 会话。
        /// </summary>
        /// <param name="withUserInterface">
        /// true = 打开界面(便于调试、观察进度)；
        /// false = 无界面后台模式(适合服务器/无人值守自动化，速度更快)。
        /// </param>
        public TiaOpennessClient(bool withUserInterface = true)
        {
            var mode = withUserInterface ? TiaPortalMode.WithUserInterface : TiaPortalMode.WithoutUserInterface;
            Portal = new TiaPortal(mode);
            Log($"TIA Portal 会话已启动 (界面模式: {mode})");
        }

        /// <summary>附加到当前已经手动打开的 TIA Portal 实例，而不是新开一个进程。</summary>
        public static TiaOpennessClient AttachToRunningInstance()
        {
            var process = TiaPortal.GetProcesses().FirstOrDefault();
            if (process == null)
                throw new InvalidOperationException("未找到正在运行的 TIA Portal 实例，请先手动打开或改用构造函数新建会话。");

            var client = new TiaOpennessClient { Portal = process.Attach() };
            client.Log("已附加到正在运行的 TIA Portal 实例。");
            return client;
        }

        #region 工程管理 Project

        /// <summary>打开一个已存在的工程(.apXX)。</summary>
        /// <param name="withUpgrade">工程是旧版本 TIA 创建时设为 true，会自动升级到当前版本。</param>
        public Project OpenProject(string projectFilePath, bool withUpgrade = false)
        {
            var fi = new FileInfo(projectFilePath);
            if (!fi.Exists)
                throw new FileNotFoundException("工程文件不存在", projectFilePath);

            CurrentProject = withUpgrade
                ? Portal.Projects.OpenWithUpgrade(fi)
                : Portal.Projects.Open(fi);

            Log($"已打开工程: {CurrentProject.Name}");
            return CurrentProject;
        }

        /// <summary>新建一个空工程。</summary>
        public Project CreateProject(string targetDirectory, string projectName)
        {
            var dir = new DirectoryInfo(targetDirectory);
            if (!dir.Exists) dir.Create();

            CurrentProject = Portal.Projects.Create(dir, projectName);
            Log($"已创建工程: {projectName} @ {targetDirectory}");
            return CurrentProject;
        }

        /// <summary>保存当前工程。</summary>
        public void SaveProject()
        {
            EnsureProject();
            CurrentProject.Save();
            Log("工程已保存");
        }

        /// <summary>另存为新工程。</summary>
        public void SaveProjectAs(string newDirectory, string newName)
        {
            EnsureProject();
            CurrentProject.SaveAs(new DirectoryInfo(newDirectory));
            Log($"工程已另存为: {newName} @ {newDirectory}");
        }

        /// <summary>关闭当前工程(不关闭 TIA Portal 会话本身)。</summary>
        public void CloseProject()
        {
            if (CurrentProject == null) return;
            CurrentProject.Close();
            Log("工程已关闭");
            CurrentProject = null;
        }

        private void EnsureProject()
        {
            if (CurrentProject == null)
                throw new InvalidOperationException("尚未打开或创建工程，请先调用 OpenProject / CreateProject。");
        }

        #endregion

        #region 设备与 PLC 定位 Device / PLC

        /// <summary>列出工程内所有一级设备名称(如 PLC_1、HMI_1 等)。</summary>
        public List<string> ListDeviceNames()
        {
            EnsureProject();
            return CurrentProject.Devices.Select(d => d.Name).ToList();
        }

        /// <summary>按名称获取设备对象。</summary>
        public Device GetDevice(string deviceName)
        {
            EnsureProject();
            var device = CurrentProject.Devices.Find(deviceName);
            if (device == null)
                throw new ArgumentException($"未找到设备: {deviceName}");
            return device;
        }

        /// <summary>
        /// 在指定设备下递归查找 PLC 软件容器(CPU 的程序/变量所在处)。
        /// 常见用法: GetPlcSoftware("PLC_1")
        /// </summary>
        public PlcSoftware GetPlcSoftware(string deviceName)
        {
            var device = GetDevice(deviceName);
            foreach (DeviceItem item in device.DeviceItems)
            {
                var software = FindPlcSoftware(item);
                if (software != null) return software;
            }

            throw new ArgumentException($"设备 {deviceName} 下未找到 PLC 软件容器(可能不是 CPU 设备)。");
        }

        /// <summary>获取工程中所有设备下的全部 PLC 软件容器。</summary>
        public List<PlcSoftware> ListAllPlcSoftware()
        {
            EnsureProject();
            var result = new List<PlcSoftware>();
            foreach (var device in CurrentProject.Devices)
                foreach (DeviceItem item in device.DeviceItems)
                {
                    var sw = FindPlcSoftware(item);
                    if (sw != null) result.Add(sw);
                }
            return result;
        }

        private PlcSoftware FindPlcSoftware(DeviceItem item)
        {
            var container = item.GetService<SoftwareContainer>();
            if (container?.Software is PlcSoftware plcSoftware) return plcSoftware;

            foreach (DeviceItem child in item.DeviceItems)
            {
                var found = FindPlcSoftware(child);
                if (found != null) return found;
            }
            return null;
        }

        #endregion

        #region 编译 Compile

        public class CompileResult
        {
            public bool Success { get; set; }
            public string State { get; set; }
            public List<string> Messages { get; set; } = new List<string>();
        }

        /// <summary>编译指定设备(整个站点的硬件+软件)。</summary>
        public CompileResult CompileDevice(string deviceName)
        {
            var device = GetDevice(deviceName);
            var deviceItem = device.DeviceItems.FirstOrDefault();
            if (deviceItem == null)
                throw new InvalidOperationException($"设备 {deviceName} 没有可编译的模块项。");

            var compileService = deviceItem.GetService<ICompilable>();
            if (compileService == null)
                throw new InvalidOperationException($"设备 {deviceName} 不支持编译服务。");

            return RunCompile(() => compileService.Compile());
        }

        /// <summary>直接编译已获取的 PlcSoftware 对象(仅编译软件，更快更常用)。</summary>
        public CompileResult CompilePlcSoftware(PlcSoftware plcSoftware)
        {
            var compileService = plcSoftware.GetService<ICompilable>();
            if (compileService == null)
                throw new InvalidOperationException("该 PlcSoftware 不支持编译服务。");

            return RunCompile(() => compileService.Compile());
        }

        private CompileResult RunCompile(Func<Siemens.Engineering.Compiler.CompilerResult> compileAction)
        {
            var raw = compileAction();
            var result = new CompileResult
            {
                Success = raw.State == CompilerResultState.Success || raw.State == CompilerResultState.Warning,
                State = raw.State.ToString()
            };
            foreach (var msg in raw.Messages)
                result.Messages.Add($"[{msg.DateTime:HH:mm:ss}] {msg.Description}");

            Log($"编译完成，结果: {result.State}");
            return result;
        }

        #endregion

        #region 在线程序比较 Online Compare

        public class OnlineCompareSummary
        {
            public bool Success { get; set; }
            public string State { get; set; }
            public string Details { get; set; }
        }

        /// <summary>
        /// 将离线工程中的 PLC 程序与当前在线 PLC 程序进行比较。
        /// TIA Portal 会根据当前用户的在线访问权限建立连接。
        /// </summary>
        public OnlineCompareSummary ComparePlcProgramOnline(string projectPath)
        {
            return ComparePlcProgramOnline(projectPath, null);
        }

        public OnlineCompareSummary ComparePlcProgramOnline(string projectPath, string deviceName)
        {

            EnsureProjectIsClosed();
            var project = OpenProject(projectPath);
            try
            {
                var device = string.IsNullOrWhiteSpace(deviceName)
                    ? project.Devices.FirstOrDefault()
                    : project.Devices.Find(deviceName);
                if (device == null)
                    throw new ArgumentException($"未找到设备: {deviceName}");

                var plcsoftware = GetPlcSoftware(device.Name);
                var deviceItem = FindDeviceItemWithSoftware(device);
                var onlineProvider = deviceItem?.GetService<OnlineProvider>();
                if (onlineProvider == null)
                    throw new InvalidOperationException($"设备 {device.Name} 不支持在线服务。");

                var knowHowProtectedBlockNames = GetAllBlocks(plcsoftware.BlockGroup)
                                                .Where(b => b.IsKnowHowProtected)
                                                .Select(b => b.Name)
                                                .ToList();

                SetConnectionWithSlot(onlineProvider);
                var result = plcsoftware.CompareToOnline();

                var details = result.RootElement == null
                    ? string.Empty
                    : WriteNonIdenticalResult(result.RootElement, string.Empty);

            // 将 Know-how 保护块名称追加到 WriteResult 的结果中
            if (knowHowProtectedBlockNames.Count > 0)
            {
                details += Environment.NewLine
                    + Environment.NewLine
                    + "Know-how 保护的块:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, knowHowProtectedBlockNames
                        .Select(name => "块名: " + name));
            }
            else
            {
                details += Environment.NewLine
                    + Environment.NewLine
                    + "Know-how 保护的块: 无";
            }

                var summary = new OnlineCompareSummary
                {
                    Success = result.RootElement != null,
                    State = result.RootElement == null
                        ? "Failed"
                        : result.RootElement.ComparisonResult.ToString(),
                    Details = details
                };

                Log($"在线程序比较完成: {summary.State}");
                return summary;
            }
            finally
            {
                CloseProject();
            }
        }

        private static DeviceItem FindDeviceItemWithSoftware(Device device)
        {
            foreach (DeviceItem item in device.DeviceItems)
            {
                var found = FindDeviceItemWithSoftware(item);
                if (found != null) return found;
            }
            return null;
        }

        private static DeviceItem FindDeviceItemWithSoftware(DeviceItem item)
        {
            // 返回既包含 PLC 软件容器又支持在线服务的实际设备项（递归查找）
            if (item.GetService<SoftwareContainer>()?.Software is PlcSoftware &&
                item.GetService<OnlineProvider>() != null)
                return item;

            foreach (DeviceItem child in item.DeviceItems)
            {
                var found = FindDeviceItemWithSoftware(child);
                if (found != null) return found;
            }
            return null;
        }

        private void EnsureProjectIsClosed()
        {
            if (CurrentProject != null)
                CloseProject();
        }

        #endregion

        #region 程序块导入 / 导出 Blocks

        /// <summary>导出单个块为 XML 文件(按名称在整棵分组树中查找)。</summary>
        public void ExportBlock(PlcSoftware plcSoftware, string blockName, string exportFilePath)
        {
            var block = FindBlockRecursive(plcSoftware.BlockGroup, blockName);
            if (block == null)
                throw new ArgumentException($"未找到块: {blockName}");

            var fi = new FileInfo(exportFilePath);
            if (fi.Directory != null && !fi.Directory.Exists) fi.Directory.Create();

            block.Export(fi, Siemens.Engineering.ExportOptions.WithDefaults);
            Log($"块 {blockName} 已导出到 {exportFilePath}");
        }

        /// <summary>递归导出指定分组(默认根分组)下的全部块，每个块一个 XML，子分组对应子文件夹。</summary>
        public void ExportAllBlocks(PlcSoftware plcSoftware, string targetFolder, PlcBlockGroup group = null)
        {
            group = group ?? plcSoftware.BlockGroup;
            Directory.CreateDirectory(targetFolder);

            foreach (PlcBlock block in group.Blocks)
            {
                var filePath = Path.Combine(targetFolder, $"{SanitizeFileName(block.Name)}.xml");
                block.Export(new FileInfo(filePath), Siemens.Engineering.ExportOptions.WithDefaults);
                Log($"导出块: {block.Name}");
            }
            foreach (PlcBlockGroup subGroup in group.Groups)
                ExportAllBlocks(plcSoftware, Path.Combine(targetFolder, SanitizeFileName(subGroup.Name)), subGroup);
        }

        /// <summary>从 XML 文件导入(或覆盖更新)一个块到指定分组(默认根分组)。</summary>
        public void ImportBlock(PlcSoftware plcSoftware, string importFilePath, PlcBlockGroup group = null)
        {
            group = group ?? plcSoftware.BlockGroup;
            var fi = new FileInfo(importFilePath);
            if (!fi.Exists)
                throw new FileNotFoundException("导入文件不存在", importFilePath);

            group.Blocks.Import(fi, Siemens.Engineering.ImportOptions.Override);
            Log($"已从 {importFilePath} 导入块");
        }

        /// <summary>批量导入文件夹内所有 XML 块文件。</summary>
        public void ImportAllBlocks(PlcSoftware plcSoftware, string sourceFolder, PlcBlockGroup group = null)
        {
            if (!Directory.Exists(sourceFolder))
                throw new DirectoryNotFoundException(sourceFolder);

            group = group ?? plcSoftware.BlockGroup;
            foreach (var file in Directory.GetFiles(sourceFolder, "*.xml"))
                ImportBlock(plcSoftware, file, group);

            foreach (PlcBlockGroup subGroup in group.Groups)
            {
                var subFolder = Path.Combine(sourceFolder, SanitizeFileName(subGroup.Name));
                if (Directory.Exists(subFolder))
                    ImportAllBlocks(plcSoftware, subFolder, subGroup);
            }
        }

        private PlcBlock FindBlockRecursive(PlcBlockGroup group, string blockName)
        {
            var block = group.Blocks.FirstOrDefault(b => b.Name == blockName);
            if (block != null) return block;

            foreach (PlcBlockGroup sub in group.Groups)
            {
                var found = FindBlockRecursive(sub, blockName);
                if (found != null) return found;
            }
            return null;
        }

        #endregion

        #region 变量表导入 / 导出 Tag Tables

        /// <summary>导出指定变量表为 XML。</summary>
        public void ExportTagTable(PlcSoftware plcSoftware, string tableName, string exportFilePath)
        {
            var table = FindTagTable(plcSoftware, tableName);
            if (table == null)
                throw new ArgumentException($"未找到变量表: {tableName}");

            var fi = new FileInfo(exportFilePath);
            if (fi.Directory != null && !fi.Directory.Exists) fi.Directory.Create();

            table.Export(fi, Siemens.Engineering.ExportOptions.WithDefaults);
            Log($"变量表 {tableName} 已导出到 {exportFilePath}");
        }

        /// <summary>从 XML 导入/更新变量表。</summary>
        public void ImportTagTable(PlcSoftware plcSoftware, string importFilePath)
        {
            var fi = new FileInfo(importFilePath);
            plcSoftware.TagTableGroup.TagTables.Import(fi, Siemens.Engineering.ImportOptions.Override);
            Log($"已从 {importFilePath} 导入变量表");
        }

        /// <summary>读取变量表中的变量清单(名称 / 数据类型 / 地址)，便于快速核查或导出 Excel。</summary>
        public List<(string Name, string DataType, string Address)> ListTags(PlcSoftware plcSoftware, string tableName)
        {
            var table = FindTagTable(plcSoftware, tableName);
            if (table == null)
                throw new ArgumentException($"未找到变量表: {tableName}");

            return table.Tags.Select(t => (t.Name, t.DataTypeName, t.LogicalAddress)).ToList();
        }

        private PlcTagTable FindTagTable(PlcSoftware plcSoftware, string tableName)
        {
            var root = plcSoftware.TagTableGroup;
            var direct = root.TagTables.FirstOrDefault(t => t.Name == tableName);
            if (direct != null) return direct;

            foreach (PlcTagTableUserGroup sub in root.Groups)
            {
                var found = FindTagTableInUserGroup(sub, tableName);
                if (found != null) return found;
            }
            return null;
        }

        private PlcTagTable FindTagTableInUserGroup(PlcTagTableUserGroup group, string tableName)
        {
            var t = group.TagTables.FirstOrDefault(x => x.Name == tableName);
            if (t != null) return t;

            foreach (PlcTagTableUserGroup sub in group.Groups)
            {
                var found = FindTagTableInUserGroup(sub, tableName);
                if (found != null) return found;
            }
            return null;
        }

        #endregion

        #region 硬件组态导出 (AML) — 版本相关，用前请核对签名

        /// <summary>
        /// 将指定设备的硬件组态导出为 AutomationML (.aml) 文件，便于版本对比/归档。
        /// 注意: 该方法在不同 TIA Portal 版本下重载可能不同，请以实际 IntelliSense 为准。
        /// </summary>
        public void ExportDeviceAsAml(Device device, string exportFilePath)
        {
            //EnsureProject();
            //var fi = new FileInfo(exportFilePath);
            //if (fi.Directory != null && !fi.Directory.Exists) fi.Directory.Create();

            //CurrentProject.Devices.Export(new List<Device> { device }, fi, Siemens.Engineering.HW.ExportOptions.None);
            //Log($"设备 {device.Name} 的硬件组态已导出到 {exportFilePath}");
        }

        #endregion

        #region 在线 / 下载 Online & Download — 高级功能，模板代码，请按实际版本核实

        /// <summary>
        /// [模板方法] 通过以太网(PN)接口将站点下载到 CPU。
        /// 不同 TIA Portal 版本的 Online/Download API 差异较大，且通常需要实现
        /// IDownloadPreviewDelegate / IDownloadConfigurationDelegate 等回调来处理
        /// "覆盖确认""密码输入"等交互式对话框。
        /// 建议先在 TIA Portal 安装目录下的 Openness 示例工程中找到与当前版本匹配的
        /// Download 示例代码，再据此调整本方法。
        /// </summary>
        public CompareResult GoOnlineAndCompare(Device device)
        {
            CompareResult compare_res=null;
            var deviceItem = device.DeviceItems[1];
            var onlineProvider = deviceItem?.GetService<OnlineProvider>();
            if (onlineProvider == null)
                throw new InvalidOperationException($"设备 {device.Name} 不支持在线服务。");

            SetConnectionWithSlot(onlineProvider);
            if (onlineProvider.State == OnlineState.Online) 
            {
                compare_res=ComparePlcToOnlinePlc(deviceItem.GetService<SoftwareContainer>().Software as PlcSoftware);//GetPlcSoftware(device.Name)
                onlineProvider.GoOffline();
            }
            //onlineProvider.GoOnline();
            Log($"{device.Name} 已切换为在线状态");

            return compare_res;
        }

        private OnlineConfigurationDelegate m_OnlineConfigurationDelegate = null;


        public void SetConnectionWithSlot(OnlineProvider onlineProvider)
        {
            try
            {
                ConnectionConfiguration configuration = onlineProvider.Configuration;
                ConfigurationMode mode = configuration.Modes.Find(@"PN/IE");
                foreach (var item in mode.PcInterfaces)
                {
                    Console.WriteLine(item.Name);
                }
#if Plcsim
            ConfigurationPcInterface pcInterface = mode.PcInterfaces.Find("PLCSIM", 1);
#else
                ConfigurationPcInterface pcInterface = mode.PcInterfaces[2];
#endif


                ConfigurationTargetInterface slot = pcInterface.TargetInterfaces.Find("1 X2");
                m_OnlineConfigurationDelegate = OnlineCallBackMethod;
                configuration.OnlineLegitimation += m_OnlineConfigurationDelegate;
                configuration.ApplyConfiguration(slot);
                // After applying configuration, you can go online
                onlineProvider.GoOnline();

            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("无法建立在线连接。", ex);
            }
            
        }

        private void OnlineCallBackMethod(OnlineConfiguration onlineConfiguration)
        {
            var tlsCommunication = onlineConfiguration as TlsVerificationConfiguration;
            if (tlsCommunication == null) return;
            tlsCommunication.CurrentSelection = TlsVerificationConfigurationSelection.Trusted;
        }

        public void GoOffline(Device device)
        {
            var deviceItem = device.DeviceItems.FirstOrDefault();
            var onlineProvider = deviceItem?.GetService<OnlineProvider>();
            onlineProvider?.GoOffline();
            Log($"{device.Name} 已切换为离线状态");
        }
        private CompareResult ComparePlcToOnlinePlc(PlcSoftware plcSoftware)

        {

            if (plcSoftware != null)

            {

                CompareResult compareResult = plcSoftware.CompareToOnline();

                return compareResult;
            }
            return null;
        }

        public string WriteResult(CompareResultElement compareResultElement, string indent)
        {
            var builder = new StringBuilder();
            AppendResult(compareResultElement, indent, builder);
            return builder.ToString();
        }

        private string WriteNonIdenticalResult(
            CompareResultElement compareResultElement,
            string indent)
        {
            var builder = new StringBuilder();
            AppendNonIdenticalResult(compareResultElement, indent, builder);
            return builder.ToString();
        }

        private void AppendNonIdenticalResult(
            CompareResultElement compareResultElement,
            string indent,
            StringBuilder builder)
        {
            if (compareResultElement == null)
                return;

            var comparisonResult = compareResultElement.ComparisonResult.ToString();
            var isObjectsIdentical = comparisonResult.IndexOf(
                "Identical",
                StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isObjectsIdentical)
            {
                builder.Append(indent)
                    .Append("<")
                    .Append(compareResultElement.LeftName)
                    .Append("> <")
                    .Append(compareResultElement.ComparisonResult)
                    .Append("> <")
                    .Append(compareResultElement.RightName)
                    .Append("> <")
                    .Append(compareResultElement.DetailedInformation)
                    .AppendLine(">");

                indent += " ";
            }

            if (compareResultElement.Elements == null)
                return;

            foreach (var childElement in compareResultElement.Elements)
            {
                AppendNonIdenticalResult(childElement, indent, builder);
            }
        }

        public void AppendResult(
            CompareResultElement compareResultElement,
            string indent,
            StringBuilder builder)
        {
            if (compareResultElement == null)
                return;

            builder.Append(indent)
                .Append("<")
                .Append(compareResultElement.LeftName)
                .Append("> <")
                .Append(compareResultElement.ComparisonResult)
                .Append("> <")
                .Append(compareResultElement.RightName)
                .Append("> <")
                .Append(compareResultElement.DetailedInformation)
                .AppendLine(">");

            AppendResults(compareResultElement.Elements, indent + " ", builder);
        }

        public void AppendResults(
            IEnumerable<CompareResultElement> compareResultElements,
            string indent,
            StringBuilder builder)
        {
            if (compareResultElements == null)
                return;

            foreach (var compareResultElement in compareResultElements)
            {
                AppendResult(compareResultElement, indent, builder);
            }
        }
        public  void WriteResultOffical(CompareResultElement compareResultElement, string indent)

        {

            Console.WriteLine("{0} <{1}> <{2}> <{3}> <{4}> ",

            indent,

            compareResultElement.LeftName,

            compareResultElement.ComparisonResult,

            compareResultElement.RightName,

            compareResultElement.DetailedInformation);

            WriteResultOffical(compareResultElement.Elements, indent);

        }

        public  void WriteResultOffical(IEnumerable<CompareResultElement> compareResultElements, string indent)

        {

            indent += " ";

            foreach (CompareResultElement compareResultElement in compareResultElements)

            {

                WriteResultOffical(compareResultElement, indent);

            }

        }
        #endregion

        #region 工具方法

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
        private static IEnumerable<PlcBlock> GetAllBlocks(PlcBlockGroup blockGroup)
        {
            if (blockGroup == null)
                yield break;

            // 当前组下的块
            foreach (PlcBlock block in blockGroup.Blocks)
            {
                yield return block;
            }

            // 递归遍历所有子组
            foreach (PlcBlockGroup childGroup in blockGroup.Groups)
            {
                foreach (PlcBlock block in GetAllBlocks(childGroup))
                {
                    yield return block;
                }
            }
        }
        #endregion

        /// <summary>释放资源：自动关闭工程并退出 TIA Portal 会话。</summary>
        public void Dispose()
        {
            try { CloseProject(); } catch { /* 忽略关闭异常，确保资源仍能释放 */ }
            var portal = Portal;
            Portal = null;
            portal?.Dispose();
            Log("TIA Portal 会话已释放");
        }
    }
}
