# 后端 ACTCLI 程序块详细比较

## 文件与执行边界

- 实际后端是 `SATExample.sln` 引用的 `../WindowsServiceV14/WindowsServiceV14.csproj`。仓库目录内的 `WindowsServiceV14` 是另一份副本，本功能没有同步覆盖它。发布时必须使用解决方案引用项目的构建输出；提交源码时也要单独保管这些仓库外文件。
- 前端是 `CentralAdmin`（Vue 3 + Vite），不是 AutomationPlatform 的 Blazor 页面。
- 后端仍用两个独立 TiaPortal 实例打开相邻离线工程。历史状态按对象 ModifiedDate 筛选，不保证时间相同意味着程序内容一致。
- 仅对两侧存在且 `Block/Different` 的程序块，在关闭工程前使用 V14 SP1 `PlcBlock.Export(..., ExportOptions.WithDefaults)` 导出新旧 XML。工艺对象、新增、删除和相同块不提供此入口。不写入 PLC、不修改工程程序。
- 前端仅发送导出 ID，并获取任务状态和 PDF；不传送 XML、不调用本机 EXE。

## 配置与部署

先在 Visual Studio 构建 WindowsServiceV14。新源文件通过已有的 `Directory.Build.targets` 显式加入编译（IDE 不允许直接编辑已加载的旧式 csproj）。部署完整输出及 `tagsconfig.json`，重启服务；配置更改同样需要重启。

`tagsconfig.json` 的 `projectPath`、`comparasionFolder`、`listenIp` 保持原用途。新增 `blockComparison`：

| 设置 | 默认 | 说明 |
| --- | --- | --- |
| ExecutablePath | `C:\Program Files\Siemens\Automation\SIMATIC Automation Compare Tool\app-1.4.0\ACT-CLI\ACTCLI.exe` | 已在开发机确认的实际路径，目标机应重新核实；必须为固定 ACTCLI.exe 绝对路径 |
| TimeoutSeconds | 180 | 单次进程超时，5–1800 秒 |
| MaxConcurrency | 1 | 1–4 个工作线程；建议保持 1，避免 ACTCLI 实例互相影响 |
| QueueCapacity | 8 | 最多排队 1–32 个任务 |
| RetentionHours | 168 | 从创建时间计算，1–720 小时；应大于最长排队加执行时间 |
| MaxJobs | 100 | 尚未清理的任务配额，1–1000；达到上限返回 HTTP 429 |
| MaxFileMegabytes | 20 | 每份输入 XML 和输出 PDF 的限制，1–100 MB |

工具参数固定为：旧 XML、新 XML、`-d`、`-e`、输出 PDF。通过 ProcessStartInfo 直接启动，不使用 cmd/PowerShell；字符串参数按 Windows 规则引用。每个任务使用独立目录，避免覆盖报告；Windows Job Object 在超时、取消、服务退出或异常时清理进程树。

每次执行持续读取并限制日志输出，只保留 stdout/stderr 各前 8192 字符，通过现有 Trace 记录退出码和诊断。部署 Windows 服务时需配置适当的 Trace 监听器/日志采集，不应仅依赖 Visual Studio 输出窗口。

成功状态表示本次进程退出且生成了通过大小、`%PDF-` 头和 `%%EOF` 完整性检查的报告，不意味着程序相同，也不意味着执行了完整 PDF 语义验证。若生成有效 PDF 但退出码非零，保留报告并在页面明确显示退出码警告；工具具体退出码语义仍应根据现场版本文档和实测确认。

## 历史与文件生命周期

产物位于 `comparasionFolder/.block-comparisons/`：

- `exports/{导出ID}/`：清单及可识别的新旧 XML，名称包含 PLC/块名的安全截断形式和版本时间，目录 GUID 防止不同工程、PLC、分组重名冲突。完整对象身份保存在清单，不依赖清洗后的文件名。
- 成对文件全部成功后才原子发布导出目录。整体比较失败或历史生成时工程变化，会清理本次未发布的导出。
- `ChangesHistory.json` 版本升级为 3。旧缓存首次升级时重新生成。之后历史缓存仅按缓存版本、数据结构和工程路径/长度/修改时间决定是否复用：优先返回内存文档，服务重启后读取 JSON 并恢复内存缓存；工程未变不重新打开 TIA、不重导出 XML，也不改写 JSON 或历史生成时间。新增工程只比较新增的相邻工程对。
- XML 缺失、为空或块导出失败不会使历史缓存失效；导出失败信息也随 JSON 保留。点击详细比较时才检查导出产物，缺失时返回不可用，而不是隐式重算历史。若需修复已丢失 XML 或重试之前失败的导出，应由管理员停服备份并移除 `ChangesHistory.json` 后重启，明确触发一次重建；普通刷新只重新读取历史，不强制后台重算。
- `jobs/{任务ID}/`：任务 JSON 及 report.pdf，执行期间另有临时输入副本，完成后移除副本。任务记录原子写入，服务重启后未完成任务标记失败，不自动重新运行。
- 复用同一导出对的运行中任务和未过期的有效成功报告；失败后 10 秒内重复点击返回已有任务，避免快速重试不断启动进程。
- 过期的终态任务/报告在后续 API 访问或服务首次初始化时清理，不是精确到时的定时删除。运行/排队任务不被请求清理逻辑删除。
- 已发布 XML 不自动按年龄删除，以免破坏历史引用。长期运行需监控磁盘。维护时先停止服务，备份历史及报告；如需完全重建，可以移除本功能的 `.block-comparisons` 目录及 `ChangesHistory.json`，保留原始工程，然后重启并刷新历史。不要在运行中手工修改清单或 XML。硬终止可能留下 `.tmp` 目录，同样在停服维护时清理。
- 清单和输入/报告目录应只允许服务账户及可信管理员写入。过长比较目录可能触发旧 .NET/Openness 路径限制；块名已截断，但根目录仍应尽量短。

