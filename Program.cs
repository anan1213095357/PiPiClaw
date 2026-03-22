using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Net;            
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using static System.Int32;


// 设置当前目录为程序运行目录
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// 强制 Windows 控制台使用 UTF-8 编码 (替代庞大的 GBK Provider)
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    try
    {
        using var pChcp = Process.Start(new ProcessStartInfo("cmd.exe", "/c chcp 65001") { CreateNoWindow = true });
        pChcp?.WaitForExit();
    }
    catch { }
}
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// 获取配置文件的函数
string GetConfig(string key, string def = "")
{
    var envValue = Environment.GetEnvironmentVariable(key);
    if (envValue != null) return envValue;

    if (!File.Exists("appsettings.json")) return def;
    try
    {
        var json = File.ReadAllText("appsettings.json", Encoding.UTF8);
        var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
        if (cfg != null)
        {
            if (key == "ApiKey" && !string.IsNullOrEmpty(cfg.ApiKey)) return cfg.ApiKey;
            if (key == "Model" && !string.IsNullOrEmpty(cfg.Model)) return cfg.Model;
            if (key == "Endpoint" && !string.IsNullOrEmpty(cfg.Endpoint)) return cfg.Endpoint;
            if (key == "SudoPassword" && cfg.SudoPassword != null) return cfg.SudoPassword;
            if (key == "WebPort") return cfg.WebPort > 0 ? cfg.WebPort.ToString() : def;
        }
    }
    catch { /* 忽略解析错误 */ }
    return def;
}

// ========================== 1. 基础配置与初始化 ==========================
if (!File.Exists("appsettings.json"))
{
    var defaultConfig = new AppConfig
    {
        ApiKey = "your_api_key_here",
        Model = "qwen3.5-plus",
        Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
        SudoPassword = "",
        WebPort = 5050
    };
    File.WriteAllText("appsettings.json", JsonSerializer.Serialize(defaultConfig, AppJsonContext.Default.AppConfig), Encoding.UTF8);
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("检测到首次运行，已自动生成默认的 appsettings.json 文件。");
    Console.ResetColor();
}

// ========================== 2. 初始化 Tools (大模型工具箱) ==========================
var toolsDoc = JsonDocument.Parse("""
[
    { "type": "function", "function": { "name": "execute_command", "description": "执行终端命令", "parameters": { "type": "object", "properties": { "command": { "type": "string" }, "is_background": { "type": "boolean", "description": "【注意，生死攸关的判断】请严格按以下规则选择：\n1. 必须设为 true (后台)：适用于【永远不会自动退出】或【启动常驻服务/UI】或【启动浏览器自动化】等等的命令。例如：启动 Web 服务器、数据库守护进程、打开浏览器及UI自动化(如 agent-browser/chrome)、死循环脚本。这类任务必须丢入后台，否则你会把自己永久卡死！\n2. 必须设为 false (前台)：适用于【执行完会自动结束】并且你需要查看最终输出结果的命令。例如：环境部署(npm install, pip install)、编译构建、下载文件、查日志、执行普通算法脚本。即使这些任务非常耗时，只要它们最终会结束，就必须设为 false 以便拿到完整的执行日志。" } }, "required": ["command", "is_background"] } } },
    { "type": "function", "function": { "name": "read_file", "description": "读文件", "parameters": { "type": "object", "properties": { "file_path": { "type": "string" } }, "required": ["file_path"] } } },
    { "type": "function", "function": { "name": "write_file", "description": "写文件或局部修改文件。局部修改必须提供 old_content。", "parameters": { "type": "object", "properties": { "file_path": { "type": "string" }, "content": { "type": "string" }, "old_content": { "type": "string" } }, "required": ["file_path", "content"] } } },
    { "type": "function", "function": { "name": "read_local_image", "description": "看图（读取本地图片为 base64）", "parameters": { "type": "object", "properties": { "file_path": { "type": "string" } }, "required": ["file_path"] } } },
    { "type": "function", "function": { "name": "search_content", "description": "全局搜索关键字。", "parameters": { "type": "object", "properties": { "keyword": { "type": "string" }, "directory": { "type": "string" }, "file_pattern": { "type": "string" } }, "required": ["keyword"] } } },
    { "type": "function", "function": { "name": "finish_task", "description": "当用户的最终目标已彻底完成时调用此工具。这会预约清空当前的上下文记忆，确保下一次接收新任务时处于干净的状态。", "parameters": { "type": "object", "properties": {} } } },
    { "type": "function", "function": { "name": "add_scheduled_task", "description": "添加定时或延时任务。系统底层的C#引擎会绝对接管时间调度，绝不能在任务执行时由AI去动态补加下一次任务。", "parameters": { "type": "object", "properties": { "execute_at": { "type": "string", "description": "首次执行时间，严格遵循 ISO 8601 格式，例如 '2026-03-20T14:30:00+08:00'" }, "user_intent": { "type": "string", "description": "到达时间时，大模型需要执行的具体任务要求和背景" }, "interval_minutes": { "type": "integer", "description": "可选。如果是周期性任务，请设置此周期间隔（分钟数）。例如每天执行则设为 1440。如果不填或为 0，则仅执行一次。系统会在底层自动无限循环，无需AI干预。" } }, "required": ["execute_at", "user_intent"] } } },
    { "type": "function", "function": { "name": "remove_scheduled_task", "description": "删除指定的定时或延时任务。", "parameters": { "type": "object", "properties": { "task_id": { "type": "string", "description": "要删除的任务ID（从任务列表中获取）" } }, "required": ["task_id"] } } },
    { "type": "function", "function": { "name": "search_skill", "description": "当用户要求安装、查找或添加某个特定技能时执行此功能。根据关键词从 Skill-hub 搜索技能。注意：当用户说要“自我构建”或“编写”技能时，不要执行此功能。", "parameters": { "type": "object", "properties": { "query": { "type": "string", "description": "用户想要搜索或安装的技能关键词，例如 'calendar', 'weather' 等" } }, "required": ["query"] } } },
    { "type": "function", "function": { "name": "install_skill", "description": "安装 单个 Skill-hub 或者 从第三方的技能，并根据包含的 MD 文件自动了解对接方式。", "parameters": { "type": "object", "properties": { "slug": { "type": "string", "description": "技能列表中的slug字段只需传入这个字段即可" } }, "required": ["slug"] } } },
    { "type": "function", "function": { "name": "self_update", "description": "当用户要求皮皮虾自我更新、自动更新或升级自身时调用此工具。将从 GitHub 下载最新版本并自动重启。", "parameters": { "type": "object", "properties": {} } } }
]
""");

// ========================== 3. 读取并检查 ApiKey ==========================
var apiKey = GetConfig("ApiKey");
if (string.IsNullOrEmpty(apiKey) || apiKey.Contains("your"))
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("\n检测到未配置 ApiKey。请输入真实的 ApiKey (直接回车退出程序)");
    Console.WriteLine("获取 ApiKey 地址：https://bailian.console.aliyun.com/cn-beijing?tab=model#/api-key");
    Console.Write("ApiKey: ");
    Console.ResetColor();
    apiKey = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(apiKey)) return; 

    try
    {
        var cfgStr = File.ReadAllText("appsettings.json", Encoding.UTF8);
        var cfg = JsonSerializer.Deserialize(cfgStr, AppJsonContext.Default.AppConfig) ?? new AppConfig();
        cfg.ApiKey = apiKey;
        File.WriteAllText("appsettings.json", JsonSerializer.Serialize(cfg, AppJsonContext.Default.AppConfig), Encoding.UTF8);
        
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✅ ApiKey 已自动保存到 appsettings.json，以后无需重复输入！\n");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[保存失败] 无法写入配置文件: {ex.Message}");
        Console.ResetColor();
        return;
    }
}

Console.Clear();
// ========================== Logo & 简介 ==========================
Console.ForegroundColor = ConsoleColor.Magenta; 
Console.WriteLine(@"
 ____   _  ____   _  ______  _               
|  _ \ (_)|  _ \ (_)/  ____|| |              
| |_) | _ | |_) | _ | |     | |   __ _ __      __
|  __/ | ||  __/ | || |     | |  / _  |\ \ /\ / /
| |    | || |    | || |____ | | | (_| | \ V  V / 
|_|    |_||_|    |_| \______|____\__,_|  \_/\_/  
");
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"皮皮虾已就绪。当前模型：[ {GetConfig("Model", "qwen3.5-plus")} ]");
Console.WriteLine("PiPiClaw | 跨平台全能智能体 · Skill-Hub 10000+ 技能即刻可用\n");

Console.ResetColor();
Console.WriteLine("【简介与食用指南】");
Console.WriteLine("这是一个能够全自动执行终端命令、读写文件、规划任务的 AI 自动化终端。\n只要像吩咐人类一样说话，它就会自己写脚本、查日志、执行系统命令来帮你办事。");
Console.WriteLine("\n💡 试试直接粘贴以下命令 (傻瓜式案例)：");
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine(" 1. \"帮我扫描一下当前目录，看有没有 C# 相关的源码文件\"");
Console.WriteLine(" 2. \"用 C# 写一个能控制树莓派 GPIO 针脚电平的简单脚本，并帮我运行它测试一下\"");
Console.WriteLine(" 3. \"帮我查一下系统当前的内存占用情况，并把结果写进 memory_log.txt\"");
Console.WriteLine(" 4. \"每天下午3点，帮我屏幕截图看一下我在干什么？\"");
Console.ResetColor();
Console.WriteLine("---------------------------------------------------------------------------\n");

// ========================== 4. Sudo 权限处理 ==========================
string sudoPassword = GetConfig("SudoPassword", "");
bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
string sudoInstruction = isWin 
    ? "如果遇到权限拒绝，请使用 PowerShell 的 PSCredential 结合 Start-Process -Verb RunAs 尝试执行，或者直接提示用户关闭控制台，以【管理员身份运行】重新打开本程序。千万不要使用 sudo 命令。" 
    : "如果遇到提权(Permission denied)，请直接使用 sudo 命令。系统会在底层按需拦截并向用户索要密码，你无需关心密码输入环节。";

// ========================== 5. 核心状态变量与网络请求初始化 ==========================
string checkpointPath = "pi_history.json"; 
string tasksPath = "pi_scheduled_tasks.json"; 
List<ChatMessage> fullHistory = new();

lock (tasksPath)
{
    if (!File.Exists(tasksPath)) File.WriteAllText(tasksPath, "[]", Encoding.UTF8);
}

// 尝试恢复上次任务（自动继续，不再询问）
if (File.Exists(checkpointPath))
{
    try
    {
        var savedState = JsonSerializer.Deserialize(File.ReadAllText(checkpointPath, Encoding.UTF8), AppJsonContext.Default.ListChatMessage);
        if (savedState != null) fullHistory = savedState;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[PiPiClaw] 发现上次未完成的任务存档，已自动恢复！\n");
        Console.ResetColor();
    }
    catch { File.Delete(checkpointPath); }
}

using var client = new HttpClient();
client.Timeout = TimeSpan.FromMinutes(10); 
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

var agentLock = new SemaphoreSlim(1, 1);
CancellationTokenSource? currentTaskCts = null;
const string CancelledMsg = "\n[任务已取消]";
bool selfUpdateRequested = false;

// ========================== 6. 启动后台服务 (调度 + WebUI) ==========================
_ = Task.Run(ScheduleLoop);
_ = Task.Run(StartWebManager);

// ========================== 7. 定时任务管理逻辑 ==========================
string AddScheduledTask(string? execAtStr, string? intent, int intervalMinutes = 0)
{
    if (!DateTimeOffset.TryParse(execAtStr, out var execTime))
        return "[添加失败] 时间格式解析错误，请使用 ISO 8601 格式，如 2026-03-20T15:30:00+08:00";
    if (execTime < DateTimeOffset.Now && intervalMinutes == 0)
        return $"[添加失败] 设定时间 {execTime:yyyy-MM-dd HH:mm:ss} 已过去，且不是周期任务。";

    lock (tasksPath)
    {
        var tasks = new List<TaskItem>();
        if (File.Exists(tasksPath)) {
            try { tasks = JsonSerializer.Deserialize(File.ReadAllText(tasksPath, Encoding.UTF8), AppJsonContext.Default.ListTaskItem) ?? new(); } catch {}
        }
        var newTask = new TaskItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ExecuteAt = execTime.ToString("o"),
            UserIntent = intent ?? "未提供具体意图",
            Status = "pending",
            IntervalMinutes = intervalMinutes
        };
        tasks.Add(newTask);
        File.WriteAllText(tasksPath, JsonSerializer.Serialize(tasks, AppJsonContext.Default.ListTaskItem), Encoding.UTF8);
    }
    Console.ForegroundColor = ConsoleColor.Green;
    string loopStr = intervalMinutes > 0 ? $" [周期: 每 {intervalMinutes} 分钟执行]" : " [单次执行]";
    Console.WriteLine($"[调度中心] 成功创建任务: {execTime:yyyy-MM-dd HH:mm:ss} -> {intent}{loopStr}");
    Console.ResetColor();
    return $"[定时任务已添加] PiPiClaw 已将任务持久化，将在 {execTime:yyyy-MM-dd HH:mm:ss} 触发执行。{loopStr} 用户的需求是：{intent}。系统会在底层调度，请不要再次重复调用添加任务。";
}

