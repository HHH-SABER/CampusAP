#!/usr/bin/env python
"""预提交敏感信息扫描。

用法：由 .githooks/pre-commit 自动调用（文件列表经 stdin 以 \\0 分隔传入），
也可手动对任意文件运行：python .githooks/sensitive_scan.py 文件1 文件2

发现疑似敏感内容时以非零码退出并逐条列出位置。
匹配规则宁枉勿纵：被误报的内容请改写表述（例如示例中用 <你的用户名> 占位符），
而不是依赖 --no-verify 绕过。
"""
import os
import re
import sys

# (正则, 标签, 处置建议)
PATTERNS = [
    (r"1[3-9]\d{9}", "疑似手机号", "删除或替换为占位符"),
    (r"[A-Za-z]?:?\\\\(?:Users|Windows|程序文件)\\\\[^\s'\"]+|/e/(?:程序文件|Users)[^\s'\"]*",
     "本机绝对路径", "改用相对路径或仓库内路径"),
    (r"18266", "本机用户名", "删除"),
    (r"HHH-SABER", "GitHub 用户名出现在文件内容中", "删除（身份只应出现在提交元数据）"),
    (r"10\.130\.\d+\.\d+", "校园网内网 IP", "改用 example.com 或占位符"),
    (r"ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}"
     r"|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{30,}",
     "疑似访问令牌/API 密钥", "立即作废该凭据（已视同泄露），改从环境变量读取"),
    (r"[A-Za-z0-9._%+-]+@(?:qq|163|126|gmail|outlook|hotmail|foxmail)\.(?:com|net)",
     "疑似个人邮箱", "改用 <user>@users.noreply.github.com 或占位符"),
    (r"(?i)(password|passwd|pwd|secret|token|api[_-]?key)\s*[:=]\s*['\"][^'\"{}\s]{6,}['\"]",
     "疑似硬编码凭据", "改为从环境变量或 %APPDATA% 配置读取，示例用 <占位符>"),
]

SKIP_SUFFIXES = {".ico", ".png", ".jpg", ".jpeg", ".gif", ".dll", ".exe", ".pdb"}


def scan(path: str) -> list[str]:
    # 自排除：扫描器的规则文本本身不含敏感信息
    if os.path.abspath(path) == os.path.abspath(__file__):
        return []
    if path.endswith(tuple(SKIP_SUFFIXES)):
        return []
    try:
        with open(path, encoding="utf-8", errors="ignore") as fh:
            lines = fh.read().splitlines()
    except OSError:
        return []

    findings = []
    for idx, line in enumerate(lines, start=1):
        for pattern, label, advice in PATTERNS:
            if re.search(pattern, line):
                findings.append(f"  第{idx}行 [{label}] {advice}\n    {line.strip()[:100]}")
    return findings


def main() -> int:
    if len(sys.argv) > 1 and sys.argv[1] != "-":
        names = sys.argv[1:]
    else:
        data = sys.stdin.read()
        names = [n for n in data.split("\0") if n]

    report: list[str] = []
    for name in names:
        for finding in scan(name):
            report.append(f"{name}\n{finding}")

    if report:
        print("pre-commit 敏感信息扫描：发现疑似内容，提交已拦截\n")
        print("\n".join(report))
        print("\n处理：修正后重新 add；确认为误报请改写表述而非绕过。")
        print("（确有特殊情况可用 git commit --no-verify 跳过，责任自负）")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