## API 与安全边界

浏览器直接使用所选 `src/config/tagSources.js` 来源的服务地址：

- `POST /api/block-comparisons`，JSON 对象仅传 `ExportId`。请求必须有 Content-Length 且不超过 1024 字节；成功 HTTP 202，返回任务。
- `GET /api/block-comparisons/{taskId}`，返回 `Id`、`ExportId`、`State`、`Message`、`CreatedUtc`、`ExpiresUtc`、`ExitCode`。状态为 Queued、Running、Succeeded 或 Failed。
- `GET /api/block-comparisons/{taskId}/report`，返回 `application/pdf`、inline 文件名、no-store；不存在、过期或尚未完成时返回 404。
- 非法 ID 返回 400；历史变更、导出缺失或正在生成历史导致暂不可用返回 409；队列/配额满返回 429；配置或服务异常返回 503。

按项目选择，本功能不增加登录或密钥，接口仅适用于可信内网。任何能访问服务的人都可能启动比较及读取知道任务 ID 的报告；GUID 和 CORS **不提供身份认证或用户级报告隔离**。必须用主机防火墙/网络设备限制访问来源，保持默认 localhost 监听直到配置完成，禁止直接暴露公网。若以后需要跨安全域，应另行配置认证代理或应用级认证。

服务沿用现有 CORS 策略，允许跨域 GET/POST/OPTIONS。生产 HTTPS 页面访问 HTTP 来源可能被浏览器拦截；应通过可信 HTTPS 反向代理或统一 HTTPS 部署，代理不得移除 POST 的 Content-Length。PDF 通过浏览器请求获取为 Blob，关闭/切换页面时释放，不在浏览器永久保存（主动下载除外）。

## 验证

自动化验证不打开真实 TIA 工程：

1. Visual Studio 构建解决方案引用的 WindowsServiceV14 Debug 项目。
2. 在仓库根目录运行 `dotnet build Tests/BlockComparisonValidation/BlockComparisonValidation.csproj`，然后运行 `Tests/BlockComparisonValidation/bin/Debug/net462/BlockComparisonValidation.exe`。它引用后端 Debug 输出，临时配置只写入验证程序输出目录，运行后恢复；使用模拟 ACTCLI、临时 localhost OWIN 监听及临时工程清单，覆盖筛选、参数引用、缓存/文件缺失、队列、重启、超时、PDF/API 和 CORS。不改变后端实际配置。
3. 在 CentralAdmin 运行 `npm test`、`npm run test:browser`、`npm run build`。浏览器测试使用模拟后端；需 Chrome/Edge，可用 CHROME_PATH 指定。

实施时已通过上述隔离后端验证（含实际 localhost HTTP 路由）及 16 项前端测试、浏览器回归和 Vite 生产构建。另外通过新执行器对本机两份示例 XML 的临时副本运行了已安装的真实 ACTCLI 1.4.0：退出码 0，生成 883232 字节 PDF，通过头/结束标记校验。此结果来自当前交互会话，不代表 Windows 服务账户验收已完成。

全工作区构建/IDE 仍可能报告与此独立前端运行链路不同的诊断：根 SATExampleV50 默认递归包含仓库内另一份 WindowsServiceV14，存在重复程序集属性和不匹配依赖；Visual Studio 的 Vue/JavaScript 类型诊断也不等同于此 JavaScript/Vite 项目的生产构建结果。本次只排除了新增的独立验证目录，未调整已有后端副本或全仓库前端类型配置。目标后端应单独构建，并使用以上验证命令核实。

现场验收仍必须使用真实后端 Windows 服务账户完成：

- ACTCLI 许可有效，账户有安装目录执行、比较目录读写及 Openness 所需权限。
- 非交互服务会话下 `-d -e` 正常生成 PDF，不依赖桌面弹窗、首次启动对话框或用户配置；任务进程可加入 Windows Job Object。手工桌面运行成功不能替代这一验证。
- 核对 ACTCLI 的旧/新方向、退出码、子进程退出行为；尤其检查 Electron/单实例行为不会提前退出启动器后把工作交给其他用户会话。当前实现要求执行进程退出时报告已完整生成。
- 用两份真实工程验证修改普通块的导出及内容差异；用受保护块验证失败提示。
- 从另一台可信内网电脑点击“修改”，确认任务状态、中文/空格路径、PDF 预览及下载；确认失败重试、超时、中断后重启和配额提示。
- 真实 Windows 服务账户、真实 Siemens XML 导出与另一台电脑端到端验证不在模拟测试覆盖范围内，发布前必须完成。
