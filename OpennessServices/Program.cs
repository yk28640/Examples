using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
namespace OpennessServices
{
    internal static class Program
    {
        private const string ResultFilePath = @"E:\Temp\OpennessServices\compare-result.txt";
        static void Main(string[] args)
        {
           // string applicationPath = @"E:\\Program Files\\SIMATIC Automation Tool SDK Trial\\OpennessServices\\bin\\Debug\\OpennessServices.exe";
           // string lastWriteTimeUtcFormatted = String.Empty;
           // DateTime lastWriteTimeUtc;
           // HashAlgorithm hashAlgorithm = SHA256.Create();
           // FileStream stream = File.OpenRead(applicationPath);
           // byte[] hash = hashAlgorithm.ComputeHash(stream);
           // // this is how the hash should appear in the .reg file
           // string convertedHash = Convert.ToBase64String(hash);
           //// lastWriteTimeUtc = fileInfo.LastWriteTimeUtc; // fileInfo not defined


            if (args.Length > 0 && string.Equals(args[0], "compare", StringComparison.OrdinalIgnoreCase))
            {
                RunCompare(args);
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "upload", StringComparison.OrdinalIgnoreCase))
            {
                RunUpload(args);
                return;
            }

            //调试比较程序
            //string[] argsSet = new string[3];
            //argsSet[1] = @"E:\projects\V25075_L1_PLC07_20260505_1141_修改了画面\V25075_L1_PLC07_20260505_1141.ap20";
            //argsSet[2] = "ET 200SP station_4";
            //RunCompare(argsSet);

            //调试上传程序
            //string[] argsSet = new string[3];
            //argsSet[1] = @"E:\TMP\";
            //argsSet[2] = "mynewUploadedStation_" + DateTime.Now.ToString("yyyyMMddHHmmss");
            //RunUpload(argsSet);

            Console.WriteLine("用法:");
            Console.WriteLine("  OpennessServices.exe compare <工程路径> <设备名称>");
            Console.WriteLine("  OpennessServices.exe upload <目标目录> <工程名称>");
        }
        private static void RunUpload(string[] args)
        {
            try
            {
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("参数不足，需要目标目录和工程名称。");
                    Environment.ExitCode = 2;
                    return;
                }

                var directoryPath = args[1];
                var projectName = args[2];
                using (var tia = new TiaOpennessClient(withUserInterface: true))
                {
                    tia.UploadStation(directoryPath, projectName);
                }

                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"上传项目失败: {ex.GetType().Name}: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }

        private static void RunCompare(string[] args)
        {

#if DEBUG
            //if (!Debugger.IsAttached)
            //{
            //    Debugger.Launch();
            //}

            //测试代码
            //testc(args);
#endif
            //WriteResultFile("Running", false, "在线程序比较任务已开始。");

            if (args.Length < 3)
            {
                WriteResultFile("Failed", false, "参数不足，需要工程路径和设备名称。");
                Environment.ExitCode = 2;
                return;
            }

            var projectPath = args[1];
            var deviceName = args[2];

            try
            {
                using (var tia = new TiaOpennessClient(withUserInterface: false))
                {
                    //tia.Logger = message => Console.Error.WriteLine(message);
                    //tia.OpenProject(projectPath);

                    // 定位 PLC 软件容器并在线比较
                    //var plc = tia.GetPlcSoftware(deviceName);
                    //var device = tia.GetDevice(deviceName);

                    var compareResult = tia.ComparePlcProgramOnline(projectPath, deviceName);
                    
                    // 输出 JSON 到标准输出，并写入结果文件
                    // 假定 compareResult 包含 Success, State, Details 字段；根据实际 API 调整字段名
                    WriteResultFile(GetString(compareResult, "State"), GetBool(compareResult, "Success"), GetString(compareResult, "Details"));
                    
                    Environment.ExitCode = GetBool(compareResult, "Success") ? 0 : 1;
                    Environment.Exit(Environment.ExitCode);
            }
            }
            catch (Exception ex)
            {
                WriteResultFile("Failed", false, ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
               
                Environment.ExitCode = 1;
            }
        }

        // 辅助：安全从动态/未知对象读取字段（在编译时若无动态，需要根据实际返回类型替换实现）
        private static bool GetBool(object obj, string propName)
        {
            if (obj == null) return false;
            try
            {
                var type = obj.GetType();
                var p = type.GetProperty(propName);
                if (p != null && p.PropertyType == typeof(bool))
                    return (bool)p.GetValue(obj);
                var f = type.GetField(propName);
                if (f != null && f.FieldType == typeof(bool))
                    return (bool)f.GetValue(obj);
            }
            catch { }
            return false;
        }

        private static string GetString(object obj, string propName)
        {
            if (obj == null) return string.Empty;
            try
            {
                var type = obj.GetType();
                var p = type.GetProperty(propName);
                if (p != null)
                {
                    var v = p.GetValue(obj);
                    return v?.ToString() ?? string.Empty;
                }
                var f = type.GetField(propName);
                if (f != null)
                {
                    var v = f.GetValue(obj);
                    return v?.ToString() ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }
        /// <summary>
        /// 写入结果文件，供外部调用程序读取，只被检测一次，只写入结果或者异常
        /// </summary>
        /// <param name="state"></param>
        /// <param name="success"></param>
        /// <param name="details"></param>
        private static void WriteResultFile(string state, bool success, string details)
        {
            try
            {
                var directory = Path.GetDirectoryName(ResultFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var content =
                    "Time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    "Success=" + success.ToString().ToLowerInvariant() + Environment.NewLine +
                    "State=" + (state ?? string.Empty) + Environment.NewLine +
                    "Details=" + Environment.NewLine +
                    (details ?? string.Empty);

                var temporaryFile = ResultFilePath + ".tmp";
                File.WriteAllText(temporaryFile, content, new System.Text.UTF8Encoding(false));

                if (File.Exists(ResultFilePath))
                {
                    File.Delete(ResultFilePath);
                }

                File.Move(temporaryFile, ResultFilePath);
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

      
  
        private static void testc(string[] args)
        {

            var projectPath = args[1];
            var deviceName = args[2];

            Console.WriteLine("调试模式下运行比较程序。");


            TiaPortal tiaPortal = new TiaPortal(TiaPortalMode.WithUserInterface);
            tiaPortal.Projects.Open(new FileInfo(projectPath));
            var plcsoftware = tiaPortal.Projects[0].Devices[4].DeviceItems[1].GetService<SoftwareContainer>().Software as PlcSoftware;
            Console.WriteLine(plcsoftware.Name);


            TiaOpennessClient tia = new TiaOpennessClient();
            
            tia.SetConnectionWithSlot(tiaPortal.Projects[0].Devices[4].DeviceItems[1].GetService<OnlineProvider>());
            var res = plcsoftware.CompareToOnline();
            tia.WriteResultOffical(res.RootElement, string.Empty);
            Console.WriteLine(tia.WriteResult(res.RootElement, string.Empty));
            tiaPortal.Dispose();
        }
    } }
