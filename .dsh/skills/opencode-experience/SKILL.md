---
name: opencode-experience
description: TakeTop DECMP ERP / TakeTop 项目管理系统 / DshWeb(DeepSeek Harness Web) / TakeTopDshWeb 相关的开发、迁移、修复、审计、多语言、安装部署任务。涉及 ASP.NET WebForms 到 Blazor Server 迁移、NHibernate/PostgreSQL、共享组件(DeptTreeNode/TtPager/ShareSvc/initLinks)、14 语言 resx、状态 HomeName 本地化、一键安装包(install-win/install-iis-win/install-mac/install-linux)、IIS/ANCM、DSH Web 集成与凭据格式时，先加载本技能，参考历史经验数据再动手。
whenToUse: 当用户请求涉及 TakeTopDECMP/TakeTopPMPBlazor/TakeTopDECMPWMLPGSolutionBlazorCore/TakeTop.Web.Blazor/DshWeb/TakeTopDshWeb/StartDshWeb.bat/install-*.bat/多语言/i18n/resx/T_*Status HomeName/DeptTreeNode/TtPager/initLinks/hrow/ClosedXML/HasAuthobility 等本仓库或历史代码时；或用户明确要求"参考以前的工作经验/经验数据/历史数据"时；或一段工作完成后需要"归档/保存/收集工作数据/总结经验"时，先调用本技能读取经验目录（含第 0 节归档约定），再继续任务。既有经验文档只读，新增工作数据按第 0 节约定写入本目录。
metadata:
  docsRoot: "E:\\WorkBuddy\\WorkSpace\\adminroot\\sharedata"
---

# OpenCode 工作经验数据（自动参考 + 数据归档）

本技能指向历史工作复盘目录，同时**经验数据目录也是以后工作数据的统一归档位置**。数据存储在管理员工作区的 `sharedata` 目录下，所有用户实例可通过 sessions API 共享经验数据。处理任何与 TakeTop / TakeTopDshWeb / DshWeb 相关的任务前，**先读取经验数据**，可避免重复踩坑（SQL 迁移方言、hrow 表头、多语言 key、DSH 凭据格式等）。

## 0. 数据归档约定（用户 2026-09-03 规定，永久生效）

- **以后的工作数据一律存到管理员工作区的 `sharedata` 目录**（即 `E:\WorkBuddy\WorkSpace\adminroot\sharedata`，不含敏感凭据，如 API Key 一律不入档）。
- 归档物包括：一段工作的复盘总结（按主题拆章，如"安装包修复"、"多语言切换"、"XX 迁移"）、统计数据、会话/消息明细 CSV 或 JSON。
- 组织规则：
  - 主题总结文档编号续排：当前已用到 `08-近期工作数据-2026-09-02至09-03.md`，新文档从 `09-` 起，文件名形如 `NN-<主题>-<日期范围>.md`。
  - 明细数据（CSV/JSON/SQL）放入 `data\` 子目录，文件名带批次标识（如 `sessions-2026-09-04.csv`）。
  - 每完成一批归档，更新 `README.md` 的"文档索引"与批次说明。
- 用户若在别处（用户级 .dsh、临时目录等）生成了工作数据，默认应合并/移动到本目录；源位置清空。
- 本 skill 的 `whenToUse` 中也应把"归档本次工作数据"列为触发场景。

## 1. 经验文档位置

数据存储在管理员工作区：`E:\WorkBuddy\WorkSpace\adminroot\sharedata`

1. 经验目录 = `E:\WorkBuddy\WorkSpace\adminroot\sharedata\`
2. 若该目录不存在，提示用户该目录缺失，不要臆造内容。

## 2. 阅读顺序

1. `README.md` — 总览：两批次数据（批次一 781 会话 2026-06-08→08-31，批次二 3 会话 2026-09-02→09-03）、文档索引、一句话总结。
2. `01-项目背景与代码库地图.md` — 新旧两代系统结构、HBM 权威表结构、模块页面前缀速查、集成子系统。
3. `03-高频问题模式与修复清单.md` — SQL/DB、UI 表头(hrow)、弹层链接、图表报表、i18n、权限、"降级处理"清单（排错最高优先）。
4. `04-共享组件与开发约定.md` — TtPager、DeptTreeNode、ShareService/HasAuthobilityAsync、initLinks、tt-btn-*、*-hrow、ShowMsg、日期初始化。
5. `05-多语言资源体系.md` — 14 语言 resx、W()/LangSvc.GetWord、拼音 key、加 key 标准流程。
6. `06-AI协作工作方法论.md` — explore/general/build 分工与提示词模板。
7. `07-DSH集成与部署运维.md` — DSH Web 结构、settings.yaml 多 Provider、.credentials.yaml 必须 `version: 1 + refs:` 格式、重启生效。
8. `08-近期工作数据-2026-09-02至09-03.md` — 最近一次：安装包修复、多语言切换、DshWeb 独立应用迁移到 TakeTopDshWeb。
9. 需要会话级明细时：`附录A-完整会话清单.md` 及 `data\*.csv`、`data\*.json`。

## 3. 核心纪律（从经验中提炼，务必遵守）

- 表结构以 `.hbm.xml` 为权威；Npgsql 计算列必须 `AS 别名`；`substring` 用 1 基；`to_char` 勿用于 text 列。
- 状态/字典显示 JOIN `T_*Status` 取 `HomeName`，不要直接显示原始 Status 或硬编码 switch。
- 新增用户可见文本必须走 W()/LangSvc，并同步**全部**语言文件（漏加是高频失误）。
- 表头 `<tr>` 带 `*-hrow` 类；居中需 `!important`；新页面优先复用共享组件（DeptTreeNode/TtPager），勿手写内联实现。
- 权限校验用 `HasAuthobilityAsync(pageName)`；保存提示用 ShowMsg（3 秒自动隐藏），勿用 alert()。
- DSH 凭据写 `.credentials.yaml` 用 `version:1 + refs:`（扁平旧格式会被拒）；改凭据后重启 dsh server。
- 迁移/对比任务用"旧版 .aspx(.cs) vs 新版 .razor 逐方法比对"，只列差异不修改，先 explore 后 general。

## 4. 边界

- **既有经验文档（01~08、附录A、README 正文）为只读参考**：不要随意改写已有总结；只在归档新一批工作数据时按第 0 节约定新增文档/数据并更新 README 索引。
- 文档不含真实 API Key；凭据问题一律引导到 `.credentials.yaml` refs 格式；任何包含真实凭据的数据不得写入本目录。