string RemoveScheduledTask(string? taskId)
{
    if (string.IsNullOrEmpty(taskId)) return "[删除失败] 必须提供 task_id";
    lock (tasksPath)
    {
        if (!File.Exists(tasksPath)) return "[删除失败] 任务文件不存在";
        var tasks = new List<TaskItem>();
        try { tasks = JsonSerializer.Deserialize(File.ReadAllText(tasksPath, Encoding.UTF8), AppJsonContext.Default.ListTaskItem) ?? new(); } catch {}

        var targetNode = tasks.FirstOrDefault(t => t.Id == taskId);
        if (targetNode != null)
        {
            tasks.Remove(targetNode);
            File.WriteAllText(tasksPath, JsonSerializer.Serialize(tasks, AppJsonContext.Default.ListTaskItem), Encoding.UTF8);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n[调度中心] 成功移除任务 (ID: {taskId})");
            Console.ResetColor();
            return $"[任务已删除] 成功移除了 ID 为 {taskId} 的任务。";
        }
        return $"[删除失败] 未在挂起队列中找到 ID 为 {taskId} 的任务。";
    }
}

// ========================== 8. 挂起任务顶层展示区 ==========================
void ShowPendingTasks()
{
    if (!File.Exists(tasksPath)) return;
    try
    {
        var tasks = new List<TaskItem>();
        lock (tasksPath)
        {
            var tasksStr = File.ReadAllText(tasksPath, Encoding.UTF8);
            if (!string.IsNullOrWhiteSpace(tasksStr)) tasks = JsonSerializer.Deserialize(tasksStr, AppJsonContext.Default.ListTaskItem) ?? new();
        }

        var pendingTasks = tasks.Where(t => t.Status == "pending").ToList();

        if (pendingTasks.Count > 0)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("".PadLeft(20, '-')+"[ PiPiClaw 挂起任务 ] "+"".PadRight(20, '-')+"\n");
            
            foreach (var t in pendingTasks)
            {
                if (!DateTimeOffset.TryParse(t.ExecuteAt, out var execTime)) continue;
                var intent = string.IsNullOrEmpty(t.UserIntent) ? "未知" : t.UserIntent;
                var diff = execTime - DateTimeOffset.Now;

                int intervalMinutes = t.IntervalMinutes;
                string loopDisplay = intervalMinutes > 0 ? $" [周期:每{intervalMinutes}分]" : "";

                bool isDelayed = diff.TotalHours < 2;
                string taskType = isDelayed ? "延时任务" : "定时任务";

                string timeDisplay;
                if (diff.TotalSeconds > 0)
                {
                    if (diff.Days > 0) timeDisplay = $"倒计时 {diff.Days}天{diff.Hours}小时{diff.Minutes}分";
                    else if (diff.Hours > 0) timeDisplay = $"倒计时 {diff.Hours}小时{diff.Minutes}分{diff.Seconds}秒";
                    else timeDisplay = $"倒计时 {diff.Minutes}分{diff.Seconds}秒";
                }
                else timeDisplay = "即将触发执行...";

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($" [{taskType}] ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"{execTime:MM-dd HH:mm:ss} ({timeDisplay}){loopDisplay}");
                    
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"  └─ 需求: {intent} (ID: {t.Id})");
            }
            Console.ResetColor();
        }
    }
    catch { /* 忽略解析错误 */ }
}
// ========================== 获取局域网 IP ==========================
string GetLocalIpAddress()
{
    try
    {
        string? backupIp = null;
        
        // 遍历所有网络接口
        foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
        {
            // 排除没连上的、回环网卡（127.0.0.1）
            if (item.OperationalStatus != OperationalStatus.Up) continue;
            if (item.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            
            // 简单暴力地屏蔽掉常见的虚拟网卡名字
            var name = item.Name.ToLower();
            var desc = item.Description.ToLower();
            if (name.Contains("vmware") || name.Contains("virtual") || name.Contains("vbox") ||
                desc.Contains("vmware") || desc.Contains("virtual") || desc.Contains("vpn") || 
                desc.Contains("zerotier") || desc.Contains("radmin") || desc.Contains("tailscale"))
            {
                continue;
            }

            foreach (var ip in item.GetIPProperties().UnicastAddresses)
            {
                // 只找 IPv4
                if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var ipStr = ip.Address.ToString();
                    
                    // 核心逻辑：优先返回最典型的局域网网段
                    if (ipStr.StartsWith("192.168.") || ipStr.StartsWith("10.") || 
                       (ipStr.StartsWith("172.") && !ipStr.StartsWith("172.16."))) // Docker 默认常在 172.17+
                    {
                        return ipStr; 
                    }
                    
                    // 如果没找到典型局域网，先留个备胎（排除掉 169.254.x.x 无效IP）
                    if (string.IsNullOrEmpty(backupIp) && !ipStr.StartsWith("169.254."))
                    {
                        backupIp = ipStr;
                    }
                }
            }
        }
        return backupIp ?? "127.0.0.1";
    }
    catch
    {
        return "127.0.0.1";
    }
}
// ========================== 9. 主交互死循环 ==========================
while (true)
{
    ShowPendingTasks();
    
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.Write("\n皮皮虾 > ");
    Console.ResetColor();
    var input = Console.ReadLine();
    if (string.IsNullOrEmpty(input)) continue;
    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
    
    await RunAgent(input);
}

return;

// ========================== 10. 核心 Agent 处理逻辑 ==========================
async Task<string> RunAgent(string inputMessage, bool isScheduledEvent = false, Action<string, string>? onUpdate = null)
{
    await agentLock.WaitAsync();
    using var taskCts = new CancellationTokenSource();
    currentTaskCts = taskCts;
    string finalAIResponse = "";
    try
    {
        var useFullContext = false; 
        var requireReset = false;   
        var userMsg = new ChatMessage { Role = "user", Content = inputMessage };
        
        fullHistory.Add(userMsg.DeepClone());
        SaveData(fullHistory, checkpointPath);
        
        var isDone = false;
        while (!isDone)
        {
            using var cts = new CancellationTokenSource();
            var animTask = Think(cts.Token);
            
            var currentTasksJson = "暂无定时任务";
            if (File.Exists(tasksPath))
            {
                lock (tasksPath) { try { currentTasksJson = File.ReadAllText(tasksPath, Encoding.UTF8); } catch { } }
            }

            var systemPromptText = $"""
                                    你是一个全能智能 Agent，代号 PiPiClaw (皮皮虾)。当前系统：{RuntimeInformation.OSDescription}。当前时间是 {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}。
                                    {sudoInstruction}
                                    你的使命：以本地优先、安全可审计的方式完成用户提出的任何任务，不限于运维/开发/数据/知识检索。你可随时通过 Skill-Hub 搜索或安装一万+ 生态技能扩展能力。
                                    【记忆管理架构】：
                                    1. 为节省Token，你的短时记忆默认只保留最近几次的对话记录。
                                    2. 当任务彻底完成时调用 finish_task 清理环境。
                                    3. [技能调用策略] 下方列表包含了本地已安装的技能和简短摘要。当用户的需求需要用到某个技能时，你必须先调用 read_file 工具，读取该技能目录下的 skill.md 文件以获取完整的对接文档，然后再根据文档指导进行下一步操作。绝对不要凭空猜测调用方式！

                                    【PiPiClaw 挂起的定时任务（包含 task_id，供你管理任务时参考）】：
                                    {currentTasksJson}

                                    【本地已安装的扩展技能及绝对路径说明】：
                                    {GetInstalledSkillsContext()}
                                    """;

            var payloadMessages = new List<ChatMessage> { new ChatMessage { Role = "system", Content = systemPromptText } };

            IEnumerable<ChatMessage> recentMessages;
            if (useFullContext)
            {
                recentMessages = fullHistory;
            }
            else
            {
                var seenUser = 0;
                var startIndex = 0;
                for (var i = fullHistory.Count - 1; i >= 0; i--)
                {
                    if (fullHistory[i].Role != "user") continue;
                    seenUser++;
                    if (seenUser <= 3) continue;
                    startIndex = i + 1;
                    break;
                }
                recentMessages = fullHistory.Skip(startIndex);
            }

            foreach (var m in recentMessages) payloadMessages.Add(m.DeepClone());
            
            var payload = new LlmRequest
            {
                Model = GetConfig("Model", "qwen3.5-plus"),
                Messages = payloadMessages,
                Tools = toolsDoc.RootElement,
                EnableSearch = true
            };
            
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetConfig("ApiKey"));
            var content = new StringContent(JsonSerializer.Serialize(payload, AppJsonContext.Default.LlmRequest), Encoding.UTF8, "application/json");
            
            HttpResponseMessage res;
            string responseString;
            try 
            { 
                res = await client.PostAsync(GetConfig("Endpoint", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"), content, taskCts.Token); 
                res.EnsureSuccessStatusCode();
                responseString = await res.Content.ReadAsStringAsync(taskCts.Token);
            }
            catch(OperationCanceledException)
            {
                cts.Cancel(); await animTask;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(CancelledMsg); Console.ResetColor();
                onUpdate?.Invoke("final", CancelledMsg);
                return CancelledMsg;
            }
            catch(Exception ex) 
            { 
                cts.Cancel(); await animTask; 
                Console.ForegroundColor = ConsoleColor.Red;
                var err = $"\n[网络错误] 请求 API 失败: {ex.Message}";
                Console.WriteLine(err); Console.ResetColor();
                onUpdate?.Invoke("final", err);
                return err; 
            }
            var msg = JsonSerializer.Deserialize(responseString, AppJsonContext.Default.LlmResponse)?.Choices?.FirstOrDefault()?.Message;
            
            cts.Cancel(); await animTask;
            if (msg == null) break;
            
            fullHistory.Add(msg.DeepClone());
            SaveData(fullHistory, checkpointPath);
            
            var toolCalls = msg.ToolCalls;
            if (toolCalls != null && toolCalls.Count > 0)
            {
                foreach (var call in toolCalls)
                {
                    var fnName = call.Function.Name;
                    var argsString = call.Function.Arguments;
                    JsonElement? tempArgs = null;
                    try { tempArgs = JsonDocument.Parse(argsString).RootElement; } catch { }

                    string GetStrProp(JsonElement? el, string key) {
                        if (el.HasValue && el.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String) return prop.GetString() ?? "";
                        return "";
                    }
                    bool GetBoolProp(JsonElement? el, string key) {
                        if (el.HasValue && el.Value.TryGetProperty(key, out var prop) && (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)) return prop.GetBoolean();
                        return false;
                    }
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n[PiPiClaw 正在调用]: {fnName}");
                    Console.ResetColor();
                    
                    string actionDesc = "";
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    switch (fnName)
                    {
                        case "execute_command": actionDesc = $"执行命令: {GetStrProp(tempArgs, "command")} (后台: {GetBoolProp(tempArgs, "is_background")})"; Console.WriteLine($"[Action] {actionDesc}"); break;
                        case "read_file": actionDesc = $"正在读取文件: {GetStrProp(tempArgs, "file_path")}"; Console.WriteLine($"[Action] {actionDesc}"); break;
                        case "write_file": actionDesc = $"正在写入文件: {GetStrProp(tempArgs, "file_path")}"; Console.WriteLine($"[Action] {actionDesc}"); break;
                        case "search_content": actionDesc = $"全局搜索关键字: {GetStrProp(tempArgs, "keyword")}"; Console.WriteLine($"[Action] {actionDesc}"); break;
                        default: actionDesc = $"调用参数: {argsString}"; break;
                    }
                    Console.ResetColor();
                    onUpdate?.Invoke("tool", $"[调用工具] {fnName}\n{actionDesc}");

                    var result = "";
                    switch (fnName)
                    {
                        case "search_skill": result = await SearchSkill(GetStrProp(tempArgs, "query")); break;
                        case "install_skill": result = await InstallSkill(GetStrProp(tempArgs, "slug")); break;
                        case "finish_task":
                            requireReset = true; 
                            result = "[系统提示] 上下文清理已预约，这将在你给出最后一句回复后执行。请现在用正常的自然语言向用户总结任务完成情况。";
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("[内部状态] Agent 判定任务结束，已预约清理上下文...");
                            Console.ResetColor();
                            break;
                        case "add_scheduled_task":
                        {
                            int intervalMin = 0;
                            if (tempArgs.HasValue && tempArgs.Value.TryGetProperty("interval_minutes", out var intProp) && intProp.ValueKind == JsonValueKind.Number)
                                intervalMin = intProp.GetInt32();
                            result = AddScheduledTask(GetStrProp(tempArgs, "execute_at"), GetStrProp(tempArgs, "user_intent"), intervalMin);
                            break;
                        }
                        case "remove_scheduled_task": result = RemoveScheduledTask(GetStrProp(tempArgs, "task_id")); break;
                        case "self_update": result = await SelfUpdate(); break;
                        case "execute_command": result = RunCmd(GetStrProp(tempArgs, "command"), GetBoolProp(tempArgs, "is_background"), taskCts.Token); break;
                        default:
                            result = fnName switch
                            {
                                "read_file" => ReadFile(GetStrProp(tempArgs, "file_path")),
                                "write_file" => WriteFile(GetStrProp(tempArgs, "file_path"), GetStrProp(tempArgs, "content"), GetStrProp(tempArgs, "old_content")),
                                "read_local_image" => ReadImg(GetStrProp(tempArgs, "file_path")),
                                "search_content" => SearchContent(GetStrProp(tempArgs, "directory"), GetStrProp(tempArgs, "keyword"), GetStrProp(tempArgs, "file_pattern")),
                                _ => "[未知工具] 系统不支持此工具。"
                            };
                            break;
                    }
                    
                    onUpdate?.Invoke("tool_result", result);

                    var toolResultMsg = new ChatMessage
                    {
                        Role = "tool",
                        Name = fnName,
                        Content = result,
                        ToolCallId = call.Id
                    };
                    fullHistory.Add(toolResultMsg.DeepClone());
                    SaveData(fullHistory, checkpointPath); 
                }
            }
            else
            {
                finalAIResponse = msg.Content ?? "";
                Console.ResetColor();
                Console.WriteLine($"\n{finalAIResponse}");
                Console.ResetColor();
                onUpdate?.Invoke("final", finalAIResponse); 
                isDone = true; 
            }
        }
        
        if (requireReset)
        {
            fullHistory.Clear();
            if (File.Exists(checkpointPath)) File.Delete(checkpointPath); 
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n✅ [生命周期] 本次任务上下文已全自动清理！PiPiClaw 已就绪，随时接收新任务。");
            Console.ResetColor();
        }

        if (selfUpdateRequested)
        {
            selfUpdateRequested = false;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[自动更新] 皮皮虾即将退出并由更新脚本完成替换重启，再见！");
            Console.ResetColor();
            await Task.Delay(1500);
            Environment.Exit(0);
        }
        
        if (isScheduledEvent) 
        {
            ShowPendingTasks();
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write("\n皮皮虾 > ");
            Console.ResetColor();
        }
    }
    finally
    {
        currentTaskCts = null;
        agentLock.Release(); 
    }
    return finalAIResponse;
}

// ========================== 11. 定时调度器守护逻辑 ==========================
async Task ScheduleLoop()
{
    while (true)
    {
        try
        {
            if (File.Exists(tasksPath))
            {
                var tasks = new List<TaskItem>();
                lock (tasksPath)
                {
                    try 
                    { 
                        var content = File.ReadAllText(tasksPath, Encoding.UTF8);
                        if(!string.IsNullOrWhiteSpace(content)) tasks = JsonSerializer.Deserialize(content, AppJsonContext.Default.ListTaskItem) ?? new(); 
                    } catch { }
                }

                bool modified = false;
                var pendingList = tasks.Where(t => t.Status == "pending").ToList();

                foreach (var t in pendingList)
                {
                    if (!DateTimeOffset.TryParse(t.ExecuteAt, out var execTime)) continue;
                    if (DateTimeOffset.Now < execTime) continue;
                    
                    t.Status = "executing";
                    modified = true;
                    
                    var intent = t.UserIntent;
                    var taskId = t.Id;
                            
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            Console.ForegroundColor = ConsoleColor.Magenta;
                            Console.WriteLine($"\n\n[🔔 皮皮虾唤醒] 正在接管系统执行预定需求: {intent}");
                            Console.ResetColor();
                                    
                            await RunAgent($"[系统级注入] 这是一个系统自动触发的定时任务。你之前设定了在此时执行该任务，用户的原始需求是：【{intent}】。请立刻处理，绝不要尝试手动重新添加定时任务，完成后调用 finish_task 结束。", true);
                        }
                        catch (Exception ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"\n[定时任务执行异常] {ex.Message}");
                            Console.ResetColor();
                        }
                        finally
                        {
                            lock (tasksPath)
                            {
                                try
                                {
                                    var currentTasks = JsonSerializer.Deserialize(File.ReadAllText(tasksPath, Encoding.UTF8), AppJsonContext.Default.ListTaskItem) ?? new();
                                    var taskToUpdate = currentTasks.FirstOrDefault(x => x.Id == taskId);
                                    
                                    if (taskToUpdate != null)
                                    {
                                        int interval = taskToUpdate.IntervalMinutes;
                                        if (interval > 0)
                                        {
                                            DateTimeOffset nextTime;
                                            if (DateTimeOffset.TryParse(taskToUpdate.ExecuteAt, out var lastExecTime))
                                            {
                                                nextTime = lastExecTime.AddMinutes(interval);
                                                while (nextTime <= DateTimeOffset.Now) nextTime = nextTime.AddMinutes(interval);
                                            }
                                            else nextTime = DateTimeOffset.Now.AddMinutes(interval);

                                            taskToUpdate.ExecuteAt = nextTime.ToString("o");
                                            taskToUpdate.Status = "pending";
                                        }
                                        else taskToUpdate.Status = "done";
                                        
                                        File.WriteAllText(tasksPath, JsonSerializer.Serialize(currentTasks, AppJsonContext.Default.ListTaskItem), Encoding.UTF8);
                                    }
                                }
                                catch { /* 忽略并发报错 */ }
                            }
                        }
                    });
                }
                
                if (modified)
                {
                    lock (tasksPath) { File.WriteAllText(tasksPath, JsonSerializer.Serialize(tasks, AppJsonContext.Default.ListTaskItem), Encoding.UTF8); }
                }
            }
        }
        catch { /* 守护进程容错 */ }

        await Task.Delay(1000); 
    }
}

