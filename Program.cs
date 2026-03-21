using System;
using System.Diagnostics;
using System.IO;
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
        SudoPassword = ""
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
    { "type": "function", "function": { "name": "install_skill", "description": "安装 单个 Skill-hub 或者 从第三方的技能，并根据包含的 MD 文件自动了解对接方式。", "parameters": { "type": "object", "properties": { "slug": { "type": "string", "description": "技能列表中的slug字段只需传入这个字段即可" } }, "required": ["slug"] } } }
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

// 尝试恢复上次任务
if (File.Exists(checkpointPath))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Write("\n[PiPiClaw] 发现上次未完成的任务存档。是否继续执行上次的任务？(Y/N): ");
    var key = Console.ReadKey().Key;
    Console.WriteLine();
    Console.ResetColor();
    if (key == ConsoleKey.Y)
    {
        try
        {
            var savedState = JsonSerializer.Deserialize(File.ReadAllText(checkpointPath, Encoding.UTF8), AppJsonContext.Default.ListChatMessage);
            if (savedState != null) fullHistory = savedState;
            Console.WriteLine($"✅ 记忆已恢复！\n");
        }
        catch { File.Delete(checkpointPath); }
    }
    else
    {
        File.Delete(checkpointPath);
    }
}

using var client = new HttpClient();
client.Timeout = TimeSpan.FromMinutes(10); 
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

