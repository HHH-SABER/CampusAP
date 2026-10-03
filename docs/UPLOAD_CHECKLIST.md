# GitHub 上传规制（提交前必读）

本项目的所有改动在推送到 GitHub 之前，必须通过以下检查。执行者包括人与 AI 代理。

## 一、身份规制

1. 提交身份使用 GitHub 用户名 + noreply 邮箱，仓库级配置：
   ```bash
   git config user.name  "<你的用户名>"
   git config user.email "<你的用户名>@users.noreply.github.com"
   ```
   禁止把真实姓名、常用个人邮箱写进提交配置或任何文件。
2. **用户名/身份只允许出现在提交元数据**，不得出现在代码、文档、示例里（预提交扫描会拦截）。

## 二、敏感信息红线

1. **凭据类信息（密码/令牌/API key/数据库连接串）一律不进仓库**——运行时从环境变量或 `%APPDATA%\CampusAP\` 配置读取；文档示例一律用 `<占位符>`。
2. 校园网 Portal 地址、学校名称等可定位到具体院校的信息，文档里用 `example.edu` / 占位 IP；真实配置只存本机 `%APPDATA%`。
3. 本机绝对路径（`C:\Users\<用户名>\`、`E:\程序文件\` 等）不得出现在任何文件，统一用相对路径。
4. 手机号、个人邮箱、聊天账号、身份证号：出现即拦截。
5. 已进仓库的凭据视同**已泄露**：先作废/改密，再清理历史（force push 不覆盖已克隆的副本）。

## 三、文件准入

1. 只保留：源码、解决方案/工程文件、`README`、`docs/`、应用图标。
2. 不入库：构建产物（`bin/`、`obj/`、`发布/`）、日志、临时文件、个人笔记、插件状态（`.mimosa/`）、安装包。
3. 二进制文件走白名单制：当前仅允许 `src/CampusAP.App/Assets/app.ico`；新增二进制前先确认无嵌入信息并在 PR 说明里写明用途。
4. 分发用 GitHub Releases 附件，不把大文件提交进仓库。

## 四、自动扫描

1. 本仓库启用了预提交钩子（`.githooks/pre-commit` → `sensitive_scan.py`），已通过
   `git config core.hooksPath .githooks` 激活。需要 python 在 PATH。
2. 每次提交自动扫描：手机号、本机绝对路径、访问令牌前缀（`ghp_`/`AKIA`/`sk-` 等）、个人邮箱、硬编码凭据、院校/用户名特征。
3. 被拦截时**优先改写内容**，而不是 `--no-verify` 绕过；确需绕过在提交信息里说明原因并自负责任。
4. 手动全量自检（提交前建议跑一次，示例：
   ```bash
   git grep -nI -E "1[3-9][0-9]{9}|ghp_|AKIA" -- .
   ```
   另外人工检查是否出现本机盘符路径、个人邮箱、学校信息）。

## 五、推送前人工核对

1. `git diff --cached` 逐文件过目，不使用 `git add -A` 盲提（至少要 `git status` 确认无意外文件）。
2. 确认 `.gitignore` 覆盖了新增的生成物目录。
3. 首次建仓：创建后立即打开 GitHub 网页复查一遍文件列表。

## 六、历史污染处理

1. 本地误提交后**尚未推送**：`git commit --amend` 或 reset 重做，随后 `git reflog expire --expire=now --all && git gc --prune=now` 清理悬空对象。
2. 已推送：作废凭据 → 修正文件 → force push 覆盖历史（记录下泄漏时间点，评估是否需要通知）。