// ========================== 12. 工具函数封装区域 ==========================
string GetInstalledSkillsContext()
{
    var skillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
    if (!Directory.Exists(skillsDir)) return "当前未安装任何技能";

    var sb = new StringBuilder();
    foreach (var dir in Directory.GetDirectories(skillsDir))
    {
        var slug = new DirectoryInfo(dir).Name;
        var summaryPath = Path.Combine(dir, "summary.txt");
        var absolutePath = Path.GetFullPath(dir).Replace("\\", "/"); 
        sb.AppendLine($"- [{slug}] 绝对路径目录: {absolutePath}");
        if (File.Exists(summaryPath))
        {
            try 
            { 
                sb.AppendLine($"  技能功能摘要: {File.ReadAllText(summaryPath, Encoding.UTF8).Trim()}"); 
                sb.AppendLine($"  (如需使用此技能，请务必先用 read_file 读取 {absolutePath}/skill.md)");
            } catch { }
        }
        else
        {
            sb.AppendLine($"  技能功能摘要: [未生成摘要]");
            sb.AppendLine($"  说明：该技能已安装。请先 read_file 查看 {absolutePath}/skill.md，然后使用 write_file 在此目录下生成一个名为 summary.txt 的文件，里面只写一句话功能概括即可。");
        }
    }
    return sb.Length > 0 ? sb.ToString() : "当前未安装任何技能";
}

async Task Think(CancellationToken ct)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.CursorVisible = false;
    for (int i = 0; !ct.IsCancellationRequested; i++)
    {
        Console.Write($"\r {"-\\|/"[i % 4]} 皮皮虾正在思考中...");
        try { await Task.Delay(100, ct); } catch { }
    }
    Console.Write("\r".PadRight(30) + "\r"); 
    Console.ResetColor();
    Console.CursorVisible = true;
}

void SaveData(List<ChatMessage> data, string path)
{
    try { File.WriteAllText(path, JsonSerializer.Serialize(data, AppJsonContext.Default.ListChatMessage), Encoding.UTF8); } catch { }
}

string ReadPasswordHidden()
{
    var pwd = new StringBuilder();
    while (true)
    {
        var i = Console.ReadKey(true);
        if (i.Key == ConsoleKey.Enter) break;
        else if (i.Key == ConsoleKey.Backspace)
        {
            if (pwd.Length <= 0) continue;
            pwd.Remove(pwd.Length - 1, 1); Console.Write("\b \b");
        }
        else if (i.KeyChar != '\u0000') { pwd.Append(i.KeyChar); Console.Write("*"); }
    }
    Console.WriteLine();
    return pwd.ToString();
}

string RunCmd(string? cmd, bool isBackground = false, CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(cmd)) return "[执行失败] 命令为空";
    var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    var askPassPath = "";
    
    if (!isWin && cmd.Contains("sudo ") && !cmd.Contains("-S") && string.IsNullOrEmpty(sudoPassword))
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("\n[提权拦截] 当前任务需要 sudo 权限，请输入密码(回车确认，输入内容不可见): ");
        Console.ResetColor();
        sudoPassword = ReadPasswordHidden();
    }
    
    if (!string.IsNullOrEmpty(sudoPassword))
    {
        switch (isWin)
        {
            case false: 
            {
                askPassPath = Path.Combine(Path.GetTempPath(), $"pi_askpass_{Guid.NewGuid():N}.sh");
                var safePwd = sudoPassword.Replace("'", "'\\''");
                File.WriteAllText(askPassPath, $"#!/bin/bash\necho '{safePwd}'\n");
            
                using (var chmodProc = Process.Start(new ProcessStartInfo("chmod", $"+x {askPassPath}") { CreateNoWindow = true }))
                {
                    chmodProc?.WaitForExit();
                }
                if (cmd.Contains("sudo ") && !cmd.Contains("-S")) cmd = cmd.Replace("sudo ", $"echo '{safePwd}' | sudo -S ");
                break;
            }
            case true when (cmd.Contains("sudo ") || cmd.Contains("runas ")): 
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[底层拦截] 警告：Windows 环境下检测到类 sudo 提权命令。PiPiClaw 无法全自动提供密码。请确保以管理员身份运行本程序。");
                Console.ResetColor();
                break;
        }
    }
    
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"[{(isBackground ? "后台异步执行中" : "同步执行中")}] {cmd}");
    Console.ResetColor();

    // 在 Windows 上，子进程的 cmd.exe 默认使用系统代码页（如 GBK），
    // 通过在命令前注入 chcp 65001 强制子进程也切换到 UTF-8，避免乱码。
    var shellArg = isWin ? $"/c chcp 65001 >nul 2>&1 & {cmd}" : $"-c \"{cmd}\"";
    var shellExe = isWin ? "cmd.exe" : "/bin/bash";
    var consoleEncoding = new UTF8Encoding(false);

    if (isBackground)
    {
        // ================= 后台非阻塞模式 =================
        // 丢入 Task.Run 剥离出主线程，大模型不会被卡死
        Task.Run(() =>
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo(shellExe, shellArg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = consoleEncoding,
                StandardErrorEncoding = consoleEncoding
            };

            if (!isWin && !string.IsNullOrEmpty(askPassPath)) p.StartInfo.EnvironmentVariables["SUDO_ASKPASS"] = askPassPath;
            
            // 后台任务的输出直接打印到控制台，不返回给大模型
            p.OutputDataReceived += (sender, e) => { if (e.Data != null) Console.WriteLine($"[后台] {e.Data}"); };
            p.ErrorDataReceived += (sender, e) => { if (e.Data != null) { Console.ForegroundColor = ConsoleColor.DarkYellow; Console.WriteLine($"[后台警告] {e.Data}"); Console.ResetColor(); } };
            
            try { p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); p.WaitForExit(); } catch { }
            finally { if (!string.IsNullOrEmpty(askPassPath) && File.Exists(askPassPath)) try { File.Delete(askPassPath); } catch { } }
        });

        // 立即返回给大模型，解除阻塞
        return $"[已转入后台运行] 进程已在后台剥离启动: {cmd}。因为是后台任务，所以你将不会直接收到此命令的后续输出。如果需要知道运行状态，请通过其他命令（如 ps、curl 或检查日志文件）来验证。";
    }
    else
    {
        // ================= 前台阻塞模式 =================
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(shellExe, shellArg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = consoleEncoding,
            StandardErrorEncoding = consoleEncoding
        };

        if (!isWin && !string.IsNullOrEmpty(askPassPath)) p.StartInfo.EnvironmentVariables["SUDO_ASKPASS"] = askPassPath;
        
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        p.OutputDataReceived += (sender, e) => { if (e.Data == null) return; Console.WriteLine(e.Data); outputBuilder.AppendLine(e.Data); };
        p.ErrorDataReceived += (sender, e) => { if (e.Data == null) return; Console.ForegroundColor = ConsoleColor.DarkYellow; Console.WriteLine(e.Data); Console.ResetColor(); errorBuilder.AppendLine(e.Data); };
        
        try
        {
            p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
            // 注册取消回调：取消时终止子进程
            using (ct.Register(() => { try { p.Kill(true); } catch { } }))
                p.WaitForExit();
            if (ct.IsCancellationRequested) return CancelledMsg;
        }
        catch (Exception ex) { return $"[执行异常] {ex.Message}"; }
        finally { if (!string.IsNullOrEmpty(askPassPath) && File.Exists(askPassPath)) try { File.Delete(askPassPath); } catch { } }
        
        var errLines = errorBuilder.ToString().Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var outLines = outputBuilder.ToString().Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var compressedErr = UniversalLogCompressor.CompressLogs(errLines);
        var compressedOut = UniversalLogCompressor.CompressLogs(outLines);
        var finalErr = string.Join("\n", compressedErr).Trim();
        var finalOut = string.Join("\n", compressedOut).Trim();

        return !string.IsNullOrWhiteSpace(finalErr) ? $"[标准错误/进度信息]\n{finalErr}\n[标准输出]\n{finalOut}" : finalOut;
    }
}

