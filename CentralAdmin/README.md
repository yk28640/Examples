# CentralAdmin

Vue 3 + Vite 工业监控看板，读取 AutomationPlatform 数据和 WindowsServiceV14 的变量/历史比较数据，并可通过 POST 触发后端离线程序块详细比较。本界面不能发起设备扫描或写入 PLC 变量。

## 页面

- 扫描历史（`#scan-history`）：扫描批次与设备快照。
- 操作日志（`#operation-logs`）：最新 100 条 TIA Portal 操作日志。
- PLC 变量（`#plc-tags`）：先选择设备来源，再读取该来源的变量集合。表格显示设备、分组、变量表、名称、数据类型与地址，不包含实时变量值。
- 更改历史（`#changes-history`）：沿用 PLC 变量页的设备来源，查看相邻历史工程比较、各 PLC 的程序块/工艺对象状态及新旧 ModifiedDate。
- ServiceV14 日志（`#service-event-logs`）：独立页面，选择设备来源，直接读取该来源机器的 TIA Portal 操作日志；不替换原“操作日志”页面。

变量页面支持跨字段关键词搜索、设备/分组/变量表/数据类型组合筛选，每页 25 条，空分组显示为“未分组”。切换来源会取消旧请求，重置筛选及分页；已成功读取的来源按来源 ID 在当前页面会话内缓存，切回不会自动请求，点击“刷新当前来源”更新。不同来源分别保存数据、成功读取时间及错误状态，刷新失败时明确标记缓存不是最新结果，不会将失败伪装为“暂无变量”。重新加载浏览器会清除缓存。

使用侧边栏切换页面，小屏幕使用顶部导航；支持 Hash 地址直达。切换到扫描历史或操作日志页面后，变量页面的当前来源及筛选会保留。

## ServiceV14 日志

新增页面直接请求所选来源的 `${baseUrl}/GetEventLog?maximumEntries=100`，复用 `src/config/tagSources.js` 的 id/name/baseUrl 配置，不经过 AutomationPlatform 或 Vite 的 `/api` 代理。来源需部署并重启含新接口的 ServiceV14；旧后端返回 404 时会明确提示，不自动回退到原日志接口。原 `#operation-logs` 仍读取 AutomationPlatform 的 `/api/event-logs/operational`，两者互相独立。

- 读取的是 ServiceV14 机器上的 `SiemensAG-Automation-TIAPortal/Operational`，不是浏览器机器的日志。日志查询不打开 TIA 工程、不比较或导出，不影响更改历史缓存。
- 默认最新 100 条，可设置 1–5000 的整数，修改条数后点击“刷新当前来源”应用；页面同时显示当前快照实际使用的读取上限。返回时间、用户、计算机、事件内容、事件 ID、级别、提供程序和记录 ID，内容仅以文本显示。
- 支持跨字段关键词及级别筛选，每页 25 条。按来源保存条数、日志、筛选分页及成功读取时间，首次访问自动读取；已成功加载（包括空数组）的来源切回时不自动重复请求，不自动轮询。页面会话缓存随浏览器重载清除。
- 切换来源或离开页面取消请求，过期响应不会覆盖当前数据；刷新失败保留旧快照并明确标记，错误不会伪装成空日志。请求超时为 75 秒，可手动重试。
- 400 表示读取条数无效，403 表示 ServiceV14 运行账户缺少日志读取权限，503 表示来源机器通道不存在或日志不可用。应由管理员按最小权限配置账户，不自动提权。
- 浏览器必须能直接访问来源地址；来源服务需允许页面源的跨域 GET，HTTPS 页面不能直连被浏览器阻止的 HTTP 来源。来源配置公开，不要加入凭据，不要将匿名日志接口暴露到公网。

`npm test` 同时运行历史及 ServiceV14 日志的 API/状态测试，`npm run test:browser` 同时验证两类页面，使用模拟数据，不访问实际日志或 TIA 工程。运行 `npm run build` 验证生产构建。部署前还需在真实来源机器上验证日志权限、通道和网络访问。

## 程序更改历史

历史页面直接请求所选来源的 `${baseUrl}/GenerateChangesHistory`（不是 `/GetChangesHistory`），不经过 AutomationPlatform 的 `/api` 代理。历史 JSON 包含 `Version`、`GeneratedAtUtc`、`Projects`、`Entries`；记录包含 `PreviousProject`、`LatestProject`、`ComparedAtUtc`、`Comparisons`。工程字段为 `Path`、`ModifiedUtc`、`Length`；PLC 比较字段为 `Device` 和 `Result`，节点包含左右名称、比较状态、两侧修改时间及 `Elements`。

版本 2/3 同时包含程序块和 `TechnologicalObjectGroup.TechnologicalObjects` 工艺对象，叶节点的 `ObjectType` 必须为 `Block` 或 `TechnologicalObject`。类型和对象路径共同标识比较对象，同名不同类型独立显示；同一个工艺对象从两个入口读到时只统计一次。前端兼容版本 1（缺失类型视为程序块）和版本 2，但旧版本没有详细比较入口。升级并重启 WindowsServiceV14 后，旧缓存将失效，首次请求重新生成版本 3 的 `ChangesHistory.json`；浏览器中的旧快照需要手动刷新。

版本 3 为修改的普通程序块提供 `ExportId`，导出失败时提供 `ExportError`。点击可操作的“修改”状态后，前端向所选来源的 `/api/block-comparisons` 提交标识，查询 `/api/block-comparisons/{taskId}`，成功后获取 `/api/block-comparisons/{taskId}/report`。XML 不离开后端；ACTCLI 必须安装在 WindowsServiceV14 电脑，不需要客户端启动助手。PDF 以 Blob URL 在页面预览并支持下载，关闭报告或离开页面释放 Blob。后端执行失败可重试；同一导出对的运行任务及有效已完成报告会复用。浏览器最多等待 10 分钟，超时或关闭页面不停止后端任务。

