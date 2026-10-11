# CentralAdmin

Vue 3 + Vite 工业监控看板，读取 AutomationPlatform 提供的扫描历史与 TIA Portal 操作日志 GET API。当前 API 仅能读取历史，不能从本界面发起设备扫描。

## 本地启动

1. 启动 `AutomationPlatform` 的 `http` 配置，确保 API 监听 `http://localhost:5123`。
2. 在此目录运行 `npm install`、`npm run dev`，打开终端输出的前端地址（默认 `http://localhost:5173`）。Windows PowerShell 若限制 npm.ps1，请使用 `npm.cmd install`、`npm.cmd run dev`。
3. 页面通过 Vite 将 `/api` 请求代理至后端，无需浏览器跨域配置。扫描历史来自 `/api/discovery/scans`；最新 100 条 TIA Portal 操作日志来自 `/api/event-logs/operational`。如果没有扫描记录，请先在 AutomationPlatform 中执行设备扫描，并检查其 `Discovery:ScanHistoryDatabasePath` 配置。操作日志要求后端所在的 Windows 主机具有相应日志通道和读取权限。

## 部署

构建命令：`npm run build`，将 `dist/` 交给静态文件服务器部署。默认请求同源 `/api` 下的扫描历史和操作日志接口，请通过反向代理把 `/api` 转发到 AutomationPlatform；如果前后端分开部署，在构建前设置 `VITE_API_BASE_URL` 为可由浏览器访问的 API 根地址（不包含 `/api`）。`.env.example` 给出了示例。此变量在构建时写入前端，需要重新构建才能生效。

扫描历史 API 目前匿名开放，部署时请限制可访问的网络范围。