string ReadFile(string? path)
{
    if (path == null || !File.Exists(path)) return "[文件不存在]";
    try { return ReadTextSmart(path, out _); }
    catch (Exception ex) { return $"[读取失败] {ex.Message}"; }
}

string WriteFile(string? path, string? content, string? oldContent = null)
{
    if (path == null) return "[写入失败] 路径为空";
    try
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        Encoding targetEncoding = new UTF8Encoding(false);
        var currentText = File.Exists(path) ? ReadTextSmart(path, out targetEncoding) : "";

        if (File.Exists(path) && !string.IsNullOrEmpty(oldContent))
        {
            var normalizedCurrent = currentText.Replace("\r\n", "\n");
            var normalizedOld = oldContent.Replace("\r\n", "\n");
            var normalizedNew = content?.Replace("\r\n", "\n") ?? "";

            if (!normalizedCurrent.Contains(normalizedOld)) return "[修改失败] 未精准匹配到 old_content，请确认内容。如果你不确定，请先 read_file 查看完整内容。";
            
            var finalContent = normalizedCurrent.Replace(normalizedOld, normalizedNew);
            File.WriteAllText(path, finalContent.Replace("\n", Environment.NewLine), targetEncoding);
            return $"[修改成功] 文件局部已更新：{path}";
        }

        File.WriteAllText(path, content ?? "", targetEncoding);
        return $"[写入成功] 文件已全量保存：{path}";
    }
    catch (Exception ex) { return $"[写入/修改失败] {ex.Message}"; }
}

string ReadTextSmart(string path, out Encoding detectedEncoding)
{
    var bytes = File.ReadAllBytes(path);
    detectedEncoding = new UTF8Encoding(false, true); 
    try { return detectedEncoding.GetString(bytes); }
    catch { detectedEncoding = new UTF8Encoding(false); return Encoding.UTF8.GetString(bytes); }
}

string ReadImg(string? path)
{
    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "[文件不存在]";
    try
    {
        var b = File.ReadAllBytes(path);
        var ext = Path.GetExtension(path).TrimStart('.').ToLower();
        if (ext == "jpg") ext = "jpeg";
        return b.Length > 2000000 ? "[图片过大] 请上传小于 2MB 的图片" : $"data:image/{ext};base64,{Convert.ToBase64String(b)}";
    }
    catch (Exception ex) { return $"[读取失败] {ex.Message}"; }
}

string SearchContent(string? dir, string? keyword, string? pattern)
{
    dir = string.IsNullOrEmpty(dir) ? "." : dir; pattern = string.IsNullOrEmpty(pattern) ? "*.*" : pattern;
    if (string.IsNullOrEmpty(keyword)) return "[搜索失败] 关键字为空";
    if (!Directory.Exists(dir)) return "[搜索失败] 目录不存在";

    try
    {
        var results = new StringBuilder(); var matchCount = 0;
        foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}.git") || file.Contains($"{Path.DirectorySeparatorChar}bin") || file.Contains($"{Path.DirectorySeparatorChar}obj")) continue;
            try
            {
                if (ReadTextSmart(file, out _).Contains(keyword))
                {
                    results.AppendLine($"- {file}"); matchCount++;
                    if (matchCount >= 20) { results.AppendLine("...[结果过多已截断，请提供更精确的关键字]..."); break; }
                }
            }
            catch { } 
        }
        return matchCount == 0 ? $"[未找到] 关键字 '{keyword}'" : $"[搜索成功] 匹配到以下文件：\n{results}";
    }
    catch (Exception ex) { return $"[异常] {ex.Message}"; }
}

// ========================== 13. 技能拓展功能 ==========================
async Task<string> SearchSkill(string? query)
{
    if (string.IsNullOrEmpty(query)) return "❌ 搜索关键词不能为空";
    var sb = new StringBuilder();
    sb.AppendLine($"🚀 正在为您搜索技能: '{query}' ...");

    using var handler = new HttpClientHandler() { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
    using var searchClient = new HttpClient(handler);
    searchClient.DefaultRequestHeaders.Add("User-Agent", "skills-store-cli/0.1");
    searchClient.DefaultRequestHeaders.Host = "skillhub.ai";

    try
    {
        string escapedQuery = Uri.EscapeDataString(query);
        string url = $"http://lb-3zbg86f6-0gwe3n7q8t4sv2za.clb.gz-tencentclb.com/api/v1/search?q={escapedQuery}&limit=10";

        var response = await searchClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) return $"❌ 搜索请求失败，状态码: {response.StatusCode}";

        var jsonString = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(jsonString);
        var root = doc.RootElement;

        if (root.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
        {
            int count = resultsProp.GetArrayLength();
            if (count == 0) return $"⚠️ 未找到与 '{query}' 相关的技能，请尝试其他关键词。";

            sb.AppendLine($"✅ 找到 {count} 个相关技能：\n");
            foreach (var item in resultsProp.EnumerateArray())
            {
                string slug = item.TryGetProperty("slug", out var s) ? s.GetString() ?? "未知" : "未知";
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "未知" : "未知";
                string desc = item.TryGetProperty("description", out var d) ? d.GetString() ?? "无描述" :
                              (item.TryGetProperty("summary", out var sum) ? sum.GetString() ?? "无描述" : "无描述");

                sb.AppendLine($"- 标识 (Slug): {slug}");
                sb.AppendLine($"  名称 (Name): {name}");
                sb.AppendLine($"  描述 (Desc): {desc}");
                sb.AppendLine("  -------------------------");
            }
            sb.AppendLine("\n💡 请询问用户要安装以上哪一个技能，或者直接根据语境调用 install_skill 安装逻辑。");
        }
        else return "❌ 解析技能数据失败：返回的数据格式不包含 results 数组。";
    }
    catch (Exception ex) { return $"❌ 搜索过程发生异常: {ex.Message}"; }
    return sb.ToString();
}

async Task<string> InstallSkill(string? slug)
{
    if (string.IsNullOrEmpty(slug)) return "❌ slug不能为空";
    var sb = new StringBuilder();
    sb.AppendLine($"🚀 启动安装程序: {slug}...");

    try
    {
        string[] downloadUrls = [ $"https://skillhub-1388575217.cos.ap-guangzhou.myqcloud.com/skills/{slug}.zip", $"https://wry-manatee-359.convex.site/api/v1/download?slug={slug}" ];
        string skillsFolder = Path.Combine(AppContext.BaseDirectory, "skills");
        string targetFolder = Path.Combine(skillsFolder, slug);
        string zipPath = Path.Combine(skillsFolder, $"{slug}.zip");

        if (!Directory.Exists(skillsFolder)) Directory.CreateDirectory(skillsFolder);

        bool downloadSuccess = false;
        foreach (var url in downloadUrls)
        {
            try { await DownloadFileAsync(url, zipPath); downloadSuccess = true; break; }
            catch (Exception ex) { sb.AppendLine($"⚠️ 当前节点不可用 ({ex.Message})，准备切换下一个节点..."); }
        }

        if (!downloadSuccess) throw new Exception("所有下载节点均不可用，文件获取失败。");

        sb.AppendLine("📦 正在解压技能文件...");
        if (Directory.Exists(targetFolder)) Directory.Delete(targetFolder, true);
        Directory.CreateDirectory(targetFolder);

        // --- 跨平台原生解压逻辑替代 System.IO.Compression ---
        bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        using var p = new Process();
        if (isWin)
        {
            // Windows 使用 PowerShell 原生解压
            p.StartInfo = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"Expand-Archive -Path '{zipPath}' -DestinationPath '{targetFolder}' -Force\"") { CreateNoWindow = true };
        }
        else
        {
            // Linux/macOS 使用原生 unzip 命令
            p.StartInfo = new ProcessStartInfo("unzip", $"-o \"{zipPath}\" -d \"{targetFolder}\"") { CreateNoWindow = true };
        }
        
        try 
        {
            p.Start();
            p.WaitForExit();
            if (p.ExitCode != 0) 
            {
                throw new Exception(isWin ? "PowerShell 解压失败。" : "unzip 命令执行失败。Linux 系统请确保已安装 unzip (如: sudo apt install unzip)。");
            }
        }
        catch (Exception cmdEx)
        {
            throw new Exception($"调用系统底层解压失败: {cmdEx.Message}。请检查系统环境。");
        }
        // ---------------------------------------------------

        File.Delete(zipPath);
        sb.AppendLine("✅ 技能解压完成。");

        string mdPath = Path.Combine(targetFolder, "skill.md");
        string absolutePath = Path.GetFullPath(targetFolder).Replace("\\", "/");

        if (File.Exists(mdPath))
        {
            sb.AppendLine($"📄 发现 skill.md。绝对路径为: {absolutePath}/skill.md");
            sb.AppendLine("[系统指令] 技能安装已完成。请立刻调用 `read_file` 工具读取上述 skill.md 文件了解该技能。随后，调用 `write_file` 工具在同目录下生成一个名为 `summary.txt` 的文件，用一句话（20字以内）总结该技能的作用，作为未来的轻量级索引。");
        }
        else sb.AppendLine("⚠️ 未找到 skill.md，你可能需要自行摸索或使用 read_file 去查看文件夹。");
    }
    catch (Exception ex) { sb.AppendLine($"❌ 安装失败: {ex.Message}"); }
    sb.AppendLine("\n🎉 安装过程结束。");
    return sb.ToString().Replace("#", "");
}


async Task DownloadFileAsync(string url, string destinationPath)
{
    using var httpClient = new HttpClient();
    using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    using var stream = await response.Content.ReadAsStreamAsync();
    using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
    await stream.CopyToAsync(fileStream);
}

// ========================== 14. 自动更新逻辑 ==========================
async Task<string> SelfUpdate()
{
    try
    {
        bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        string osName = isWin ? "win" : isMac ? "osx" : "linux";
        string archName = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.X86   => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm   => "arm",
            _                  => "x64"
        };
        string rid = $"{osName}-{archName}";
        string ext = isWin ? "zip" : "tar.gz";
        string archiveName = $"PiPiClaw_{rid}.{ext}";
        string exeName = isWin ? "PiPiClaw.exe" : "PiPiClaw";
        string downloadUrl = $"https://github.com/anan1213095357/PiPiClaw/releases/download/latest/{archiveName}";

        string? currentExePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExePath))
            currentExePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(currentExePath))
            return "[更新失败] 无法获取当前程序路径";

        int currentPid = Environment.ProcessId;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[自动更新] 将由外部脚本下载并替换 {archiveName}");
        Console.WriteLine($"[自动更新] 平台: {rid}，下载地址: {downloadUrl}");
        Console.ResetColor();

        if (isWin)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"pi_update_{Guid.NewGuid():N}.bat");
            string archivePath = Path.Combine(Path.GetTempPath(), $"PiPiClaw_pkg_{Guid.NewGuid():N}.zip");
            string extractDir = Path.Combine(Path.GetTempPath(), $"PiPiClaw_extract_{Guid.NewGuid():N}");

            var bat = new StringBuilder();
            bat.AppendLine("@echo off");
            bat.AppendLine("setlocal enabledelayedexpansion");
            bat.AppendLine($"set PID={currentPid}");
            bat.AppendLine($"set DL_URL=\"{downloadUrl}\"");
            bat.AppendLine($"set TARGET=\"{currentExePath}\"");
            bat.AppendLine($"set ARCHIVE=\"{archivePath}\"");
            bat.AppendLine($"set EXTRACT=\"{extractDir}\"");
            bat.AppendLine();
            bat.AppendLine("echo [AutoUpdate] 正在等待进程退出...");
            bat.AppendLine(":waitloop");
            bat.AppendLine("tasklist /FI \"PID eq %PID%\" | find \"%PID%\" >nul");
            bat.AppendLine("if %ERRORLEVEL%==0 (");
            bat.AppendLine("  timeout /t 1 /nobreak >nul");
            bat.AppendLine("  goto waitloop");
            bat.AppendLine(")");
            bat.AppendLine("powershell -NoProfile -Command \"Invoke-WebRequest -Uri '%DL_URL%' -OutFile '%ARCHIVE%'\" || goto fail");
            bat.AppendLine("powershell -NoProfile -Command \"Expand-Archive -Path '%ARCHIVE%' -DestinationPath '%EXTRACT%' -Force\" || goto fail");
            bat.AppendLine($"if not exist \"%EXTRACT%\\{exeName}\" goto fail");
            bat.AppendLine($"copy /y \"%EXTRACT%\\{exeName}\" \"%TARGET%\" || goto fail");
            bat.AppendLine("start \"\" \"%TARGET%\"");
            bat.AppendLine("rd /s /q \"%EXTRACT%\"");
            bat.AppendLine("del \"%ARCHIVE%\"");
            bat.AppendLine("del \"%~f0\"");
            bat.AppendLine("exit /b 0");
            bat.AppendLine(":fail");
            bat.AppendLine("echo [AutoUpdate] 更新失败，未能完成替换");
            bat.AppendLine("timeout /t 5 /nobreak >nul");
            bat.AppendLine("del \"%~f0\"");
            bat.AppendLine("exit /b 1");

            File.WriteAllText(scriptPath, bat.ToString(), Encoding.ASCII);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"") { CreateNoWindow = true, UseShellExecute = false });
        }
        else
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"pi_update_{Guid.NewGuid():N}.sh");
            string archivePath = Path.Combine(Path.GetTempPath(), $"PiPiClaw_pkg_{Guid.NewGuid():N}.{ext}");
            string extractDir = Path.Combine(Path.GetTempPath(), $"PiPiClaw_extract_{Guid.NewGuid():N}");

            var sh = new StringBuilder();
            sh.AppendLine("#!/bin/sh");
            sh.AppendLine($"PID={currentPid}");
            sh.AppendLine($"DL_URL=\"{downloadUrl}\"");
            sh.AppendLine($"TARGET=\"{currentExePath}\"");
            sh.AppendLine($"ARCHIVE=\"{archivePath}\"");
            sh.AppendLine($"EXTRACT=\"{extractDir}\"");
            sh.AppendLine();
            sh.AppendLine("echo \"[AutoUpdate] waiting process $PID exit...\"");
            sh.AppendLine("while kill -0 \"$PID\" 2>/dev/null; do");
            sh.AppendLine("  sleep 1");
            sh.AppendLine("done");
            sh.AppendLine();
            sh.AppendLine("if command -v curl >/dev/null 2>&1; then");
            sh.AppendLine("  curl -fL \"$DL_URL\" -o \"$ARCHIVE\" || exit 1");
            sh.AppendLine("elif command -v wget >/dev/null 2>&1; then");
            sh.AppendLine("  wget -O \"$ARCHIVE\" \"$DL_URL\" || exit 1");
            sh.AppendLine("else");
            sh.AppendLine("  echo \"[AutoUpdate] 未找到 curl 或 wget\"; exit 1;");
            sh.AppendLine("fi");
            sh.AppendLine();
            sh.AppendLine("mkdir -p \"$EXTRACT\"");
            sh.AppendLine("tar -xzf \"$ARCHIVE\" -C \"$EXTRACT\" || exit 1");
            sh.AppendLine($"if [ ! -f \"$EXTRACT/{exeName}\" ]; then echo \"[AutoUpdate] 未找到可执行文件\"; exit 1; fi");
            sh.AppendLine($"cp \"$EXTRACT/{exeName}\" \"$TARGET\" || exit 1");
            sh.AppendLine("chmod +x \"$TARGET\"");
            sh.AppendLine("nohup \"$TARGET\" >/dev/null 2>&1 &");
            sh.AppendLine("rm -rf \"$EXTRACT\" \"$ARCHIVE\"");
            sh.AppendLine("rm -f \"$0\"");

            File.WriteAllText(scriptPath, sh.ToString(), new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo("/bin/sh", scriptPath) { CreateNoWindow = true, UseShellExecute = false });
        }

        selfUpdateRequested = true;
        return $"[自动更新] 更新脚本已启动，将下载 {archiveName} 并在退出后自动替换重启。";
    }
    catch (Exception ex)
    {
        return $"[更新失败] {ex.Message}";
    }
}

