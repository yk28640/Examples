# ServiceV14 托盘程序

## 使用
双击 ServiceV14.exe 启动 HTTP 后端，无主窗口或控制台，右下角通知区域显示图标（可能在隐藏图标中）。Debug 和 Release 行为一致。
双击图标查看状态；右键打开 API 文档、配置目录或退出。退出释放监听并取消比较任务。同一登录会话重复启动会提示已运行。用户注销后不继续运行，未配置开机自启或自动提权。

## 构建与部署
构建 ServiceV14 的 Release 配置，复制 ServiceV14/bin/Release 完整输出；无需注册 Windows 服务。保留 OWIN、Web API、Newtonsoft.Json、Swashbuckle.Core 和 Siemens.Engineering 等依赖，不要只复制主 EXE。ServiceV14 不引用 WindowsServiceV14 项目或程序集，部署不需要 WindowsServiceV14.exe。切换旧部署时使用干净目录，避免混入旧的后端程序集。
检查输出目录的 tagsconfig.json：listenIp、projectPath、comparasionFolder、blockComparison.ExecutablePath。运行时从 ServiceV14.exe 所在目录读取配置；更新时保留现场数据和配置。
核心源码位于本项目 Core 目录，是从原服务复制的实体文件；tagsconfig.json 也是本地实体副本，不链接原服务配置。后续核心修复和配置调整需要分别维护两个项目。
Swagger 开关位于 ServiceV14.exe.config 的 appSettings/SwaggerEnabled。开启时访问 http://配置IP:5736/swagger；关闭或无效时菜单禁用。修改配置后退出再启动。
目标机器需要 .NET Framework 4.6.2 或兼容版本、TIA Portal V14 SP1 / Openness 环境；PDF 功能需要 ACTCLI。

## 权限与冲突
普通用户仍需要 HTTP.sys URL ACL。管理员一次性给实际运行账户授权对应 URL；已有授权先核对，不要删除其他程序配置或授权 Everyone。
查看：netsh http show urlacl url=http://192.168.5.166:5736/
授权：netsh http add urlacl url=http://192.168.5.166:5736/ user="计算机名\运行账户"
更换 IP 后需对应新授权，IP 必须属于本机。外部访问需按安全策略配置防火墙。运行账户需要 TIA Openness 用户组及访问授权、工程和比较目录权限，并能运行 ACTCLI。不要将未认证 API 暴露到互联网。
原 WindowsServiceV14 已恢复为独立的 Windows 服务实现，ServiceV14 保存自己的核心代码副本。两个程序不能同时监听相同 URL，使用托盘前停止原服务。单实例锁只约束当前会话，两个进程不共享内存缓存。
历史及 PDF 缓存规则沿用原实现，托盘启动与文档加载不主动打开工程、比较或导出。

## 验证
验证 /test、/swagger/docs/v1、托盘菜单、重复启动提示和退出后重新启动。工程读取、比较和 PDF 需在实际部署环境另行验证。
