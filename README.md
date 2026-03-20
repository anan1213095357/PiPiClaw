# <div align="left" style="display: flex; align-items: center; line-height: 1.2;"> <img src="./141221163.png" width="32" height="32" style="margin-right: 15px;"> <span style="font-size: 32px; font-weight: bold;">PiPiClaw - 皮皮虾智能运维自动化 Agent</span> </div>

<div align="center">

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-blue?style=flat-square)
![Arch](https://img.shields.io/badge/Arch-x86%20%7C%20x64%20%7C%20ARM32%20%7C%20ARM64-blue?style=flat-square)
![License](https://img.shields.io/badge/License-MIT-green?style=flat-square)
![Version](https://img.shields.io/badge/Version-v1.0.0-orange?style=flat-square)

**🦐 让 AI 成为你的运维指挥官 | 命令行里的智能自动化专家**

**🌐 语言切换 | Language:** [中文](README.md) | [English](README_EN.md)

</div>

---

## 📖 项目简介

**PiPiClaw (皮皮虾)** 是一款基于阿里云通义千问大语言模型的智能运维自动化命令行工具。它通过自然语言交互，帮助你自动执行系统命令、管理文件、分析日志、规划定时任务，让繁琐的运维工作变得简单高效。

只需像吩咐人类一样输入自然语言指令，PiPiClaw 就能理解你的意图，自动调用合适的工具完成任务 —— 就像有一个 24 小时在线的运维专家随时待命。

---

## ✨ 核心功能

| 功能 | 描述 |
|------|------|
| 🔧 **命令执行** | 跨平台终端命令执行 (ls, git, npm, docker, systemctl 等) |
| 📄 **文件读取** | 智能读取文本、代码、配置文件内容，自动检测编码 (UTF-8/GBK) |
| ✍️ **文件写入** | 自动生成代码、配置文件并写入本地，支持局部修改 |
| 🖼️ **图片分析** | 支持本地截图、照片的识别与分析 (Base64 编码) |
| 🔍 **内容搜索** | 在指定目录下搜索包含特定关键字的文件 (函数名、变量名、类名等) |
| ⏰ **定时任务** | 支持一次性/周期性任务调度，持久化存储，自动执行 |
| 🧩 **技能扩展** | 支持从 Skill-Hub 搜索和安装扩展技能，无限拓展能力 |
| 🌐 **Web 控制台** | 内置轻量级 Web UI (端口 5050)，支持浏览器远程操控 |
| 🧠 **记忆管理** | 多轮对话上下文记忆，任务完成后自动清理节省 Token |
| 🔐 **权限处理** | 智能 sudo 提权拦截，自动处理密码输入 (非 Windows) |

---

## 🚀 快速开始

### 前置要求

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) 或更高版本
- 阿里云 DashScope API Key ([获取地址](https://bailian.console.aliyun.com/cn-beijing?tab=model#/api-key))

### 安装与运行

```bash
# 克隆项目
git clone https://github.com/anan1213095357/PiPiClaw.git
cd PiPiClaw

# 还原依赖
dotnet restore

# 运行项目
dotnet run
```

首次运行时会自动生成 `appsettings.json` 配置文件，按提示输入 API Key 即可。

### 发布为独立可执行文件

```bash
# 发布 AOT 编译版本（单文件、高性能）
dotnet publish -c Release -r win-x64 --self-contained true
dotnet publish -c Release -r osx-x64 --self-contained true
dotnet publish -c Release -r linux-x64 --self-contained true
```

---

## ⚙️ 配置说明

### 配置文件 (appsettings.json)

```json
{
  "ApiKey": "sk-your-actual-api-key-here",
  "Model": "qwen3.5-plus",
  "Endpoint": "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
  "SudoPassword": ""
}
```

| 配置项 | 说明 |
|--------|------|
| `ApiKey` | 阿里云 DashScope API 密钥 |
| `Model` | 使用的 AI 模型 (默认 qwen3.5-plus) |
| `Endpoint` | API 端点地址 |
| `SudoPassword` | (可选) Linux/macOS 自动提权密码 |

### 环境变量方式

```bash
# macOS / Linux
export QWENCLI_API_KEY="sk-your-api-key-here"
export QWENCLI_MODEL="qwen3.5-plus"
export QWENCLI_ENDPOINT="https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"

# Windows PowerShell
$env:QWENCLI_API_KEY="sk-your-api-key-here"
```

---

## 💡 使用示例

启动 PiPiClaw 后，你可以输入各种自然语言指令：

### 基础操作
```
> 帮我扫描一下当前目录，看有没有 C# 相关的源码文件

> 读取 package.json 的内容并分析依赖

> 执行 git status 查看当前仓库状态

> 帮我查一下系统当前的内存占用情况，并把结果写进 memory_log.txt
```

### 文件操作
```
> 创建一个名为 config.json 的配置文件，包含数据库连接信息

> 把 main.cs 里的所有 Console.WriteLine 替换成 Debug.WriteLine
```

### 定时任务
```
> 每天下午 3 点，帮我屏幕截图看一下我在干什么

> 每隔 30 分钟检查一次 CPU 使用率，超过 80% 就记录到日志
```

### 技能扩展
```
> 帮我搜索 weather 相关的技能

> 安装 calendar 技能
```

---

## 🏗️ 项目结构

```
PiPiClaw/
├── Program.cs                # 主程序入口 (全部逻辑)
├── PiPiClaw.csproj           # 项目配置文件
├── README.md                 # 项目说明文档 (中文)
├── README_EN.md              # 项目说明文档 (英文)
├── .gitignore                # Git 忽略规则
├── appsettings.json          # 配置文件（本地使用，不提交）
├── appsettings.example.json  # 配置模板（可提交）
├── Properties/               # 项目属性配置
├── bin/                      # 编译输出目录
├── obj/                      # 临时构建文件
├── logs/                     # 日志目录
├── skills/                   # 扩展技能目录 (自动创建)
├── pi_history.json           # 对话记忆存档
└── pi_scheduled_tasks.json   # 定时任务存档
```

---

## 🔬 技术栈

| 组件 | 技术选型 |
|------|----------|
| **运行时** | .NET 10.0 |
| **编译优化** | AOT (Ahead-of-Time) 编译 |
| **AI 模型** | 阿里云通义千问 (qwen3.5-plus) |
| **HTTP 客户端** | System.Net.Http |
| **JSON 处理** | System.Text.Json |
| **编码支持** | UTF-8 / GBK 自动检测 |

### 项目特性

- ✅ **AOT 编译** - 更小的体积、更快的启动速度
- ✅ **跨平台** - 支持 Windows、Linux、macOS
- ✅ **流式对话** - 支持多轮上下文对话
- ✅ **工具调用** - 10+ 内置工具智能调度
- ✅ **定时调度** - 持久化任务队列，自动循环执行
- ✅ **Web UI** - 内置 HTTP 服务器，浏览器远程操控
- ✅ **技能系统** - 支持在线搜索和安装扩展技能
- ✅ **错误处理** - 完善的异常捕获与提示
- ✅ **安全配置** - API Key 与代码分离，支持环境变量
- ✅ **日志压缩** - 自动折叠相似日志行，节省 Token

---

## 🌐 Web 控制台

PiPiClaw 内置轻量级 Web UI，启动后自动监听 `http://localhost:5050`

**功能特性：**
- 📱 响应式设计，支持手机/平板访问
- 🎨 赛博朋克风格界面
- 📡 实时流式输出工具调用过程
- ⏰ 定时任务可视化查看
- 🔧 在线配置 API Key 和模型
- 📷 左下角二维码，手机扫码即连

---

## 📝 注意事项

1. **API Key 安全**: `appsettings.json` 已加入 `.gitignore`，请勿手动提交包含真实 Key 的文件
2. **命令执行权限**: 工具执行的命令具有与当前用户相同的权限，请谨慎操作
3. **网络依赖**: 需要稳定的网络连接以调用 AI 服务
4. **Token 限制**: 大文件读取会自动截断，防止 Token 溢出
5. **文件修改**: 修改现有文件时，请确保提供精确的 old_content 进行替换
6. **Windows 提权**: Windows 环境下无法自动处理 sudo，请以管理员身份运行程序

---

## 🤝 贡献指南

欢迎提交 Issue 和 Pull Request！

1. Fork 本项目
2. 创建特性分支 (`git checkout -b feature/AmazingFeature`)
3. 提交更改 (`git commit -m 'Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 开启 Pull Request

---

## 📄 许可证

本项目采用 [MIT 许可证](LICENSE) 开源。

---

<div align="center">

**🦐 Made with ❤️ by 奶茶叔叔**

如果这个项目对你有帮助，请给一个 ⭐ Star 支持一下！

</div>