async Task StartWebManager()
{
    int webPort = int.TryParse(GetConfig("WebPort", "5050"), out var p) && p > 0 ? p : 5050;
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://+:{webPort}/");
    try
    {
        listener.Start();
        string lanIp = GetLocalIpAddress();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Web UI] 网页控制台已启动:\n  - 本机访问: http://localhost:{webPort} \n  - 手机扫码或局域网: http://{lanIp}:{webPort}");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[Web UI] 启动失败(非致命): {ex.Message}。如果需要 Web 界面请尝试管理员运行。");
        Console.ResetColor();
        return;
    }

    while (true)
    {
        try
        {
            var context = await listener.GetContextAsync();
            var req = context.Request;
            var res = context.Response;

            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 200;
                res.Close();
                continue;
            }

            var url = req.Url;
            if (url == null)
            {
                res.StatusCode = 400;
                res.Close();
                continue;
            }

            if (url.AbsolutePath == "/")
            {
                string htmlContent = GetWebUIHtml()
                    .Replace("{{LAN_IP}}", GetLocalIpAddress())
                    .Replace("{{WEB_PORT}}", webPort.ToString())
                    .Replace("{{LOGO_DATA_URL}}", GetLogoDataUrl());
                byte[] buffer = Encoding.UTF8.GetBytes(htmlContent);
                res.ContentType = "text/html; charset=utf-8";
                res.ContentLength64 = buffer.Length;
                await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                res.Close();
            }
            else if (url.AbsolutePath == "/api/config" && req.HttpMethod == "GET")
            {
                var cfg = new AppConfig
                {
                    ApiKey = GetConfig("ApiKey"),
                    Model = GetConfig("Model", "qwen3.5-plus"),
                    Endpoint = GetConfig("Endpoint", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"),
                    SudoPassword = GetConfig("SudoPassword", ""),
                    WebPort = int.TryParse(GetConfig("WebPort", "5050"), out var wp) && wp > 0 ? wp : 5050
                };
                byte[] buffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cfg, AppJsonContext.Default.AppConfig));
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                res.Close();
            }
            else if (url.AbsolutePath == "/api/config" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var newCfg = JsonSerializer.Deserialize(body, AppJsonContext.Default.AppConfig);
                bool portChanged = false;
                if (newCfg != null)
                {
                    int currentPort = int.TryParse(GetConfig("WebPort", "5050"), out var cp) && cp > 0 ? cp : 5050;
                    if (newCfg.WebPort < 1 || newCfg.WebPort > 65535) newCfg.WebPort = currentPort;
                    portChanged = newCfg.WebPort != currentPort;
                    File.WriteAllText("appsettings.json", JsonSerializer.Serialize(newCfg, AppJsonContext.Default.AppConfig), Encoding.UTF8);
                    sudoPassword = newCfg.SudoPassword ?? "";
                }
                res.StatusCode = 200;
                res.ContentType = "application/json";
                var resultBytes = Encoding.UTF8.GetBytes(portChanged ? "{\"status\":\"port_changed\"}" : "{\"status\":\"ok\"}");
                await res.OutputStream.WriteAsync(resultBytes, 0, resultBytes.Length);
                res.Close();
            }
            else if (url.AbsolutePath == "/api/tasks" && req.HttpMethod == "GET")
            {
                string tasksJson = "[]";
                lock (tasksPath) {
                    if (File.Exists(tasksPath)) tasksJson = File.ReadAllText(tasksPath, Encoding.UTF8);
                }
                byte[] buffer = Encoding.UTF8.GetBytes(tasksJson);
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                res.Close();
            }
            else if (url.AbsolutePath == "/api/cancel" && req.HttpMethod == "POST")
            {
                currentTaskCts?.Cancel();
                res.StatusCode = 200;
                res.ContentType = "application/json";
                var cancelBytes = Encoding.UTF8.GetBytes("{\"status\":\"cancelled\"}");
                await res.OutputStream.WriteAsync(cancelBytes, 0, cancelBytes.Length);
                res.Close();
            }
            else if (url.AbsolutePath == "/api/chat" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var inputMsg = JsonSerializer.Deserialize(body, AppJsonContext.Default.ChatReq)?.Message;
                
                res.ContentType = "text/plain; charset=utf-8";
                res.SendChunked = true;
                using var writer = new StreamWriter(res.OutputStream, new UTF8Encoding(false));
                writer.AutoFlush = true;

                Action<string, string> onUpdate = (type, content) =>
                {
                    try {
                        var pushMsg = new PushMsg { Type = type, Content = content };
                        writer.Write(JsonSerializer.Serialize(pushMsg, AppJsonContext.Default.PushMsg) + "|||END|||");
                        writer.Flush();
                    } catch { }
                };

                if (!string.IsNullOrEmpty(inputMsg)) await RunAgent(inputMsg, false, onUpdate);
                res.Close();
            }
            else if (url.AbsolutePath == "/api/history" && req.HttpMethod == "GET")
            {
                string historyJson = JsonSerializer.Serialize(fullHistory, AppJsonContext.Default.ListChatMessage);
                byte[] buffer = Encoding.UTF8.GetBytes(historyJson);
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                res.Close();
            }
            else
            {
                res.StatusCode = 404;
                res.Close();
            }
        }
        catch { }
    }
}

