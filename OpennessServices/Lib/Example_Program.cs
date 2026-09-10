//using System;


//namespace OpennessServices
//{
//    /// <summary>
//    /// 示例：演示如何用 TiaOpennessClient 在 10 行代码内完成
//    /// "打开工程 -> 定位PLC -> 编译 -> 导出块与变量表 -> 保存关闭"。
//    /// </summary>
//    class Program
//    {
//        static void Main(string[] args)
//        {
//            // withUserInterface: true = 打开界面方便观察；调试完成后可改为 false 做无人值守批处理
//            using (var tia = new TiaOpennessClient(withUserInterface: true))
//            {
//                tia.Logger = Console.WriteLine; // 把日志打印到控制台

//                try
//                {
//                    // 1. 打开工程(把路径换成你自己的 .apXX 文件)
//                    tia.OpenProject(@"D:\Projects\MyProject\MyProject.ap20");

//                    // 2. 列出所有设备，确认名称
//                    foreach (var name in tia.ListDeviceNames())
//                        Console.WriteLine($"发现设备: {name}");

//                    // 3. 定位 PLC 软件容器
//                    var plc = tia.GetPlcSoftware("PLC_1");

//                    // 4. 编译
//                    var result = tia.CompilePlcSoftware(plc);
//                    Console.WriteLine($"编译结果: {result.State}");
//                    foreach (var msg in result.Messages)
//                        Console.WriteLine(msg);

//                    if (!result.Success)
//                    {
//                        Console.WriteLine("编译失败，终止后续操作。");
//                        return;
//                    }

//                    // 5. 导出所有程序块到本地文件夹(自动按分组建子文件夹)
//                    tia.ExportAllBlocks(plc, @"D:\Export\Blocks");

//                    // 6. 导出一张变量表
//                    tia.ExportTagTable(plc, "Tag_table_1", @"D:\Export\Tags\Tag_table_1.xml");

//                    // 7. 读取变量表内容做校验/打印
//                    foreach (var tag in tia.ListTags(plc, "Tag_table_1"))
//                        Console.WriteLine($"{tag.Name}\t{tag.DataType}\t{tag.Address}");

//                    // 8. 从文件导入一个更新过的块(示例)
//                    // tia.ImportBlock(plc, @"D:\Import\FB_Motor.xml");

//                    // 9. 保存工程
//                    tia.SaveProject();
//                }
//                finally
//                {
//                    // 10. 关闭工程(using 结束时会自动 Dispose 并退出 TIA Portal 会话)
//                    tia.CloseProject();
//                }
//            }
//        }
//    }
//}