详细配置、可信内网边界、资源限制和 Windows 服务账户验收见 [后端比较部署说明](../docs/block-comparisons.md)。

- 左侧保留时间线排序切换，默认选中最新记录；右侧按 PLC 展示新增、删除、修改、相同对象，每页 25 个，默认只看变化，支持类型筛选和按 PLC/对象名/分组路径搜索。
- 工程文件修改时间决定时间线；实际比较时间、服务端历史生成时间和浏览器成功读取时间分别展示。对象 ModifiedDate 保留接口原始精度和时区标记（Z 表示 UTC），缺失一侧显示“—”。只比较 ModifiedDate，不验证程序代码或工艺参数具体值是否相同，也不比较变量表。
- 首次进入页面自动读取；已成功加载的来源在当前浏览器页面会话中缓存。切回来源或页面不自动重复请求；“刷新当前来源”手动读取，仍由后端决定是否复用 `ChangesHistory.json`。不会自动轮询、重试或强制重建历史。
- 切换来源或离开页面会取消当前浏览器请求，过期响应不会覆盖新选择。取消或超时不保证停止后端历史生成。刷新失败会保留旧快照及上次成功读取时间，并明确显示失败提示。
- 历史请求超时为 5 分钟，可在 `src/api/changesHistory.js` 的 `HISTORY_REQUEST_TIMEOUT_MS` 调整；变量请求仍为 75 秒。来源服务必须允许 CentralAdmin 页面源的跨域 GET；生产 HTTPS 页面不能直接请求被浏览器阻止的 HTTP 来源。

运行 `npm test` 验证历史响应校验、API 错误、超时、来源缓存和请求竞态，运行 `npm run build` 验证生产构建。测试使用模拟响应，不会打开或更改真实 TIA 工程。

安装 Chrome/Edge 后可运行 `npm run test:browser`，验证 Hash 导航、筛选/分页、页面缓存、刷新失败保留旧数据及 390px 移动布局。默认查找 Windows 标准安装路径，也可用环境变量 `CHROME_PATH` 指定浏览器。该测试拦截后端请求并使用样例，不访问真实工程；截图保存到 `node_modules/.cache/history-validation/`，不随源码提交。

## 设备来源配置

在前端 `src/config/tagSources.js` 的 `tagSources` 数组中维护来源：

```js
export const tagSources = [
  {
    id: 'plc-01',
    name: 'PLC 01',
    baseUrl: 'http://172.16.0.100:5736',
  },
]
```

增加设备时追加配置项，设置不同的 `id`、显示名称和服务地址。开发模式下 Vite 热更新会刷新配置；部署前修改配置后重新构建前端。变量页在浏览器中直接请求每个来源的 `${baseUrl}/getTags`，不依赖 AutomationPlatform 的 `TagSources` 配置或 `/api/tag-sources` 接口。

- `id`：稳定来源标识，1 到 64 位字母、数字、下划线或连字符，首位为字母或数字；大小写不敏感且不得重复。不要用接口返回的 `Device` 名称作为来源 ID。
- `name`：非空的来源显示名称。
- `baseUrl`：浏览器可访问的 HTTP/HTTPS 服务地址；前端在其路径后追加 `/getTags`。不能包含用户凭据、查询参数或片段。此配置会随前端代码公开，不要在其中放置秘密。

标签接口必须直接返回 JSON 数组，每个元素包含 `Device`、`Group`、`Table`、`Name`、`DataType`、`Address` 六个字符串字段；空字符串允许，缺失字段、null 或非字符串字段会报告数据格式异常。浏览器跨域请求要求标签服务允许 CentralAdmin 所在源的 CORS GET 请求。

## 本地启动

1. 启动 `AutomationPlatform` 的 `http` 配置，确保 API 监听 `http://localhost:5123`。
2. 在此目录运行 `npm install`、`npm run dev`，打开终端输出的前端地址（默认 `http://localhost:5173`）。Windows PowerShell 若限制 npm.ps1，请使用 `npm.cmd install`、`npm.cmd run dev`。
3. 扫描历史和操作日志通过 Vite 将 `/api` 代理至后端。PLC 变量由浏览器直接请求前端来源配置中的 `/getTags`，因此标签服务需允许开发页面源的 CORS GET 请求。
4. 扫描历史来自 `/api/discovery/scans`，操作日志来自 `/api/event-logs/operational`。没有扫描记录时，请先在 AutomationPlatform 执行扫描，并检查 `Discovery:ScanHistoryDatabasePath`；操作日志要求后端 Windows 主机具有相应日志通道及读取权限。

## 部署

运行 `npm run build`，将 `dist/` 交给静态文件服务器部署，并将同源 `/api` 反向代理至 AutomationPlatform。扫描历史和操作日志使用 `VITE_API_BASE_URL`（API 根地址，不包含 `/api`），可在构建前配置；PLC 变量来源独立维护在 `src/config/tagSources.js`，浏览器直接请求标签服务，因此生产标签服务也需允许部署站点源的 CORS GET 请求。Vite 开发代理不包含在构建产物内。

变量来源配置位于前端项目中并会公开给浏览器用户；不要在来源地址中嵌入凭据。浏览器标签请求超时为 75 秒。

现有读取接口以及新增的比较/报告接口均匿名开放，部署时必须通过防火墙限制可信内网范围；不要对公网开放。比较接口可启动服务器上的固定 ACTCLI 进程，报告不提供用户级隔离。来源地址应只由可信管理员维护。新增比较接口还需允许 CORS POST/OPTIONS 及 Content-Type 请求头；CORS 不是身份验证。