// ========================== 15. HTML 前端重构区域 ==========================
string GetWebUIHtml()
{
    var html = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no" />
  <title>PiPiClaw // SkillHub Ready C&amp;C Terminal v3.0</title>

  <script src="https://cdn.jsdelivr.net/npm/qrcodejs@1.0.0/qrcode.min.js"></script>

  <script>
    if (localStorage.getItem('theme') === 'light') {
      document.documentElement.setAttribute('data-theme', 'light');
    }
  </script>

  <style>
    :root {
      /* === 默认深色主题 (Dark Theme) === */
      --bg-depth: #0e1420;
      --bg-card: rgba(255,255,255,.08);
      --pipi-magenta: #a78bfa;
      --pipi-cyan: #5ac8fa;
      --text-main: #e8edf5;
      --text-muted: #9aa6b6;
      --cyan-glow: rgba(90,200,250,.35);
      --font-mono: 'Fira Code','Consolas','Courier New',monospace;
      
      --shadow-color: rgba(0,0,0,.45);
      --shadow-color-hover: rgba(0,0,0,.55);
      --grid-line: rgba(255,255,255,.03);
      --glass-stroke: rgba(255,255,255,.12);
      --glass-surface: rgba(255,255,255,.06);
      --layer-solid: #101827;
      
      --input-bg: rgba(255,255,255,.07);
      --input-focus: rgba(255,255,255,.14);
      --input-border: rgba(255,255,255,.12);
      
      --chat-box-bg: rgba(255,255,255,.07);
      --chat-box-border: rgba(255,255,255,.12);
      
      --msg-user-bg: linear-gradient(145deg,rgba(90,200,250,.18),rgba(255,255,255,.06));
      --msg-ai-bg: linear-gradient(145deg,rgba(167,139,250,.18),rgba(255,255,255,.06));
      
      --term-bg: rgba(6,10,18,.9);
      --term-border: rgba(255,255,255,.1);
      --term-text: #c8e1ff;
      --term-action: #8bd1ff;
      --term-result: #9fb0c5;
      
      --cmd-bg: rgba(255,255,255,.05);
      --cmd-hover-bg: rgba(255,255,255,.1);

      /* 滚动条深色变量 */
      --sb-track: rgba(255,255,255,.06);
      --sb-thumb: rgba(90,200,250,.35);
      --sb-thumb-hover: rgba(90,200,250,.65);
    }

    /* === 浅色主题 (Light Theme) === */
    [data-theme="light"] {
      --bg-depth: #e9edf5;
      --bg-card: rgba(255,255,255,.9);
      --pipi-magenta: #7c6ff1;
      --pipi-cyan: #4a90e2;
      --text-main: #1f2a3d;
      --text-muted: #596273;
      --cyan-glow: rgba(74,144,226,.35);
      
      --shadow-color: rgba(0,0,0,.08);
      --shadow-color-hover: rgba(0,0,0,.12);
      --grid-line: rgba(255,255,255,.25);
      --glass-stroke: rgba(255,255,255,.5);
      --glass-surface: rgba(255,255,255,.72);
      --layer-solid: #edf1f7;
      
      --input-bg: rgba(255,255,255,.85);
      --input-focus: #ffffff;
      --input-border: rgba(0,0,0,.08);
      
      --chat-box-bg: rgba(255,255,255,.82);
      --chat-box-border: rgba(0,0,0,.08);
      
      --msg-user-bg: linear-gradient(145deg,rgba(74,144,226,.16),rgba(255,255,255,.9));
      --msg-ai-bg: linear-gradient(145deg,rgba(124,111,241,.16),rgba(255,255,255,.92));
      
      --term-bg: #f6f7fb;
      --term-border: rgba(0,0,0,.08);
      --term-text: #2f3d4f;
      --term-action: #3b82f6;
      --term-result: #64748b;
      
      --cmd-bg: rgba(74,144,226,.08);
      --cmd-hover-bg: rgba(74,144,226,.16);

      /* 滚动条浅色变量 */
      --sb-track: rgba(0,0,0,.08);
      --sb-thumb: rgba(74,144,226,.4);
      --sb-thumb-hover: rgba(74,144,226,.65);
    }

    /* === 全局滚动条美化 === */
    ::-webkit-scrollbar {
      width: 6px;
      height: 6px;
    }
    ::-webkit-scrollbar-track {
      background: var(--sb-track);
      border-radius: 4px;
    }
    ::-webkit-scrollbar-thumb {
      background: var(--sb-thumb);
      border-radius: 4px;
      transition: background 0.3s ease;
    }
    ::-webkit-scrollbar-thumb:hover {
      background: var(--sb-thumb-hover);
    }
    /* 兼容 Firefox */
    * {
      scrollbar-width: thin;
      scrollbar-color: var(--sb-thumb) var(--sb-track);
    }

    *{box-sizing:border-box}
    body{
      font-family:var(--font-mono);
      background:linear-gradient(135deg, var(--bg-depth), #0b101a 60%, var(--layer-solid));
      color:var(--text-main);
      margin:0;
      padding:20px;
      height:100dvh;
      display:flex;
      flex-direction:column;
      align-items:center;
      overflow:hidden;
      position:relative;
      text-size-adjust:100%;
      -webkit-text-size-adjust:100%;
      transition: background 0.4s ease, color 0.4s ease;
    }

    body::before{
      content:"";
      position:fixed; inset:0;
      background:linear-gradient(120deg, rgba(255,255,255,.04), transparent 30%, rgba(255,255,255,.06) 60%, transparent 100%);
      pointer-events:none;
      z-index:0;
      opacity:.85;
      backdrop-filter:blur(12px);
      transition: background 0.4s ease, opacity 0.4s ease;
    }

    body::after{
      content:"";
      position:fixed; inset:0;
      background:
        radial-gradient(circle at 20% 20%, rgba(90,200,250,.14), transparent 35%),
        radial-gradient(circle at 80% 15%, rgba(124,111,241,.14), transparent 40%),
        radial-gradient(circle at 40% 75%, rgba(90,200,250,.12), transparent 40%);
      z-index:-1;
      filter:blur(20px);
    }

    .container{width:100%; max-width:1000px; z-index:2; display:flex; flex-direction:column; gap:20px; flex:1; min-height:0;}

    .header{display:flex; align-items:center; justify-content:space-between; animation:slideDown .6s ease-out; gap:12px;}
    .header h1{
      font-size:2.4em;
      font-weight:800;
      margin:0;
      letter-spacing:2px;
      background:linear-gradient(120deg,var(--pipi-cyan),var(--pipi-magenta));
      background-size:200% auto;
      -webkit-background-clip:text;
      -webkit-text-fill-color:transparent;
      filter:drop-shadow(0 4px 12px rgba(0,0,0,.35));
      animation:shineText 8s ease infinite;
      display:inline-flex;
      align-items:center;
      gap:12px;
      justify-content:center;
      flex:1;
      text-align:center;
    }
    .header p{color:var(--text-muted); font-size:.9em; margin-top:6px; opacity:.9; letter-spacing:1px;}
    .header-btn{
      background:var(--glass-surface);
      border:1px solid var(--glass-stroke);
      color:var(--text-main);
      width:46px;
      height:46px;
      border-radius:12px;
      cursor:pointer;
      display:flex;
      align-items:center;
      justify-content:center;
      font-size:1.1em;
      font-weight:700;
      transition:all 0.3s ease;
      flex-shrink:0;
      box-shadow:0 12px 25px var(--shadow-color);
      backdrop-filter:blur(12px);
      letter-spacing:0;
    }
    .header-btn:hover{
      transform:translateY(-1px) scale(1.03);
      box-shadow:0 14px 28px var(--shadow-color-hover);
      border-color:var(--pipi-cyan);
    }

    /* === 主题切换按钮 === */
    .theme-toggle {
      font-size: 1.3em;
    }
    .collapse-toggle[aria-expanded="true"]{border-color:var(--pipi-cyan); color:var(--pipi-cyan); box-shadow:0 0 10px rgba(0,242,254,.2);}

    .box{
      background:var(--glass-surface);
      padding:25px;
      border-radius:16px;
      border:1px solid var(--glass-stroke);
      box-shadow:
        0 18px 40px var(--shadow-color),
        inset 0 1px 0 rgba(255,255,255,.12);
      position:relative;
      overflow:hidden;
      backdrop-filter:blur(14px);
      animation:slideUp .6s ease-out both;
      transition:transform .25s, box-shadow .25s, background .4s, border-color .4s;
    }
    .box:hover{
      box-shadow:0 20px 46px var(--shadow-color-hover), inset 0 1px 0 rgba(255,255,255,.16);
      transform:translateY(-1px);
    }

    h2{
      margin:0 0 12px 0;
      color:var(--text-main);
      font-size:1.05em;
      text-transform:uppercase;
      letter-spacing:1px;
      display:flex;
      align-items:center;
      gap:10px;
      transition: color 0.4s;
    }
    h2::before{
      content:"";
      width:6px; height:18px;
      background:var(--pipi-cyan);
      box-shadow:0 0 10px var(--pipi-cyan);
      border-radius:3px;
    }

    .logo-mark{width:42px;height:42px;object-fit:contain;filter:drop-shadow(0 0 8px rgba(0,242,254,.25));}
    .logo-badge{width:22px;height:22px;object-fit:contain;vertical-align:middle;}

    .collapsible .collapse-header{display:flex;align-items:center;justify-content:space-between;gap:12px;}
    .collapse-toggle{
      user-select:none;
    }
    .collapse-toggle:hover{box-shadow:0 0 12px rgba(0,242,254,.25);}
    .collapse-body{margin-top:15px;}
    .collapsible.collapsed .collapse-body{display:none;}

    .modal{
      position:fixed;
      inset:0;
      background:rgba(12,18,30,.65);
      display:none;
      align-items:center;
      justify-content:center;
      padding:20px;
      z-index:2000;
      backdrop-filter:blur(14px);
    }
    .modal.show{display:flex;}
    .modal-dialog{
      width:min(820px,calc(100% - 30px));
      max-height:90dvh;
      overflow:auto;
    }
    .modal-header{
      display:flex;
      align-items:center;
      justify-content:space-between;
      gap:12px;
      margin-bottom:12px;
    }
    .modal-close{
      width:40px;
      height:40px;
      font-size:0.9em;
    }

    .config-grid{display:grid;grid-template-columns:1fr;gap:15px;}
    .config-row{display:grid;grid-template-columns:1fr 1fr;gap:15px;}
    @media (max-width:600px){.config-row{grid-template-columns:1fr;}}

    label{
      display:block;
      color:var(--text-muted);
      font-size:.75em;
      margin-bottom:6px;
      text-transform:uppercase;
      letter-spacing:1px;
    }
    input{
      width:100%;
      padding:12px 14px;
      border-radius:8px;
      border:1px solid var(--input-border);
      background:var(--input-bg);
      color:var(--text-main);
      font-family:var(--font-mono);
      font-size:.95em;
      transition:all .2s;
    }
    input:focus{
      outline:none;
      border-color:var(--pipi-cyan);
      box-shadow:0 0 15px rgba(0,242,254,.2);
      background:var(--input-focus);
    }

    .btn-submit{
      width:100%;
      margin-top:14px;
      padding:12px 18px;
      border-radius:10px;
      background:rgba(0,242,254,.1);
      border:1px solid var(--pipi-cyan);
      color:var(--pipi-cyan);
      font-weight:800;
      font-family:var(--font-mono);
      cursor:pointer;
      transition:all .25s;
      letter-spacing:2px;
      text-transform:uppercase;
    }
    .btn-submit:hover{background:var(--pipi-cyan); color:#000; box-shadow:0 0 25px rgba(0,242,254,.6); transform:translateY(-1px);}
    .btn-submit:active{transform:translateY(0);}

    #terminalBox{flex:1; display:flex; flex-direction:column; min-height:0;}

    .chat-box{
      flex:1;
      min-height:0;
      overflow-y:auto;
      background:var(--chat-box-bg);
      padding:18px;
      border:1px solid var(--chat-box-border);
      border-radius:16px;
      display:flex;
      flex-direction:column;
      gap:18px;
      scroll-behavior:smooth;
      box-shadow:
        inset 0 1px 0 rgba(255,255,255,.08),
        0 18px 32px var(--shadow-color);
      transition: background 0.4s, border-color 0.4s, box-shadow 0.4s;
    }

    .msg{max-width:92%; display:flex; flex-direction:column; animation:msgPop .35s cubic-bezier(.175,.885,.32,1.275) both;}
    .msg-header{font-size:.75em; margin-bottom:6px; opacity:.9; display:flex; align-items:center; gap:8px; text-transform:uppercase; font-weight:800; letter-spacing:1px;}

    .msg.user{align-self:flex-end;}
    .msg.user .msg-header{color:var(--pipi-cyan); justify-content:flex-end;}
    .msg.user .msg-content{
      background:var(--msg-user-bg);
      border:1px solid rgba(255,255,255,.1);
      padding:14px 16px;
      border-radius:14px 14px 4px 14px;
      white-space:pre-wrap;
      word-break:break-word;
      transition: background 0.4s;
    }

    .msg.ai{align-self:flex-start; width:100%;}
    .msg.ai .msg-header{color:var(--pipi-magenta);}
    .msg.ai .msg-content{
      background:var(--msg-ai-bg);
      border:1px solid rgba(255,255,255,.08);
      padding:14px 16px;
      border-radius:14px 14px 14px 4px;
      white-space:pre-wrap;
      word-break:break-word;
      transition: background 0.4s;
    }

    .exec-terminal{
      background:var(--term-bg);
      border:1px solid var(--term-border);
      border-radius:12px;
      padding:10px;
      margin:10px 0 0 0;
      font-size:.82em;
      color:var(--term-text);
      height:160px;
      overflow-y:auto;
      box-shadow:inset 0 1px 0 rgba(255,255,255,.08), 0 8px 20px var(--shadow-color);
      transition: all 0.4s;
    }
    .exec-terminal .log-action{color:var(--term-action); font-weight:800; display:block; margin-bottom:4px;}
    .exec-terminal .log-result{color:var(--term-result); display:block; margin-bottom:10px; padding-left:8px; border-left:2px solid var(--chat-box-border);}

    .input-area{
      display:flex;
      gap:12px;
      align-items:stretch;
      margin-top:18px;
      border-top:1px solid var(--chat-box-border);
      padding-top:18px;
      transition: border-color 0.4s;
    }
    .input-main{
      position:relative;
      flex:1;
      display:flex;
      min-height:64px;
    }
    textarea{
      width:100%;
      min-height:64px;
      max-height:200px;
      background:var(--input-bg);
      border:1px solid var(--input-border);
      color:var(--text-main);
      font-family:var(--font-mono);
      font-size:1em;
      line-height:1.5;
      padding:14px 16px;
      border-radius:14px;
      resize:vertical;
      outline:none;
      transition:all .2s;
      box-shadow:inset 0 1px 0 rgba(255,255,255,.08), 0 10px 24px var(--shadow-color);
    }
    textarea:disabled{
      opacity:0.9;
      cursor:not-allowed;
    }
    textarea:focus{
      border-color:var(--pipi-cyan);
      background:var(--input-focus);
      box-shadow:0 12px 26px var(--shadow-color-hover), inset 0 1px 0 rgba(255,255,255,.12);
    }

    .loading-overlay{
      position:absolute;
      inset:0;
      display:flex;
      align-items:center;
      justify-content:center;
      gap:10px;
      border-radius:14px;
      background:var(--layer-solid);
      color:var(--pipi-cyan);
      font-size:.95em;
      letter-spacing:1px;
      opacity:0;
      visibility:hidden;
      pointer-events:none;
      transition:opacity .2s ease, visibility .2s ease;
      box-shadow:0 12px 28px var(--shadow-color);
    }
    .loading-overlay.show{
      opacity:1;
      visibility:visible;
      pointer-events:auto;
    }
    .loader-bars{display:inline-flex;gap:6px;align-items:center;}
    .loader-bars span{width:6px;height:14px;background:var(--pipi-cyan);animation:barDance 1s infinite;border-radius:3px;}
    .loader-bars span:nth-child(2){animation-delay:.2s;}
    .loader-bars span:nth-child(3){animation-delay:.4s;}
    .loader-text{white-space:nowrap;color:var(--text-main);}

    .btn-wrapper{
      width:70px;
      min-height:64px;
      display:flex;
      align-self:stretch;
    }
    .btn-send{
      width:100%; height:100%;
      border-radius:14px;
      border:1px solid var(--glass-stroke);
      background:linear-gradient(145deg,var(--pipi-cyan),#368ddc);
      color:#fff;
      cursor:pointer;
      display:flex;
      align-items:center;
      justify-content:center;
      transition:transform .2s, box-shadow .2s, background .3s;
      box-shadow:0 12px 26px var(--shadow-color);
      backdrop-filter:blur(10px);
    }
    .btn-send:hover{
      transform:translateY(-1px) scale(1.02);
      box-shadow:0 16px 30px var(--shadow-color-hover);
      background:linear-gradient(145deg,#5ac8fa,#368ddc);
    }
    .btn-send svg{width:24px;height:24px;fill:currentColor;}

    .btn-cancel{
      width:100%; height:100%;
      border-radius:14px;
      border:1px solid var(--glass-stroke);
      background:linear-gradient(145deg,var(--pipi-magenta),#6f5bd6);
      color:#fff;
      cursor:pointer;
      display:flex;
      align-items:center;
      justify-content:center;
      transition:transform .2s, box-shadow .2s, background .3s;
      box-shadow:0 12px 26px var(--shadow-color);
      backdrop-filter:blur(10px);
    }
    .btn-cancel:hover{
      transform:translateY(-1px) scale(1.02);
      box-shadow:0 16px 30px var(--shadow-color-hover);
      background:linear-gradient(145deg,#8f7df4,#6f5bd6);
    }
    .btn-cancel svg{width:22px;height:22px;fill:currentColor;}

    #qrcode-container{
      position:fixed; bottom:30px; left:30px; z-index:999;
      background:var(--glass-surface);
      padding:12px;
      border-radius:14px;
      box-shadow:0 14px 28px var(--shadow-color);
      border:1px solid var(--glass-stroke);
      display:flex;
      flex-direction:column;
      align-items:center;
      gap:8px;
      transition:transform .25s, box-shadow .25s;
      backdrop-filter:blur(10px);
    }
    #qrcode-container:hover{
      transform:translateY(-2px) scale(1.05);
      transform-origin:bottom left;
      box-shadow:0 18px 36px var(--shadow-color-hover);
    }
    #qrcode-container span{color:var(--text-main);font-size:10px;font-weight:800;font-family:sans-serif;}

    @media (max-width:600px){
      :root{font-size:15px;}
      body{padding:10px;}
      .header h1{font-size:1.4em; letter-spacing:1px;}
      .header-btn{width:38px; height:38px; font-size:1em;}
      .box{padding:15px;}
      .chat-box{padding:12px;}
      #qrcode-container{display:none;}
      .btn-wrapper{width:60px; min-height:60px;}
      textarea{padding:10px; font-size:.95em;}
    }

    @keyframes slideUp{from{opacity:0;transform:translateY(40px)}to{opacity:1;transform:translateY(0)}}
    @keyframes slideDown{from{opacity:0;transform:translateY(-40px)}to{opacity:1;transform:translateY(0)}}
    @keyframes msgPop{0%{opacity:0;transform:scale(.95) translateY(14px)}100%{opacity:1;transform:scale(1) translateY(0)}}
    @keyframes scanLight{0%{left:-100%}100%{left:200%}}
    @keyframes breatheBg{0%{transform:scale(1);opacity:.5}100%{transform:scale(1.1);opacity:.8}}
    @keyframes barDance{0%,100%{height:8px;background:var(--pipi-cyan)}50%{height:20px;background:var(--pipi-magenta)}}
    @keyframes shineText{0%{background-position:0% center}100%{background-position:200% center}}
    @keyframes glitch{0%,100%{transform:none}92%{transform:translate(1px,1px) skewX(2deg);filter:hue-rotate(90deg)}94%{transform:translate(-1px,-1px) skewX(-2deg)}96%{transform:translate(2px,0) skewX(0)}}
    
    .intro-text { line-height: 1.6; margin-bottom: 12px; }
    .intro-text strong { color: var(--pipi-cyan); font-weight: normal; }
    .cmd-suggestions { display: flex; flex-direction: column; gap: 10px; margin-top: 15px; }
    .cmd-item {
      background: var(--cmd-bg);
      border-left: 3px solid var(--pipi-cyan);
      padding: 12px 16px;
      border-radius: 6px;
      font-size: 0.9em;
      color: var(--text-main);
      cursor: pointer;
      transition: all 0.25s ease;
      position: relative;
    }
    .cmd-item:hover {
      background: var(--cmd-hover-bg);
      transform: translateX(6px);
      box-shadow: 0 4px 15px rgba(0,242,254,0.15);
      border-left-color: var(--pipi-magenta);
    }
    .cmd-item::before {
      content: ">_ ";
      color: var(--pipi-magenta);
      font-weight: bold;
      margin-right: 5px;
    }
  </style>
</head>

<body>
  <div id="qrcode-container" title="手机扫码中控">
    <div id="qrcode"></div>
    <span>手机扫码打开这个界面</span>
  </div>

  <div class="container">
    <div class="header">
      <button type="button" class="header-btn collapse-toggle" id="configToggle" onclick="toggleConfig()" aria-expanded="false" title="展开/收起设置">⚙</button>
      <h1>
        <img class="logo-mark" src="data:image/gif;base64,R0lGODlhAQABAAAAACw=" alt="PiPiClaw Logo" />
        <span>PiPiClaw</span>
      </h1>
      <button class="header-btn theme-toggle" onclick="toggleTheme()" aria-label="切换主题" title="切换深/浅色主题">
        <span id="theme-icon">🌙</span>
      </button>
    </div>

    <div class="box" id="terminalBox" style="animation-delay:.2s;">
      <h2><span style="color:var(--pipi-magenta);">⌨️</span> 交互终端 (Terminal)</h2>

      <div class="chat-box" id="chatBox">
        <div class="msg ai">
          <div class="msg-header">
            <img src="data:image/gif;base64,R0lGODlhAQABAAAAACw=" alt="PiPiClaw Logo" class="logo-badge" />
            皮皮虾 // 系统
          </div>
          <div class="msg-content">
            <div class="intro-text">
<strong>神经链接已建立。等待指令……</strong><br><br>
【简介与食用指南】<br>
这是一个能够全自动执行终端命令、读写文件、规划任务的本地 AI 智能体，能力不限于运维。<br>
只要像吩咐人类一样说话，它就会自己写脚本、查日志、执行系统命令或调用 Skill-Hub 上的一万+ 生态技能来帮你办事。<br><br>
<span style="color:var(--text-muted); font-size: 0.9em;">试试直接点击或粘贴以下命令：</span>
            </div>
            <div class="cmd-suggestions">
              <div class="cmd-item" onclick="document.getElementById('chatInput').value='帮我扫描一下当前目录，看有没有 C# 相关的源码文件';">帮我扫描一下当前目录，看有没有 C# 相关的源码文件</div>
              <div class="cmd-item" onclick="document.getElementById('chatInput').value='用 C# 写一个能控制树莓派 GPIO 针脚电平的简单脚本，并帮我运行它测试一下';">用 C# 写一个能控制树莓派 GPIO 针脚电平的简单脚本，并帮我运行它测试一下</div>
              <div class="cmd-item" onclick="document.getElementById('chatInput').value='帮我查一下系统当前的内存占用情况，并把结果写进 memory_log.txt';">帮我查一下系统当前的内存占用情况，并把结果写进 memory_log.txt</div>
              <div class="cmd-item" onclick="document.getElementById('chatInput').value='每天下午3点，帮我屏幕截图看一下我在干什么？';">每天下午3点，帮我屏幕截图看一下我在干什么？</div>
            </div>
          </div>
        </div>
      </div>

      <div class="input-area">
        <div class="input-main">
          <textarea id="chatInput" placeholder="输入任务指令... (按 Enter 发送)"></textarea>
          <div class="loading-overlay" id="loadingOverlay" aria-live="polite">
            <div class="loader-bars"><span></span><span></span><span></span></div>
            <span class="loader-text">正在深潜数据流...</span>
          </div>
        </div>
        <div class="btn-wrapper" id="sendWrapper">
          <button class="btn-send" type="button" onclick="sendMsg()" aria-label="Send">
            <svg viewBox="0 0 24 24"><path d="M2.01 21L23 12 2.01 3 2 10l15 2-15 2z"></path></svg>
          </button>
        </div>
        <div class="btn-wrapper" id="cancelWrapper" style="display:none;">
          <button class="btn-cancel" type="button" onclick="cancelTask()" aria-label="Cancel">
            <svg viewBox="0 0 24 24"><path d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/></svg>
          </button>
        </div>
      </div>
    </div>

    <div class="box" id="tasksBox" style="display:none; border-color:var(--pipi-magenta); animation-delay:.3s;">
      <h2><span style="color:var(--pipi-magenta);">⏰</span> 挂起与守护任务 (Daemon Tasks)</h2>
      <div id="tasksContainer"></div>
    </div>
  </div>

  <div class="modal" id="configModal" aria-hidden="true" role="dialog" aria-labelledby="configTitle">
    <div class="modal-dialog box" id="configBox" style="animation:none;">
      <div class="modal-header">
        <h2 id="configTitle"><span style="color:var(--pipi-cyan);">🛰️</span> 核心链路配置 (Config)</h2>
        <button type="button" class="header-btn modal-close" onclick="closeConfig()" aria-label="关闭设置">✖</button>
      </div>

      <div class="collapse-body" id="configBody">
        <div class="config-grid">
          <div class="config-row">
            <div class="form-group">
              <label>激活模型</label>
              <input type="text" id="model" placeholder="e.g. qwen3.5-plus" />
            </div>

            <div class="form-group">
              <label>密钥 (ApiKey)</label>
              <input type="password" id="apiKey" placeholder="sk-..." />
            </div>
          </div>

          <div class="config-row">
            <div class="form-group">
              <label>端点地址 (Endpoint)</label>
              <input type="text" id="endpoint" placeholder="https://..." />
            </div>

            <div class="form-group">
              <label>提权密码 (SudoPassword)</label>
              <input type="password" id="sudoPassword" placeholder="自动执行 sudo 时使用的密码" />
            </div>
          </div>

          <div class="config-row">
            <div class="form-group">
              <label>Web 端口 (WebPort)</label>
              <input type="number" id="webPort" placeholder="5050" min="1" max="65535" />
            </div>
          </div>
        </div>

        <button class="btn-submit" type="button" onclick="saveConfig()">保存并上传配置</button>
      </div>
    </div>
  </div>

  <script>
    const LOGO_DATA_URL = "{{LOGO_DATA_URL}}";

    // set all logos
    document.querySelectorAll('.logo-mark, .logo-badge').forEach(img => { img.src = LOGO_DATA_URL; });

    // === 主题切换逻辑 ===
    function toggleTheme() {
      const root = document.documentElement;
      const icon = document.getElementById('theme-icon');
      if (root.getAttribute('data-theme') === 'light') {
        root.removeAttribute('data-theme');
        if(icon) icon.innerText = '🌙';
        localStorage.setItem('theme', 'dark');
      } else {
        root.setAttribute('data-theme', 'light');
        if(icon) icon.innerText = '☀️';
        localStorage.setItem('theme', 'light');
      }
    }
    window.addEventListener('DOMContentLoaded', () => {
      if (localStorage.getItem('theme') === 'light') {
        const icon = document.getElementById('theme-icon');
        if(icon) icon.innerText = '☀️';
      }
    });

    const configModal = document.getElementById('configModal');
    const configBox = document.getElementById('configBox');
    const configBody = document.getElementById('configBody');
    const configToggle = document.getElementById('configToggle');

    function openConfig() {
      if (!configModal || !configToggle) return;
      configModal.classList.add('show');
      configModal.setAttribute('aria-hidden', 'false');
      configToggle.setAttribute('aria-expanded', 'true');
      configToggle.title = '收起设置';
      const firstInput = configBody?.querySelector('input');
      if (firstInput) firstInput.focus();
    }

    function closeConfig() {
      if (!configModal || !configToggle) return;
      configModal.classList.remove('show');
      configModal.setAttribute('aria-hidden', 'true');
      configToggle.setAttribute('aria-expanded', 'false');
      configToggle.title = '展开设置';
    }

    function toggleConfig() {
      if (!configModal) return;
      if (configModal.classList.contains('show')) closeConfig();
      else openConfig();
    }

    if (configModal) {
      configModal.addEventListener('click', (e) => {
        if (e.target === configModal) closeConfig();
      });
    }
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') closeConfig();
    });

    // QR
    const host = window.location.hostname;
    const targetUrl = (host === 'localhost' || host === '127.0.0.1')
      ? 'http://{{LAN_IP}}:{{WEB_PORT}}/'
      : window.location.href;

    if (typeof QRCode !== 'undefined') {
      new QRCode(document.getElementById("qrcode"), {
        text: targetUrl,
        width: 100,
        height: 100,
        colorDark : "#000000",
        colorLight : "#ffffff",
        correctLevel : QRCode.CorrectLevel.H
      });
    } else {
      const qrContainer = document.getElementById("qrcode-container");
      if (qrContainer) qrContainer.style.display = 'none';
    }

    document.getElementById('chatInput').addEventListener('keydown', function(e) {
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        sendMsg();
      }
    });

    function escapeHtml(s) {
      if (s == null) return '';
      return String(s)
        .replaceAll('&','&amp;')
        .replaceAll('<','&lt;')
        .replaceAll('>','&gt;')
        .replaceAll('"','&quot;')
        .replaceAll("'","&#39;");
    }

    async function loadConfig() {
      try {
        const res = await fetch('/api/config');
        if (!res.ok) return;
        const data = await res.json();
        document.getElementById('apiKey').value = data.ApiKey || '';
        document.getElementById('model').value = data.Model || '';
        document.getElementById('endpoint').value = data.Endpoint || '';
        document.getElementById('sudoPassword').value = data.SudoPassword || '';
        document.getElementById('webPort').value = data.WebPort || 5050;
      } catch {}
    }

    async function saveConfig() {
      const portVal = parseInt(document.getElementById('webPort').value) || 5050;
      if (portVal < 1 || portVal > 65535) {
        alert('端口号必须在 1 到 65535 之间');
        return;
      }
      const cfg = {
        ApiKey: document.getElementById('apiKey').value,
        Model: document.getElementById('model').value,
        Endpoint: document.getElementById('endpoint').value,
        SudoPassword: document.getElementById('sudoPassword').value,
        WebPort: portVal
      };

      const btn = document.querySelector('.btn-submit');
      const originalText = btn ? btn.innerHTML : '';

      try {
        if (btn) btn.innerHTML = '正在上传...';

        const res = await fetch('/api/config', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(cfg)
        });

        if (!res.ok) throw new Error('Config upload failed');

        const result = await res.json().catch(() => ({}));
        if (result.status === 'port_changed') {
          if (btn) {
            btn.style.background = 'var(--pipi-cyan)';
            btn.innerHTML = '✅ 配置已保存，端口变更需重启生效';
            setTimeout(() => { btn.style.background = ''; btn.innerHTML = originalText; }, 3500);
          }
        } else {
          if (btn) {
            btn.style.background = 'var(--pipi-magenta)';
            btn.innerHTML = '✅ 配置已同步';
            setTimeout(() => { btn.style.background = ''; btn.innerHTML = originalText; }, 1800);
          }
        }
      } catch (e) {
        if (btn) btn.innerHTML = originalText || '保存并上传配置';
        alert('配置上传失败：' + (e?.message || '通信中断'));
      }
    }

    async function fetchTasks() {
      try {
        const res = await fetch('/api/tasks');
        if (!res.ok) return;

        const tasks = await res.json();
        const container = document.getElementById('tasksContainer');
        const pending = (tasks || []).filter(t => t.status === 'pending');

        if (pending.length > 0) {
          document.getElementById('tasksBox').style.display = 'block';
          container.innerHTML = pending.map(t => `
            <div class="task-item" style="border-left:4px solid var(--pipi-magenta); padding:12px; margin-bottom:12px; background:rgba(255,0,127,0.05)">
              <div style="font-size:0.8em; color:var(--text-muted)">${escapeHtml(t.execute_at)}</div>
              <div style="font-weight:800">${escapeHtml(t.user_intent)}</div>
            </div>
          `).join('');
        } else {
          document.getElementById('tasksBox').style.display = 'none';
        }
      } catch {}
    }
    setInterval(fetchTasks, 1000);

    let currentAbortController = null;

    function setBusy(busy) {
      const sendWrapper = document.getElementById('sendWrapper');
      const cancelWrapper = document.getElementById('cancelWrapper');
      const overlay = document.getElementById('loadingOverlay');
      const input = document.getElementById('chatInput');

      if (sendWrapper) sendWrapper.style.display = busy ? 'none' : 'block';
      if (cancelWrapper) cancelWrapper.style.display = busy ? 'block' : 'none';
      if (overlay) overlay.classList.toggle('show', busy);
      if (input) {
        input.disabled = busy;
        if (busy) input.blur();
      }
    }

    async function cancelTask() {
      try { currentAbortController?.abort(); } catch {}
      try { await fetch('/api/cancel', { method: 'POST' }); } catch(e) { console.warn('[cancelTask] /api/cancel failed:', e); }
      setBusy(false);
    }

    async function sendMsg() {
      const input = document.getElementById('chatInput');
      if (!input || input.disabled) return;
      const text = (input.value || '').trim();
      if (!text) return;

      const chatBox = document.getElementById('chatBox');
      chatBox.innerHTML += `<div class="msg user"><div class="msg-header">我</div><div class="msg-content">${escapeHtml(text)}</div></div>`;
      input.value = '';
      chatBox.scrollTop = chatBox.scrollHeight;

      setBusy(true);

      const uniqueId = 'ai_' + Date.now();
      chatBox.innerHTML += `<div class="msg ai"><div class="msg-header">皮皮虾 // 工具</div><div class="msg-content" id="${uniqueId}"></div></div>`;
      const contentBox = document.getElementById(uniqueId);

      let currentTerminalBox = null;
      currentAbortController = new AbortController();

      try {
        const response = await fetch('/api/chat', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ message: text }),
          signal: currentAbortController.signal
        });

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (true) {
          const { value, done } = await reader.read();
          if (done) break;

          buffer += decoder.decode(value, { stream: true });
          const parts = buffer.split('|||END|||');
          buffer = parts.pop();

          for (const part of parts) {
            if (!part.trim()) continue;
            const data = JSON.parse(part.trim());

            if (data.type === 'tool' || data.type === 'tool_result') {
              if (!currentTerminalBox) {
                currentTerminalBox = document.createElement('div');
                currentTerminalBox.className = 'exec-terminal';
                contentBox.appendChild(currentTerminalBox);
              }
              if (data.type === 'tool') currentTerminalBox.innerHTML += `<span class="log-action">>> ${escapeHtml(data.content)}</span>`;
              else currentTerminalBox.innerHTML += `<span class="log-result">${escapeHtml(data.content)}</span>`;
              currentTerminalBox.scrollTop = currentTerminalBox.scrollHeight;
            }

            if (data.type === 'final') {
              const finalWrap = document.createElement('div');
              finalWrap.style.marginTop = '12px';
              contentBox.appendChild(finalWrap);

              const txt = String(data.content ?? '');
              let i = 0;
              const chunk = txt.length > 300 ? 4 : 2;

              const timer = setInterval(() => {
                finalWrap.appendChild(document.createTextNode(txt.substring(i, i + chunk)));
                i += chunk;
                chatBox.scrollTop = chatBox.scrollHeight;
                if (i >= txt.length) clearInterval(timer);
              }, 15);
            }

            chatBox.scrollTop = chatBox.scrollHeight;
          }
        }
      } catch (e) {
        // 'AbortError' is the expected error name when fetch is cancelled via AbortController
        if (e?.name !== 'AbortError') {
          const errWrap = document.createElement('div');
          errWrap.style.cssText = 'margin-top:8px; color:var(--pipi-magenta); font-size:0.85em;';
          errWrap.textContent = '[连接中断]';
          contentBox.appendChild(errWrap);
        }
      } finally {
        setBusy(false);
      }
    }

    async function loadHistory() {
      try {
        const res = await fetch('/api/history');
        if (!res.ok) return;
        const messages = await res.json();
        if (!Array.isArray(messages) || messages.length === 0) return;
        const chatBox = document.getElementById('chatBox');

        let currentAiContentBox = null;
        let currentTerminalBox = null;

        for (const msg of messages) {
          if (msg.role === 'user') {
            chatBox.insertAdjacentHTML('beforeend', `<div class="msg user"><div class="msg-header">我</div><div class="msg-content">${escapeHtml(msg.content)}</div></div>`);
            currentAiContentBox = null;
            currentTerminalBox = null;
          } else if (msg.role === 'assistant') {
            if (!currentAiContentBox) {
              const uniqueId = 'hist_' + Date.now() + '_' + Math.random().toString(36).substring(2, 7);
              chatBox.insertAdjacentHTML('beforeend', `<div class="msg ai"><div class="msg-header">皮皮虾 // 工具</div><div class="msg-content" id="${uniqueId}"></div></div>`);
              currentAiContentBox = document.getElementById(uniqueId);
              currentTerminalBox = null;
            }
            if (msg.tool_calls) {
              if (!currentTerminalBox) {
                currentTerminalBox = document.createElement('div');
                currentTerminalBox.className = 'exec-terminal';
                currentAiContentBox.appendChild(currentTerminalBox);
              }
              for (const tc of msg.tool_calls) {
                const actionSpan = document.createElement('span');
                actionSpan.className = 'log-action';
                const fnName = (tc.function && tc.function.name) ? tc.function.name : 'tool';
                const rawArgs = (tc.function && typeof tc.function.arguments === 'string') ? tc.function.arguments.trim() : '';
                const displayArgs = rawArgs && rawArgs !== '{}' ? rawArgs : '';
                actionSpan.textContent = `>> ${fnName}${displayArgs ? '(' + displayArgs + ')' : '()'}`;
                currentTerminalBox.appendChild(actionSpan);
              }
            }
            if (msg.content) {
              const finalWrap = document.createElement('div');
              if (currentTerminalBox) finalWrap.style.marginTop = '12px';
              finalWrap.textContent = msg.content;
              currentAiContentBox.appendChild(finalWrap);
              currentTerminalBox = null;
            }
          } else if (msg.role === 'tool') {
            if (currentTerminalBox && msg.content) {
              const resultSpan = document.createElement('span');
              resultSpan.className = 'log-result';
              resultSpan.textContent = msg.content;
              currentTerminalBox.appendChild(resultSpan);
            }
          }
        }

        chatBox.scrollTop = chatBox.scrollHeight;
      } catch(e) {
        console.warn('[loadHistory] failed:', e);
      }
    }

    loadConfig();
    loadHistory();
  </script>