var agentLock = new SemaphoreSlim(1, 1);

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
            try 
            { 
                res = await client.PostAsync(GetConfig("Endpoint", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"), content); 
                res.EnsureSuccessStatusCode();
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
            
            var responseString = await res.Content.ReadAsStringAsync();
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
                        default:
                            result = fnName switch
                            {
                                "execute_command" => RunCmd(GetStrProp(tempArgs, "command"), GetBoolProp(tempArgs, "is_background")),
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

string RunCmd(string? cmd, bool isBackground = false)
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

    if (isBackground)
    {
        // ================= 后台非阻塞模式 =================
        // 丢入 Task.Run 剥离出主线程，大模型不会被卡死
        Task.Run(() =>
        {
            var consoleEncoding = new UTF8Encoding(false);
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo(isWin ? "cmd.exe" : "/bin/bash", isWin ? $"/c {cmd}" : $"-c \"{cmd}\"")
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
        // ================= 前台阻塞模式（原逻辑） =================
        var consoleEncoding = new UTF8Encoding(false);
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(isWin ? "cmd.exe" : "/bin/bash", isWin ? $"/c {cmd}" : $"-c \"{cmd}\"")
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
        
        try { p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); p.WaitForExit(); } 
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

async Task StartWebManager()
{
    var listener = new HttpListener();
    listener.Prefixes.Add("http://+:5050/");
    try
    {
        listener.Start();
        string lanIp = GetLocalIpAddress();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Web UI] 网页控制台已启动:\n  - 本机访问: http://localhost:5050 \n  - 手机扫码或局域网: http://{lanIp}:5050");
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
                    SudoPassword = GetConfig("SudoPassword", "")
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
                if (newCfg != null)
                {
                    File.WriteAllText("appsettings.json", JsonSerializer.Serialize(newCfg, AppJsonContext.Default.AppConfig), Encoding.UTF8);
                    sudoPassword = newCfg.SudoPassword ?? "";
                }
                res.StatusCode = 200;
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
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no">
                <title>PiPiClaw // SkillHub Ready C&C Terminal v3.0</title>
               <script src="https://cdn.jsdelivr.net/npm/qrcodejs@1.0.0/qrcode.min.js"></script>
               <style>
                   /* ================= 核心变量与极暗底色 ================= */
                    :root {
                        --bg-depth: #030405;
                        --bg-card: rgba(10, 14, 20, 0.7);
                        --pipi-magenta: #ff007f;
                        --pipi-cyan: #00f2fe;    
                        --text-main: #e0e6ed;
                        --text-muted: #8b949e;
                        --border-glow: rgba(255, 0, 127, 0.4);
                        --cyan-glow: rgba(0, 242, 254, 0.4);
                        --font-mono: 'Fira Code', 'Consolas', 'Courier New', monospace;
                    }

                   /* ================= 全局动画背景 ================= */
                     body { 
                         font-family: var(--font-mono); 
                         background-color: var(--bg-depth); 
                        color: var(--text-main); 
                        margin: 0; padding: 20px; 
                        display: flex; flex-direction: column; align-items: center; 
                        overflow-x: hidden; position: relative;
                        min-height: 100vh;
                        text-size-adjust: 100%;
                        -webkit-text-size-adjust: 100%;
                    }
                   /* 扫描线与噪点 */
                   body::before {
                       content: ""; position: fixed; top: 0; left: 0; width: 100%; height: 100%;
                       background: repeating-linear-gradient(0deg, rgba(0,0,0,0.15), rgba(0,0,0,0.15) 1px, transparent 1px, transparent 2px);
                       pointer-events: none; z-index: 10; opacity: 0.6;
                   }
                   /* 深海呼吸光斑 */
                   body::after {
                       content: ""; position: fixed; top: 0; left: 0; width: 100%; height: 100%;
                       background: radial-gradient(circle at 50% 50%, rgba(0, 242, 254, 0.05) 0%, transparent 60%),
                                   radial-gradient(circle at 80% 20%, rgba(255, 0, 127, 0.05) 0%, transparent 50%);
                       z-index: -1; animation: breatheBg 8s infinite alternate ease-in-out;
                   }

                   .container { width: 100%; max-width: 1000px; z-index: 2; display: flex; flex-direction: column; gap: 20px; }

                   /* ================= 头部动画 ================= */
                   .header { text-align: center; margin-bottom: 10px; position: relative; animation: slideDown 0.8s ease-out; }
                    .header h1 { 
                        font-size: 3em; font-weight: 900; margin: 0; text-transform: uppercase; letter-spacing: 4px;
                        background: linear-gradient(90deg, var(--pipi-cyan), #fff, var(--pipi-magenta), var(--pipi-cyan));
                        background-size: 200% auto;
                        -webkit-background-clip: text; -webkit-text-fill-color: transparent;
                        filter: drop-shadow(0 0 15px rgba(255, 255, 255, 0.2));
                        animation: shineText 3s linear infinite, glitch 4s infinite;
                        display: inline-flex; align-items: center; gap: 12px;
                    }
                   .header p { color: var(--text-muted); font-size: 0.9em; margin-top: 5px; opacity: 0.8; letter-spacing: 1px; }
                   
                   /* ================= 卡片悬浮与进场 ================= */
                   .box { 
                       background: var(--bg-card); padding: 25px; border-radius: 8px; 
                       border: 1px solid rgba(48, 54, 61, 0.5);
                       box-shadow: 0 10px 30px rgba(0,0,0,0.8), inset 0 0 20px rgba(0, 242, 254, 0.02);
                       position: relative; overflow: hidden; backdrop-filter: blur(10px);
                       animation: slideUp 0.6s ease-out both; transition: transform 0.3s, box-shadow 0.3s;
                   }
                   .box:hover { box-shadow: 0 10px 40px rgba(0,0,0,0.9), inset 0 0 20px rgba(0, 242, 254, 0.05); }
                   .box::before {
                       content: ''; position: absolute; top: 0; left: -100%; width: 50%; height: 2px;
                       background: linear-gradient(90deg, transparent, var(--pipi-cyan), var(--pipi-magenta), transparent);
                       animation: scanLight 4s infinite linear;
                   }

                    h2 { margin-top: 0; color: #fff; font-size: 1.1em; text-transform: uppercase; letter-spacing: 1px; display: flex; align-items: center; gap: 10px; }
                    h2::before { content: ''; width: 6px; height: 18px; background: var(--pipi-cyan); box-shadow: 0 0 10px var(--pipi-cyan); border-radius: 3px; }

                    .logo-heading { display: inline-flex; align-items: center; gap: 12px; justify-content: center; }
                    .logo-mark { width: 42px; height: 42px; object-fit: contain; vertical-align: middle; filter: drop-shadow(0 0 8px rgba(0, 242, 254, 0.25)); }
                    .logo-badge { width: 22px; height: 22px; object-fit: contain; vertical-align: middle; }

                   /* ================= 输入框动效 ================= */
                    .config-grid { display: grid; grid-template-columns: 1fr; gap: 15px; }
                    .config-row-1 { display: grid; grid-template-columns: 1fr 1fr; gap: 15px; }
                    .config-row-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 15px; }
                    @media (max-width: 600px) { .config-row-1, .config-row-2 { grid-template-columns: 1fr; } }

                    .collapsible .collapse-header { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
                    .collapse-toggle { background: transparent; color: var(--pipi-cyan); border: 1px solid var(--pipi-cyan); padding: 8px 12px; border-radius: 6px; cursor: pointer; font-weight: bold; letter-spacing: 1px; transition: all 0.2s; }
                    .collapse-toggle:hover { background: rgba(0, 242, 254, 0.1); box-shadow: 0 0 12px rgba(0, 242, 254, 0.25); }
                    .collapse-toggle:active { transform: translateY(1px); }
                    .collapse-body { margin-top: 15px; }
                    .collapsible.collapsed .collapse-body { display: none; }
                   
                   label { display: block; color: var(--text-muted); font-size: 0.75em; margin-bottom: 5px; text-transform: uppercase; letter-spacing: 1px; }
                   .input-wrapper { position: relative; width: 100%; }
                   input { 
                       width: 100%; box-sizing: border-box; padding: 12px 15px; border-radius: 6px;
                       border: 1px solid rgba(255, 255, 255, 0.1); background: rgba(0,0,0,0.5); 
                       color: #fff; font-family: var(--font-mono); font-size: 0.95em; transition: all 0.3s;
                   }
                   input:focus { outline: none; border-color: var(--pipi-cyan); box-shadow: 0 0 15px rgba(0, 242, 254, 0.2); background: rgba(0,0,0,0.8); }

                   .btn-submit { 
                       background: rgba(0, 242, 254, 0.1); border: 1px solid var(--pipi-cyan);
                       color: var(--pipi-cyan); padding: 12px 20px; border-radius: 6px; 
                       font-weight: bold; font-family: var(--font-mono); cursor: pointer; 
                       width: 100%; transition: all 0.3s cubic-bezier(0.25, 0.8, 0.25, 1); margin-top: 15px;
                       font-size: 0.9em; text-transform: uppercase; letter-spacing: 2px;
                       position: relative; overflow: hidden;
                   }
                   .btn-submit::after {
                       content: ''; position: absolute; top: -50%; left: -50%; width: 200%; height: 200%;
                       background: radial-gradient(circle, rgba(255,255,255,0.2) 0%, transparent 60%);
                       transform: scale(0); transition: transform 0.5s;
                   }
                   .btn-submit:hover { 
                       background: var(--pipi-cyan); color: #000; 
                       box-shadow: 0 0 25px rgba(0, 242, 254, 0.6); transform: translateY(-2px);
                   }
                   .btn-submit:active::after { transform: scale(1); transition: 0s; }
                   .btn-submit:active { transform: translateY(0); }

                   /* ================= 终端对话框 ================= */
                   .chat-box { 
                       height: 55vh; min-height: 400px; overflow-y: auto; background: rgba(0,0,0,0.4); 
                       padding: 20px; border: 1px solid rgba(255, 255, 255, 0.05); border-radius: 8px;
                       display: flex; flex-direction: column; gap: 20px; scroll-behavior: smooth;
                       box-shadow: inset 0 0 30px rgba(0,0,0,0.8);
                   }
                   .chat-box::-webkit-scrollbar { width: 6px; }
                   .chat-box::-webkit-scrollbar-track { background: rgba(0,0,0,0.2); border-radius: 3px; }
                   .chat-box::-webkit-scrollbar-thumb { background: rgba(0, 242, 254, 0.3); border-radius: 3px; transition: background 0.3s; }
                   .chat-box::-webkit-scrollbar-thumb:hover { background: var(--pipi-cyan); }

                   .msg { animation: msgPop 0.4s cubic-bezier(0.175, 0.885, 0.32, 1.275) both; position: relative; max-width: 90%; display: flex; flex-direction: column; }
                   .msg-header { font-size: 0.75em; margin-bottom: 6px; opacity: 0.8; display: flex; align-items: center; gap: 8px; text-transform: uppercase; font-weight: bold; letter-spacing: 1px; }
                   
                   .msg.user { align-self: flex-end; }
                   .msg.user .msg-header { color: var(--pipi-cyan); justify-content: flex-end; }
                   .msg.user .msg-content { 
                       background: linear-gradient(135deg, rgba(0, 242, 254, 0.15), rgba(0, 150, 255, 0.05));
                       border: 1px solid rgba(0, 242, 254, 0.3); color: #fff;
                       padding: 12px 18px; border-radius: 12px 12px 0 12px;
                       box-shadow: 0 5px 15px rgba(0,0,0,0.3); white-space: pre-wrap; word-break: break-all;
                   }
                   
                   .msg.ai { align-self: flex-start; width: 100%; }
                   .msg.ai .msg-header { color: var(--pipi-magenta); }
                   .msg.ai .msg-content { 
                       background: rgba(255, 0, 127, 0.03); color: #fff;
                       border: 1px solid rgba(255, 0, 127, 0.2);
                       padding: 12px 18px; border-radius: 12px 12px 12px 0;
                       box-shadow: 0 5px 15px rgba(0,0,0,0.3); white-space: pre-wrap; word-break: break-all;
                   }

                   /* ================= 终端执行日志固定区域 ================= */
                   .exec-terminal {
                       background: #050a0f; border: 1px solid rgba(0, 255, 128, 0.3); border-radius: 6px;
                       padding: 10px; margin: 10px 0; font-family: var(--font-mono); font-size: 0.75em; 
                       color: #0f0; height: 160px; overflow-y: auto; 
                       box-shadow: inset 0 0 15px #000; text-shadow: 0 0 2px rgba(0, 255, 0, 0.5);
                       scroll-behavior: smooth;
                   }
                   .exec-terminal::-webkit-scrollbar { width: 4px; }
                   .exec-terminal::-webkit-scrollbar-thumb { background: rgba(0, 255, 128, 0.5); border-radius: 2px; }
                   .exec-terminal .log-action { color: #0ff; font-weight: bold; margin-bottom: 4px; display: block; border-bottom: 1px dashed rgba(0, 255, 255, 0.2); padding-bottom: 2px;}
                   .exec-terminal .log-result { color: #8b949e; display: block; margin-bottom: 10px; padding-left: 8px; border-left: 2px solid rgba(139, 148, 158, 0.3); }

                   /* ================= 输入区 ================= */
                   .input-area { 
                       display: flex; gap: 12px; align-items: flex-end; 
                       margin-top: 20px; border-top: 1px solid rgba(255, 255, 255, 0.05); padding-top: 20px; 
                   }
                   textarea {
                       flex: 1; height: 60px; min-height: 60px; max-height: 180px;
                       background: rgba(0,0,0,0.5); border: 1px solid rgba(255, 255, 255, 0.1);
                       color: #fff; font-family: var(--font-mono); font-size: 1em; line-height: 1.4;
                       padding: 15px; border-radius: 12px; resize: vertical; outline: none;
                       transition: all 0.3s; box-shadow: inset 0 4px 10px rgba(0,0,0,0.5);
                   }
                   textarea:focus { 
                       border-color: var(--pipi-cyan); background: rgba(0,0,0,0.8);
                       box-shadow: inset 0 4px 10px rgba(0,0,0,0.5), 0 0 20px rgba(0, 242, 254, 0.15); 
                   }
                   
                   .btn-wrapper { position: relative; width: 70px; height: 60px; }
                   .btn-send { 
                       width: 100%; height: 100%; border-radius: 12px; border: none; outline: none;
                       background: linear-gradient(135deg, var(--pipi-cyan), #0072ff);
                       color: #fff; font-size: 1.5em; cursor: pointer;
                       display: flex; align-items: center; justify-content: center;
                       transition: all 0.3s cubic-bezier(0.175, 0.885, 0.32, 1.275);
                       box-shadow: 0 5px 15px rgba(0, 242, 254, 0.3), inset 0 2px 5px rgba(255,255,255,0.4);
                       z-index: 2; position: relative;
                   }
                   .btn-send::before {
                       content: ''; position: absolute; top: 0; left: 0; right: 0; bottom: 0;
                       background: linear-gradient(135deg, var(--pipi-magenta), #ff8a00);
                       border-radius: 12px; opacity: 0; transition: opacity 0.3s; z-index: -1;
                   }
                   .btn-send:hover { transform: translateY(-4px) scale(1.05); box-shadow: 0 10px 25px rgba(255, 0, 127, 0.5); }
                   .btn-send:hover::before { opacity: 1; }
                   .btn-send svg { width: 24px; height: 24px; fill: currentColor; transition: transform 0.3s; }

                   .loader-wrapper { display: none; margin: 15px auto; text-align: center; color: var(--pipi-cyan); font-size: 0.8em; text-transform: uppercase; letter-spacing: 2px; }
                   .loader-bars { display: inline-flex; gap: 4px; align-items: center; margin-right: 10px; }
                   .loader-bars span { width: 4px; height: 12px; background: var(--pipi-cyan); animation: barDance 1s infinite; border-radius: 2px; }
                   .loader-bars span:nth-child(2) { animation-delay: 0.2s; }
                   .loader-bars span:nth-child(3) { animation-delay: 0.4s; }

                   /* ================= 二维码悬浮 (左下角) ================= */
                   #qrcode-container {
                       position: fixed; bottom: 30px; left: 30px; z-index: 999;
                       background: rgba(255, 255, 255, 0.95); padding: 12px;
                       border-radius: 12px; box-shadow: 0 0 30px var(--cyan-glow);
                       display: flex; flex-direction: column; align-items: center; gap: 8px;
                       transition: transform 0.3s cubic-bezier(0.175, 0.885, 0.32, 1.275);
                   }
                   #qrcode-container:hover { transform: scale(1.6); transform-origin: bottom left; }
                   #qrcode-container span { color: #000; font-size: 10px; font-weight: bold; font-family: sans-serif; }

                   /* ================= 移动端专项适配 ================= */
                    @media (max-width: 600px) {
                        :root { font-size: 15px; }
                        body { padding: 10px; font-size: 0.95em; text-size-adjust: 95%; -webkit-text-size-adjust: 95%; }
                        .header h1 { font-size: 1.6em; letter-spacing: 1.5px; }
                        h2 { font-size: 1em; }
                        .box { padding: 15px; }
                        .chat-box { height: 58vh; padding: 12px; }
                        #qrcode-container { display: none; } /* 手机已打开，无需二维码 */
                        .btn-wrapper { width: 60px; }
                        textarea { padding: 10px; font-size: 0.9em; }
                        input { font-size: 1em; }
                        .btn-submit { font-size: 0.85em; }
                        .msg-header, .exec-terminal { font-size: 0.7em; }
                    }

                   /* ================= Animations ================= */
                   @keyframes slideUp { from { opacity: 0; transform: translateY(40px); } to { opacity: 1; transform: translateY(0); } }
                   @keyframes slideDown { from { opacity: 0; transform: translateY(-40px); } to { opacity: 1; transform: translateY(0); } }
                   @keyframes msgPop { 0% { opacity: 0; transform: scale(0.9) translateY(20px); } 100% { opacity: 1; transform: scale(1) translateY(0); } }
                   @keyframes scanLight { 0% { left: -100%; } 100% { left: 200%; } }
                   @keyframes breatheBg { 0% { transform: scale(1); opacity: 0.5; } 100% { transform: scale(1.1); opacity: 0.8; } }
                   @keyframes barDance { 0%, 100% { height: 8px; background: var(--pipi-cyan); } 50% { height: 20px; background: var(--pipi-magenta); box-shadow: 0 0 10px var(--pipi-magenta); } }
                   @keyframes shineText { 0% { background-position: 0% center; } 100% { background-position: 200% center; } }
                   @keyframes glitch { 0%, 100% { transform: none; } 92% { transform: translate(1px, 1px) skewX(2deg); filter: hue-rotate(90deg); } 94% { transform: translate(-1px, -1px) skewX(-2deg); } 96% { transform: translate(2px, 0px) skewX(0); } }
               </style>
           </head>
           <body>
               <div id="qrcode-container" title="手机扫码中控">
                   <div id="qrcode"></div>
                   <span>手机扫码打开这个界面</span>
               </div>

                 <div class="container">
                     <div class="header">
                         <h1><img class="logo-mark" src="data:image/gif;base64,R0lGODlhAQABAAAAACw=" alt="PiPiClaw Logo"> <span>PiPiClaw</span></h1>
                         <p>SkillHub-ready general agent // Terminal v3.0</p>
                     </div>

                    <div class="box collapsible" id="configBox" style="animation-delay: 0.1s;">
                        <div class="collapse-header">
                            <h2><span style="color: var(--pipi-cyan);">🛰️</span> 核心链路配置 (Config)</h2>
                            <button type="button" class="collapse-toggle" id="configToggle" onclick="toggleConfig()" aria-expanded="true">收起设置 ▾</button>
                        </div>
                        <div class="collapse-body" id="configBody">
                            <div class="config-grid">
                                <div class="config-row-1">
                                    <div class="form-group">
                                        <label>激活模型</label>
                                        <input type="text" id="model" placeholder="e.g. qwen3.5-plus">
                                    </div>
                                    <div class="form-group">
                                        <label>密钥 (ApiKey)</label>
                                        <input type="password" id="apiKey" placeholder="sk-...">
                                    </div>
                                </div>
                                <div class="config-row-2">
                                    <div class="form-group">
                                        <label>端点地址 (Endpoint)</label>
                                        <input type="text" id="endpoint" placeholder="https://...">
                                    </div>
                                     <div class="form-group">
                                         <label>提权密码 (SudoPassword)</label>
                                         <input type="password" id="sudoPassword" placeholder="自动执行 sudo 时使用的密码">
                                     </div>
                                </div>
                            </div>
                            <button class="btn-submit" onclick="saveConfig()">保存并上传配置</button>
                        </div>
                    </div>
                   
                   <div class="box" style="animation-delay: 0.2s;">
                       <h2><span style="color:var(--pipi-magenta);">⌨️</span> 交互终端 (Terminal)</h2>
                            <div class="chat-container">
                                <div class="chat-box" id="chatBox">
                                 <div class="msg ai">
                                     <div class="msg-header"><img src="data:image/gif;base64,R0lGODlhAQABAAAAACw=" alt="PiPiClaw Logo" class="logo-badge" /> 皮皮虾 // 系统</div>
                                     <div class="msg-content">神经链接已建立。等待指令……<br><br>
                                    <div style="color: var(--pipi-cyan); font-weight: bold; margin-bottom: 8px;">【简介与食用指南】</div>
                                    <div>
            这是一个能够全自动执行终端命令、读写文件、规划任务的本地 AI 智能体，能力不限于运维。<br>
            只要像吩咐人类一样说话，它就会自己写脚本、查日志、执行系统命令或调用 Skill-Hub 上的一万+ 生态技能来帮你办事。<br><br>
            💡 试试直接粘贴以下命令 (傻瓜式案例)：<br>
            <span style="color: var(--text-muted); line-height: 1.6;">
            1. "帮我扫描一下当前目录，看有没有 C# 相关的源码文件"<br>
            2. "用 C# 写一个能控制树莓派 GPIO 针脚电平的简单脚本，并帮我运行它测试一下"<br>
            3. "帮我查一下系统当前的内存占用情况，并把结果写进 memory_log.txt"<br>
            4. "每天下午3点，帮我屏幕截图看一下我在干什么？"
            </span>
                                   </div>
                                   </div>
                               </div>
                           </div>
                           
                           <div class="loader-wrapper" id="loading">
                               <div class="loader-bars"><span></span><span></span><span></span></div>
                               正在深潜数据流...
                           </div>
                           
                           <div class="input-area">
                               <textarea id="chatInput" placeholder="输入任务指令... (按 Enter 发送)"></textarea>
                               <div class="btn-wrapper">
                                   <button class="btn-send" onclick="sendMsg()">
                                       <svg viewBox="0 0 24 24"><path d="M2.01 21L23 12 2.01 3 2 10l15 2-15 2z"/></svg>
                                   </button>
                               </div>
                           </div>
                       </div>
                   </div>

                   <div class="box" id="tasksBox" style="display:none; border-color: var(--pipi-magenta); animation-delay: 0.3s;">
                       <h2><span style="color: var(--pipi-magenta);">⏰</span> 挂起与守护任务 (Daemon Tasks)</h2>
                       <div id="tasksContainer"></div>
                   </div>
                </div>

                <script>
                    const LOGO_DATA_URL = "{{LOGO_DATA_URL}}";
                    const configBox = document.getElementById('configBox');
                    const configBody = document.getElementById('configBody');
                    const configToggle = document.getElementById('configToggle');

                    document.querySelectorAll('.logo-mark, .logo-badge').forEach(img => { img.src = LOGO_DATA_URL; });

                    function setConfigCollapsed(collapsed) {
                        if (!configBox || !configBody || !configToggle) return;
                        if (collapsed) {
                            configBox.classList.add('collapsed');
                            configBody.style.display = 'none';
                            configToggle.innerText = '展开设置 ▸';
                            configToggle.setAttribute('aria-expanded', 'false');
                        } else {
                            configBox.classList.remove('collapsed');
                            configBody.style.display = 'block';
                            configToggle.innerText = '收起设置 ▾';
                            configToggle.setAttribute('aria-expanded', 'true');
                        }
                    }

                    function toggleConfig() {
                        if (!configBox) return;
                        setConfigCollapsed(!configBox.classList.contains('collapsed'));
                    }

                    setConfigCollapsed(window.innerWidth < 720);

                    let host = window.location.hostname;
                    let targetUrl = (host === 'localhost' || host === '127.0.0.1') ? 'http://{{LAN_IP}}:5050/' : window.location.href;
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

                   async function loadConfig() {
                       try {
                           let res = await fetch('/api/config');
                           let data = await res.json();
                           document.getElementById('apiKey').value = data.ApiKey || '';
                           document.getElementById('model').value = data.Model || '';
                           document.getElementById('endpoint').value = data.Endpoint || '';
                           document.getElementById('sudoPassword').value = data.SudoPassword || '';
                       } catch(e) {}
                   }

                   async function saveConfig() {
                       let cfg = { 
                           ApiKey: document.getElementById('apiKey').value, 
                           Model: document.getElementById('model').value,
                           Endpoint: document.getElementById('endpoint').value,
                           SudoPassword: document.getElementById('sudoPassword').value
                       };
                       try {
                           const btn = document.querySelector('.btn-submit'); 
                           let originalText = btn.innerHTML;
                           btn.innerHTML = '正在上传...';
                           await fetch('/api/config', { method: 'POST', body: JSON.stringify(cfg) });
                           setTimeout(() => { 
                               btn.style.background = 'var(--pipi-magenta)';
                               btn.innerHTML = '✅ 配置已同步'; 
                               setTimeout(() => { btn.style.background = ''; btn.innerHTML = originalText; }, 2000); 
                           }, 500);
                       } catch(e) { alert('通信中断'); }
                   }

                   async function fetchTasks() {
                       try {
                           let res = await fetch('/api/tasks');
                           let tasks = await res.json();
                           let container = document.getElementById('tasksContainer');
                           let pending = tasks.filter(t => t.status === 'pending');
                           if(pending.length > 0) {
                               document.getElementById('tasksBox').style.display = 'block';
                               container.innerHTML = pending.map(t => `
                                   <div class="task-item" style="border-left:4px solid var(--pipi-magenta); padding:12px; margin-bottom:12px; background:rgba(255,0,127,0.05)">
                                       <div style="font-size:0.8em; color:var(--text-muted)">${t.execute_at}</div>
                                       <div style="font-weight:bold">${t.user_intent}</div>
                                   </div>`).join('');
                           } else { document.getElementById('tasksBox').style.display = 'none'; }
                       } catch(e) {}
                   }
                   setInterval(fetchTasks, 1000);

                   function escapeHtml(unsafe) {
                       return unsafe.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
                   }

                   async function sendMsg() {
                       let input = document.getElementById('chatInput');
                       let text = input.value.trim();
                       if (!text) return;
                       
                       let chatBox = document.getElementById('chatBox');
                       chatBox.innerHTML += `<div class="msg user"><div class="msg-header">我</div><div class="msg-content">${escapeHtml(text)}</div></div>`;
                       input.value = '';
                       chatBox.scrollTop = chatBox.scrollHeight;
                       document.getElementById('loading').style.display = 'block';

                       const uniqueId = 'ai_' + Date.now();
                       chatBox.innerHTML += `<div class="msg ai"><div class="msg-header">皮皮虾 // 工具</div><div class="msg-content" id="${uniqueId}"></div></div>`;
                       let contentBox = document.getElementById(uniqueId);
                       let currentTerminalBox = null;

                       try {
                           const response = await fetch('/api/chat', { 
                               method: 'POST', 
                               headers: { 'Content-Type': 'application/json' },
                               body: JSON.stringify({ message: text }) 
                           });
                           document.getElementById('loading').style.display = 'none';
                           const reader = response.body.getReader();
                           const decoder = new TextDecoder();
                           let buffer = '';

                           while(true) {
                               const {value, done} = await reader.read();
                               if(done) break;
                               buffer += decoder.decode(value, {stream: true});
                               let parts = buffer.split('|||END|||');
                               buffer = parts.pop(); 
                               for(let part of parts) {
                                   if(!part.trim()) continue;
                                   let data = JSON.parse(part.trim());
                                   if(data.type === 'tool' || data.type === 'tool_result') {
                                       if(!currentTerminalBox) {
                                           currentTerminalBox = document.createElement('div');
                                           currentTerminalBox.className = 'exec-terminal';
                                           contentBox.appendChild(currentTerminalBox);
                                       }
                                       if (data.type === 'tool') currentTerminalBox.innerHTML += `<span class="log-action">>> ${data.content}</span>`;
                                       else currentTerminalBox.innerHTML += `<span class="log-result">${escapeHtml(data.content)}</span>`;
                                       currentTerminalBox.scrollTop = currentTerminalBox.scrollHeight;
                                   } else if (data.type === 'final') {
                                       let finalWrap = document.createElement('div');
                                       finalWrap.style.marginTop = '15px';
                                       contentBox.appendChild(finalWrap);
                                       let i = 0, txt = data.content, chunk = txt.length > 300 ? 4 : 2;
                                       let timer = setInterval(() => {
                                           finalWrap.appendChild(document.createTextNode(txt.substring(i, i + chunk)));
                                           i += chunk;
                                           chatBox.scrollTop = chatBox.scrollHeight;
                                           if (i >= txt.length) clearInterval(timer);
                                       }, 15);
                                   }
                                   chatBox.scrollTop = chatBox.scrollHeight;
                               }
                           }
                       } catch(e) { document.getElementById('loading').style.display = 'none'; }
                   }
                   loadConfig();
               </script>
           </body>
            </html>
    """;

    return html;
}

string GetLogoDataUrl()
{
    try
    {
        string logoPath = Path.Combine(AppContext.BaseDirectory, "IMG_0868.png");
        if (File.Exists(logoPath))
        {
            var bytes = File.ReadAllBytes(logoPath);
            return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        }
    }
    catch { }
    return "data:image/gif;base64,R0lGODlhAQABAAAAACw=";
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