</body>
</html>
""";

    return html;
}

                                  
string GetLogoDataUrl()
{
    const string logoDataUrl = "data:image/png;base64,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";
    return logoDataUrl;
}



// ========================== 14. 通用日志压缩器 ==========================
// ========================== 14. 通用日志压缩器 ==========================
public class UniversalLogCompressor
{
    public static List<string> CompressLogs(IEnumerable<string> rawLogs)
    {
        var compressedLogs = new List<string>(); var currentBlock = new List<string>();
        foreach (var rawLine in rawLogs)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            string line = StripAnsiFast(rawLine);
            if (currentBlock.Count == 0 || CalculateSimilarityFast(currentBlock.Last(), line) >= 0.7) { currentBlock.Add(line); }
            else { FlushBlock(compressedLogs, currentBlock); currentBlock.Clear(); currentBlock.Add(line); }
        }
        FlushBlock(compressedLogs, currentBlock); return compressedLogs;
    }

    // 纯手工的高速有限状态机 ANSI 清洗器，完美替代体积庞大的 Regex 引擎
    private static string StripAnsiFast(string input)
    {
        if (string.IsNullOrEmpty(input) || input.IndexOf('\x1B') == -1) return input;
        var sb = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '\x1B')
            {
                i++;
                if (i >= input.Length) break;
                char next = input[i];
                if (next == '[' || next == '(' || next == ')') 
                {
                    while (i < input.Length - 1)
                    {
                        i++;
                        char c = input[i];
                        if (c >= '@' && c <= '~') break;
                    }
                }
                continue;
            }
            sb.Append(input[i]);
        }
        return sb.ToString();
    }

    private static void FlushBlock(List<string> result, List<string> block)
    {
        if (block.Count == 0) return;
        if (block.Count <= 3) result.AddRange(block);
        else { result.Add(block.First()); result.Add($"    ... [PiPiClaw 折叠了 {block.Count - 2} 行相似输出以节约 Token] ..."); result.Add(block.Last()); }
    }

    private static double CalculateSimilarityFast(string s, string t)
    {
        if (s == t) return 1.0; if (s.Length == 0 || t.Length == 0) return 0.0;
        int max = Math.Max(s.Length, t.Length); if ((double)Math.Min(s.Length, t.Length) / max < 0.4) return 0.0; 
        int[] v0 = new int[t.Length + 1], v1 = new int[t.Length + 1];
        for (var i = 0; i < v0.Length; i++) v0[i] = i;
        for (var i = 0; i < s.Length; i++) {
            v1[0] = i + 1;
            for (var j = 0; j < t.Length; j++) v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + (s[i] == t[j] ? 0 : 1));
            for (var j = 0; j < v0.Length; j++) v0[j] = v1[j];
        }
        return 1.0 - ((double)v1[t.Length] / max);
    }
}

// ========================== 16. AOT Source Generator 实体类定义 ==========================
public class AppConfig {
    [JsonPropertyName("ApiKey")] public string ApiKey { get; set; } = "";
    [JsonPropertyName("Model")] public string Model { get; set; } = "";
    [JsonPropertyName("Endpoint")] public string Endpoint { get; set; } = "";
    [JsonPropertyName("SudoPassword")] public string SudoPassword { get; set; } = "";
    [JsonPropertyName("WebPort")] public int WebPort { get; set; } = 5050;
}

public class TaskItem {
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("execute_at")] public string ExecuteAt { get; set; } = "";
    [JsonPropertyName("user_intent")] public string UserIntent { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("interval_minutes")] public int IntervalMinutes { get; set; } = 0;
}

public class ChatMessage {
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Content { get; set; }
    [JsonPropertyName("name")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
    [JsonPropertyName("tool_call_id")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ToolCallId { get; set; }
    [JsonPropertyName("tool_calls")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ToolCall>? ToolCalls { get; set; }

    public ChatMessage DeepClone() {
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(this, AppJsonContext.Default.ChatMessage), AppJsonContext.Default.ChatMessage) ?? new ChatMessage();
    }
}

public class ToolCall {
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public ToolFunction Function { get; set; } = new();
}

public class ToolFunction {
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "";
}

public class LlmRequest {
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();
    [JsonPropertyName("tools")] public JsonElement Tools { get; set; }
    [JsonPropertyName("enable_search")] public bool EnableSearch { get; set; }
}

public class LlmResponse {
    [JsonPropertyName("choices")] public List<LlmChoice>? Choices { get; set; }
}

public class LlmChoice {
    [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
}

public class ChatReq {
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public class PushMsg {
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

// 这是 AOT 极限压缩的核心上下文注册：
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(List<TaskItem>))]
[JsonSerializable(typeof(TaskItem))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(LlmRequest))]
[JsonSerializable(typeof(LlmResponse))]
[JsonSerializable(typeof(ChatReq))]
[JsonSerializable(typeof(PushMsg))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppJsonContext : JsonSerializerContext {}
