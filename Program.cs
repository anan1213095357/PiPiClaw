using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.NetworkInformation;
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !Console.IsOutputRedirected)
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
AppConfig GlobalConfig = new();

// ========================== 1. 基础配置与初始化 ==========================
if (!File.Exists("appsettings.json"))
{
    // 初次运行生成默认配置
    File.WriteAllText("appsettings.json", JsonSerializer.Serialize(GlobalConfig, AppJsonContext.Default.AppConfig), Encoding.UTF8);
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("检测到首次运行，已自动生成默认的 appsettings.json 文件。");
    Console.ResetColor();
}
else
{
    try
    {
        var json = File.ReadAllText("appsettings.json", Encoding.UTF8);
        var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
        if (cfg != null) GlobalConfig = cfg;
    }
    catch { /* 忽略解析错误，使用默认值 */ }
}
string GetConfig(string key, string def = "")
{
    var envValue = Environment.GetEnvironmentVariable(key);
    if (envValue != null) return envValue;
    var firstModel = (GlobalConfig.Models != null && GlobalConfig.Models.Count > 0)
        ? GlobalConfig.Models[0]
        : new ModelConfig();
    return key switch
    {
        "ApiKey" => !string.IsNullOrEmpty(firstModel.ApiKey) ? firstModel.ApiKey : def,
        "Model" => !string.IsNullOrEmpty(firstModel.Model) ? firstModel.Model : def,
        "Endpoint" => !string.IsNullOrEmpty(firstModel.Endpoint) ? firstModel.Endpoint : def,
        "SudoPassword" => GlobalConfig.SudoPassword ?? def,
        "WebPort" => GlobalConfig.WebPort > 0 ? GlobalConfig.WebPort.ToString() : def,
        "SkillHubSearchUrl" => !string.IsNullOrEmpty(GlobalConfig.SkillHubSearchUrl) ? GlobalConfig.SkillHubSearchUrl : def,
        _ => def
    };
}
// 新增热重载函数
void ReloadConfig()
{
    try
    {
        if (File.Exists("appsettings.json"))
        {
            var json = File.ReadAllText("appsettings.json", Encoding.UTF8);
            var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
            if (cfg != null) GlobalConfig = cfg;
        }
    }
    catch { /* 忽略解析错误，避免配置文件损坏时程序崩溃 */ }
}
// ========================== 2. 初始化 Tools (大模型工具箱) ==========================
var toolsDoc = JsonDocument.Parse("""
[
    { "type": "function", "function": { "name": "execute_command", "description": "执行终端命令", "parameters": { "type": "object", "properties": { "command": { "type": "string" }, "is_background": { "type": "boolean", "description": "【注意，生死攸关的判断】请严格按以下规则选择：\n1. 必须设为 true (后台)：适用于【永远不会自动退出】或【启动常驻服务/UI】或【启动浏览器自动化】等等的命令。例如：启动 Web 服务器、数据库守护进程、打开浏览器及UI自动化(如 agent-browser/chrome)、死循环脚本。这类任务必须丢入后台，否则你会把自己永久卡死！\n2. 必须设为 false (前台)：适用于【执行完会自动结束】并且你需要查看最终输出结果的命令。例如：环境部署(npm install, pip install)、编译构建、下载文件、查日志、执行普通算法脚本。即使这些任务非常耗时，只要它们最终会结束，就必须设为 false 以便拿到完整的执行日志。" } }, "required": ["command", "is_background"] } } },
    { "type": "function", "function": { "name": "read_file", "description": "读文件", "parameters": { "type": "object", "properties": { "file_path": { "type": "string" } }, "required": ["file_path"] } } },
    { "type": "function", "function": { "name": "write_file", "description": "写文件或局部修改文件。局部修改必须提供 old_content。", "parameters": { "type": "object", "properties": { "file_path": { "type": "string" }, "content": { "type": "string" }, "old_content": { "type": "string" } }, "required": ["file_path", "content"] } } },
    { "type": "function", "function": { "name": "read_local_image", "description": "看图（读取本地图片为 base64）", "parameters": { "type": "object", "properties": { "file_path": { "type": "string" } }, "required": ["file_path"] } } },
    { "type": "function", "function": { "name": "search_content", "description": "全局搜索关键字。", "parameters": { "type": "object", "properties": { "keyword": { "type": "string" }, "directory": { "type": "string" }, "file_pattern": { "type": "string" } }, "required": ["keyword"] } } },
    { "type": "function", "function": { "name": "finish_task", "description": "当用户主动提及清理上下文或者清理聊天记录时触发。", "parameters": { "type": "object", "properties": {} } } },
    { "type": "function", "function": { "name": "add_scheduled_task", "description": "添加定时或延时任务。系统底层的C#引擎会绝对接管时间调度，绝不能在任务执行时由AI去动态补加下一次任务。", "parameters": { "type": "object", "properties": { "execute_at": { "type": "string", "description": "首次执行时间，严格遵循 ISO 8601 格式，例如 '2026-03-20T14:30:00+08:00'" }, "user_intent": { "type": "string", "description": "到达时间时，大模型需要执行的具体任务要求和背景" }, "interval_minutes": { "type": "integer", "description": "可选。如果是周期性任务，请设置此周期间隔（分钟数）。例如每天执行则设为 1440。如果不填或为 0，则仅执行一次。系统会在底层自动无限循环，无需AI干预。" } }, "required": ["execute_at", "user_intent"] } } },
    { "type": "function", "function": { "name": "remove_scheduled_task", "description": "删除指定的定时或延时任务。", "parameters": { "type": "object", "properties": { "task_id": { "type": "string", "description": "要删除的任务ID（从任务列表中获取）" } }, "required": ["task_id"] } } },
    { "type": "function", "function": { "name": "install_skill", "description": "安装 单个 Skill-hub 或者 从第三方的技能，并根据包含的 MD 文件自动了解对接方式。", "parameters": { "type": "object", "properties": { "slug": { "type": "string", "description": "技能列表中的slug字段只需传入这个字段即可" } }, "required": ["slug"] } } },
    { "type": "function", "function": { "name": "self_update", "description": "当用户要求皮皮虾自我更新、自动更新或升级自身时调用此工具。将从 GitHub 下载最新版本并自动重启。", "parameters": { "type": "object", "properties": {} } } },
    { "type": "function", "function": { "name": "delegate_task", "description": "向通讯录中的其他皮皮虾节点指派任务。请从系统提示词的【友军通讯录】中查找对方的准确名字。严禁委派给当前节点自己！", "parameters": { "type": "object", "properties": { "user_name": { "type": "string", "description": "目标节点的名称，例如 '树莓派'" }, "task_message": { "type": "string", "description": "你要交办的具体任务内容" } }, "required": ["user_name", "task_message"] } } }
]
""");
//在没有找到合适的  搜索 api 之前先注释
//    { "type": "function", "function": { "name": "search_skill", "description": "当用户要求安装、查找或添加某个特定技能时执行此功能。根据关键词从 Skill-hub 搜索技能。注意：当用户说要“自我构建”或“编写”技能时，不要执行此功能。", "parameters": { "type": "object", "properties": { "query": { "type": "string", "description": "用户想要搜索或安装的技能关键词，例如 'calendar', 'weather' 等" } }, "required": ["query"] } } },
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
string recordsDir = Path.Combine(AppContext.BaseDirectory, "Records");
if (!Directory.Exists(recordsDir)) Directory.CreateDirectory(recordsDir);

string tasksPath = Path.Combine(recordsDir, "pi_scheduled_tasks.json");

// 移除单一的全局变量，改为多用户并发字典
System.Collections.Concurrent.ConcurrentDictionary<string, List<ChatMessage>> userHistories = new();
System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> userLocks = new();
System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> userCts = new();
System.Collections.Concurrent.ConcurrentDictionary<string, List<PushMsg>> userLiveStream = new();
System.Collections.Concurrent.ConcurrentDictionary<string, Action<PushMsg>> userConnections = new();
lock (tasksPath)
{
    if (!File.Exists(tasksPath)) File.WriteAllText(tasksPath, "[]", Encoding.UTF8);
}
List<ChatMessage> GetHistory(string user)
{
    if (userHistories.TryGetValue(user, out var h)) return h;
    var path = Path.Combine(recordsDir, $"{user}_history.json");
    var list = new List<ChatMessage>();
    if (File.Exists(path))
    {
        try { list = JsonSerializer.Deserialize(File.ReadAllText(path, Encoding.UTF8), AppJsonContext.Default.ListChatMessage) ?? new(); } catch { }
    }
    userHistories[user] = list;
    return list;
}
using var client = new HttpClient();
client.Timeout = TimeSpan.FromMinutes(10);
const string CancelledMsg = "\n[任务已取消]";
bool selfUpdateRequested = false;
// ========================== 6. 启动后台服务 (调度 + WebUI) ==========================
_ = Task.Run(ScheduleLoop);
_ = Task.Run(StartWebManager);
// ========================== 7. 定时任务管理逻辑 ==========================
string AddScheduledTask(string username, string? execAtStr, string? intent, int intervalMinutes = 0)
{
    if (!DateTimeOffset.TryParse(execAtStr, out var execTime))
        return "[添加失败] 时间格式解析错误，请使用 ISO 8601 格式，如 2026-03-20T15:30:00+08:00";
    if (execTime < DateTimeOffset.Now && intervalMinutes == 0)
        return $"[添加失败] 设定时间 {execTime:yyyy-MM-dd HH:mm:ss} 已过去，且不是周期任务。";

    lock (tasksPath)
    {
        var tasks = new List<TaskItem>();
        if (File.Exists(tasksPath))
        {
            try { tasks = JsonSerializer.Deserialize(File.ReadAllText(tasksPath, Encoding.UTF8), AppJsonContext.Default.ListTaskItem) ?? new(); } catch { }
        }
        var newTask = new TaskItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ExecuteAt = execTime.ToString("o"),
            UserIntent = intent ?? "未提供具体意图",
            Status = "pending",
            IntervalMinutes = intervalMinutes,
            Username = username // 👈 绑定到当前操作的用户
        };
        tasks.Add(newTask);
        File.WriteAllText(tasksPath, JsonSerializer.Serialize(tasks, AppJsonContext.Default.ListTaskItem), Encoding.UTF8);
    }
    // 控制台输出顺手加上归属人
    Console.ForegroundColor = ConsoleColor.Green;
    string loopStr = intervalMinutes > 0 ? $" [周期: 每 {intervalMinutes} 分钟执行]" : " [单次执行]";
    Console.WriteLine($"[调度中心] 成功创建任务: {execTime:yyyy-MM-dd HH:mm:ss} -> {intent}{loopStr} (归属: {username})");
    Console.ResetColor();
    return $"[定时任务已添加] PiPiClaw 已将任务持久化，将在 {execTime:yyyy-MM-dd HH:mm:ss} 触发执行。{loopStr} 用户的需求是：{intent}。系统会在底层调度，请不要再次重复调用添加任务。";
}
string RemoveScheduledTask(string username, string? taskId)
{
    if (string.IsNullOrEmpty(taskId)) return "[删除失败] 必须提供 task_id";
    lock (tasksPath)
    {
        if (!File.Exists(tasksPath)) return "[删除失败] 任务文件不存在";
        var tasks = new List<TaskItem>();
        try { tasks = JsonSerializer.Deserialize(File.ReadAllText(tasksPath, Encoding.UTF8), AppJsonContext.Default.ListTaskItem) ?? new(); } catch { }

        // 👈 核心：只能删自己名下的任务
        var targetNode = tasks.FirstOrDefault(t => t.Id == taskId && t.Username == username);
        if (targetNode != null)
        {
            tasks.Remove(targetNode);
            File.WriteAllText(tasksPath, JsonSerializer.Serialize(tasks, AppJsonContext.Default.ListTaskItem), Encoding.UTF8);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n[调度中心] 成功移除任务 (ID: {taskId}, 归属: {username})");
            Console.ResetColor();
            return $"[任务已删除] 成功移除了 ID 为 {taskId} 的任务。";
        }
        return $"[删除失败] 未在挂起队列中找到归属于你且 ID 为 {taskId} 的任务。";
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
            Console.WriteLine("".PadLeft(20, '-') + "[ PiPiClaw 挂起任务 ] " + "".PadRight(20, '-') + "\n");
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
                Console.WriteLine($"  └─ [归属: {t.Username}] 需求: {intent} (ID: {t.Id})");
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
        foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (item.OperationalStatus != OperationalStatus.Up) continue;
            if (item.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
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
                if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var ipStr = ip.Address.ToString();
                    if (ipStr.StartsWith("192.168.") || ipStr.StartsWith("10.") ||
                       (ipStr.StartsWith("172.") && !ipStr.StartsWith("172.16."))) // Docker 默认常在 172.17+
                    {
                        return ipStr;
                    }
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
    string? input = null;
    if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
    {
        Console.Write("\n皮皮虾 > ");
        input = Console.ReadLine();
    }
    if (input == null)
    {
        await Task.Delay(-1);
    }
    if (string.IsNullOrEmpty(input)) continue;
    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
    await RunAgent(input);
}

return;

// ========================== 10. 核心 Agent 处理逻辑 ==========================
async Task<string> RunAgent(string inputMessage, bool isScheduledEvent = false, int modelIndex = 0, string username = "local", string caller = "")
{

    var userLock = userLocks.GetOrAdd(username, _ => new SemaphoreSlim(100, 100));
    await userLock.WaitAsync();
    var liveStream = new List<PushMsg>();
    userLiveStream[username] = liveStream;
    Action<string, string> PushUpdate = (type, content) =>
    {
        var pm = new PushMsg { Type = type, Content = content };
        lock (liveStream) liveStream.Add(pm);
        if (userConnections.TryGetValue(username, out var conn)) conn(pm);
    };

    var historyPath = Path.Combine(recordsDir, $"{username}_history.json");
    var history = GetHistory(username);
    void SafeAddHistory(ChatMessage m)
    {
        lock (history)
        {
            history.Add(m.DeepClone());
            SaveData(history, historyPath);
        }
    }

    var currentModelCfg = (GlobalConfig.Models != null && GlobalConfig.Models.Count > modelIndex)
        ? GlobalConfig.Models[modelIndex]
        : (GlobalConfig.Models?.FirstOrDefault() ?? new ModelConfig());
    var activeModel = !string.IsNullOrEmpty(currentModelCfg.Model) ? currentModelCfg.Model : "qwen3.5-plus";
    var activeEndpoint = !string.IsNullOrEmpty(currentModelCfg.Endpoint) ? currentModelCfg.Endpoint : "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";
    var activeApiKey = currentModelCfg.ApiKey;
    using var taskCts = new CancellationTokenSource();
    userCts[username] = taskCts;
    string finalAIResponse = "";
    try
    {
        var useFullContext = false;
        var requireReset = false;
        var userMsg = new ChatMessage { Role = "user", Content = inputMessage };
        SafeAddHistory(userMsg);
        var isDone = false;
        while (!isDone)
        {
            using var cts = new CancellationTokenSource();
            var animTask = Think(username, cts.Token);
            var currentTasksJson = "暂无定时任务";
            if (File.Exists(tasksPath))
            {
                lock (tasksPath)
                {
                    try
                    {
                        var allTasks = JsonSerializer.Deserialize(File.ReadAllText(tasksPath, Encoding.UTF8), AppJsonContext.Default.ListTaskItem) ?? new();
                        var userTasks = allTasks.Where(t => t.Username == username).ToList();
                        if (userTasks.Count > 0)
                        {
                            currentTasksJson = JsonSerializer.Serialize(userTasks, AppJsonContext.Default.ListTaskItem);
                        }
                    }
                    catch { }
                }
            }
            var callerStr = !string.IsNullOrEmpty(caller)
                ? $"\n【任务来源追溯】：当前任务由友军【{caller}】指派。当任务彻底做完准备交付时，请**直接用自然语言输出最终结果**，系统底层会全自动将你的话作为工作报告回传给【{caller}】！绝对不要用 delegate_task 跑去向【{caller}】做最终汇报。(注：若执行中途遇到困难，需要向【{caller}】请教确认需求，仍可使用 delegate_task 联系对方)。\n"
                : "";

            var nodeIdentityStr = !string.IsNullOrEmpty(username)
                ? $"你当前是【{username}】皮皮虾。如【友军通讯录】中看到你自己！请直接用本地工具执行，绝对不要委派给自己！\n"
                : "";

            var peersStr = (GlobalConfig.PeerNodes != null && GlobalConfig.PeerNodes.Count > 0)
                ? string.Join("\n", GlobalConfig.PeerNodes.Select(p => $"- 【{p.Key}】皮皮虾\n  能力说明: 【{p.Value.Role}】{p.Value.Description}"))
                : "暂无已知友军节点";

            var customPrompt = !string.IsNullOrWhiteSpace(GlobalConfig.SystemPrompt)
                ? GlobalConfig.SystemPrompt
                : "你是一个全能智能 Agent，代号 PiPiClaw (皮皮虾)。你的使命：以本地优先、安全可审计的方式完成用户提出的任何任务，不限于运维/开发/数据/知识检索。你可随时通过 Skill-Hub 安装技能扩展能力。";

            var systemPromptText = $$"""
                                     {{customPrompt}}
                                     {{callerStr}}
                                     【当前环境状态】
                                     当前系统：{{RuntimeInformation.OSDescription}}。当前时间是 {{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}}。
                                     {{sudoInstruction}}

                                     【记忆管理架构】：
                                     [技能调用策略] 下方列表包含了本地已安装的技能和简短摘要。当用户的需求需要用到某个技能时，你必须先调用 read_file 工具，读取该技能目录下的 skill.md 文件以获取完整的对接文档，然后再根据文档指导进行下一步操作。绝对不要凭空猜测调用方式！
                                     
                                     【身份认知】
                                     {{nodeIdentityStr}}

                                     【友军通讯录 (Peer Nodes)】：
                                     当用户提的需求友军能力满足的时候，优先调用 delegate_task 工具，以下是友军的能力说明：
                                     {{peersStr}}
                                      
                                     【PiPiClaw 挂起的定时任务（包含 task_id，供你管理任务时参考）】：
                                     {{currentTasksJson}}

                                     【本地已安装的扩展技能及绝对路径说明】：
                                     {{GetInstalledSkillsContext(username)}}
                                     """;
            var payloadMessages = new List<ChatMessage> { new ChatMessage { Role = "system", Content = systemPromptText } };
            IEnumerable<ChatMessage> recentMessages;
            if (useFullContext)
            {
                recentMessages = history;
            }
            else
            {
                var seenUser = 0;
                var startIndex = 0;
                for (var i = history.Count - 1; i >= 0; i--)
                {
                    if (history[i].Role != "user") continue;
                    seenUser++;
                    if (seenUser <= 3) continue;
                    startIndex = i + 1;
                    break;
                }
                recentMessages = history.Skip(startIndex);
            }
            foreach (var m in recentMessages) payloadMessages.Add(m.DeepClone());
            var payload = new LlmRequest
            {
                Model = activeModel,
                Messages = payloadMessages,
                Tools = toolsDoc.RootElement,
                EnableSearch = true
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", activeApiKey);
            string responseString = "";
            bool isSuccess = false;
            Exception? lastEx = null;
            int maxRetries = 30; // 设置最大重试次数

            for (int retry = 0; retry < maxRetries; retry++)
            {
                try
                {
                    // 注意：每次重试需要重新生成 StringContent，避免流被重复读取导致报错
                    var content = new StringContent(JsonSerializer.Serialize(payload, AppJsonContext.Default.LlmRequest), Encoding.UTF8, "application/json");
                    var res = await client.PostAsync(activeEndpoint, content, taskCts.Token);

                    if (res.IsSuccessStatusCode)
                    {
                        responseString = await res.Content.ReadAsStringAsync(taskCts.Token);
                        isSuccess = true;
                        break; // 请求成功，跳出重试循环
                    }

                    int statusCode = (int)res.StatusCode;
                    // 如果网关返回 5xx 错误，手动抛出异常以触发重试
                    if (statusCode >= 500 && statusCode < 600)
                    {
                        throw new Exception($"网关返回 5xx 错误码 ({statusCode})");
                    }
                    else
                    {
                        // 4xx 级别的错误（如鉴权失败、参数错误等）通常没有重试的必要，直接抛出
                        res.EnsureSuccessStatusCode();
                    }
                }
                catch (OperationCanceledException)
                {
                    cts.Cancel(); await animTask;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(CancelledMsg); Console.ResetColor();
                    PushUpdate?.Invoke("final", CancelledMsg);
                    return CancelledMsg;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (retry < maxRetries - 1)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine($"\n[网络抖动] {ex.Message}，正在进行第 {retry + 1} 次重试...");
                        Console.ResetColor();
                        PushUpdate?.Invoke("tool_result", $"[网络抖动] 请求网关失败，准备第 {retry + 1} 次重试...");

                        // 延时 2 秒后进行下一次重试，给网关恢复的时间
                        await Task.Delay(2000, taskCts.Token);
                    }
                }
            }

            // 如果用尽了重试次数依然失败，则执行原来的失败报错逻辑
            if (!isSuccess)
            {
                cts.Cancel(); await animTask;
                Console.ForegroundColor = ConsoleColor.Red;
                var err = $"\n[网络错误] 请求 API 失败(已达最大重试次数 {maxRetries}): {lastEx?.Message}";
                Console.WriteLine(err); Console.ResetColor();
                PushUpdate?.Invoke("final", err);
                return err;
            }
            var msg = JsonSerializer.Deserialize(responseString, AppJsonContext.Default.LlmResponse)?.Choices?.FirstOrDefault()?.Message;
            if (msg != null)
            {
                if (msg.Content == "")
                {
                    msg.Content = null;
                }
                if (msg.ReasoningContent == "")
                {
                    msg.ReasoningContent = null;
                }
            }
            cts.Cancel();
            await animTask;
            if (msg == null) break;
            SafeAddHistory(msg);
            var toolCalls = msg.ToolCalls;

            string thinkText = msg.ReasoningContent ?? msg.Content ?? "";
            if (!string.IsNullOrWhiteSpace(thinkText) && toolCalls != null && toolCalls.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkMagenta;
                Console.WriteLine($"\n[思考过程] {thinkText}");
                Console.ResetColor();
                PushUpdate?.Invoke("thinking", thinkText); // 下发给前端
            }

            if (toolCalls != null && toolCalls.Count > 0)
            {
                foreach (var call in toolCalls)
                {
                    var fnName = call.Function.Name;
                    var argsString = call.Function.Arguments;
                    JsonElement? tempArgs = null;
                    try { tempArgs = JsonDocument.Parse(argsString).RootElement; } catch { }
                    string GetStrProp(JsonElement? el, string key)
                    {
                        if (el.HasValue && el.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String) return prop.GetString() ?? "";
                        return "";
                    }
                    bool GetBoolProp(JsonElement? el, string key)
                    {
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
                        case "execute_command": actionDesc = $"执行: {GetStrProp(tempArgs, "command")} (is_background: {GetBoolProp(tempArgs, "is_background")})"; Console.WriteLine($"[Action] {actionDesc}"); break;
                        case "read_file": actionDesc = $"读取: {GetStrProp(tempArgs, "file_path")}"; Console.WriteLine($"[Action] {actionDesc}"); break;
                        case "write_file": actionDesc = $"写入: {GetStrProp(tempArgs, "file_path")}"; Console.WriteLine($"[Action] {actionDesc}"); break;
                        case "search_content": actionDesc = $"搜索: {GetStrProp(tempArgs, "keyword")}"; Console.WriteLine($"[Action] {actionDesc}"); break;
                        case "delegate_task": actionDesc = $"找同事: {GetStrProp(tempArgs, "user_name")}"; Console.WriteLine($"[Action] {actionDesc}"); break;
                        default: actionDesc = $"调用参数: {argsString}"; break;
                    }
                    Console.ResetColor();
                    PushUpdate?.Invoke("tool", $"[调用工具] {fnName}\n{actionDesc}");
                    var result = "";
                    switch (fnName)
                    {
                        case "install_skill": result = await InstallSkill(GetStrProp(tempArgs, "slug"), username); break;
                        case "finish_task":
                            requireReset = true;
                            result = "[系统提示] 上下文清理已预约，这将在你给出最后一句回复后执行。请现在用正常的自然语言向用户总结任务完成情况。";
                            try
                            {
                                // 直接删当前用户的历史记录文件，historyPath 已经是完整路径了
                                if (File.Exists(historyPath)) File.Delete(historyPath);
                            }
                            catch (Exception ex) { }
                            PushUpdate?.Invoke("clear_chat", "");
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("[内部状态] Agent 判定任务结束，已预约清理上下文...");
                            Console.ResetColor();
                            break;
                        case "add_scheduled_task":
                            {
                                int intervalMin = 0;
                                if (tempArgs.HasValue && tempArgs.Value.TryGetProperty("interval_minutes", out var intProp) && intProp.ValueKind == JsonValueKind.Number)
                                    intervalMin = intProp.GetInt32();
                                // 👈 传入 username
                                result = AddScheduledTask(username, GetStrProp(tempArgs, "execute_at"), GetStrProp(tempArgs, "user_intent"), intervalMin);
                                break;
                            }
                        case "remove_scheduled_task":
                            result = RemoveScheduledTask(username, GetStrProp(tempArgs, "task_id"));
                            break;
                        case "self_update": result = await SelfUpdate(); break;
                        case "execute_command": result = await RunCmd(GetStrProp(tempArgs, "command"), PushUpdate, GetBoolProp(tempArgs, "is_background"), taskCts.Token); break;
                        case "delegate_task": result = await DelegateTaskAsync(username, GetStrProp(tempArgs, "user_name"), GetStrProp(tempArgs, "task_message")); break;
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
                    if (fnName != "execute_command")
                    {
                        PushUpdate?.Invoke("tool_result", result);
                    }
                    var toolResultMsg = new ChatMessage
                    {
                        Role = "tool",
                        //Name = fnName,
                        Content = result,
                        ToolCallId = call.Id
                    };
                    SafeAddHistory(toolResultMsg);
                }
            }
            else
            {
                finalAIResponse = msg.Content ?? "";
                Console.ResetColor();
                Console.WriteLine($"\n{finalAIResponse}");
                Console.ResetColor();
                PushUpdate?.Invoke("final", finalAIResponse);
                isDone = true;
            }
        }
        if (requireReset)
        {
            // 在清空前，抓取 AI 刚刚输出的最后一句有效内容（即最终工作报告）
            var finalSummary = history.LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content));

            history.Clear();
            if (File.Exists(historyPath)) File.Delete(historyPath);

            // 把这句报告重新塞入干净的记忆中并落盘，专门留给 Team 中控读取
            if (finalSummary != null)
            {
                SafeAddHistory(userMsg);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n✅ [生命周期] 用户 {username} 的任务上下文已全自动清理！(并保留了最终报告留底)");
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
        userLiveStream.TryRemove(username, out _);
        userCts.TryRemove(username, out _);
        userLock.Release();
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
                        if (!string.IsNullOrWhiteSpace(content)) tasks = JsonSerializer.Deserialize(content, AppJsonContext.Default.ListTaskItem) ?? new();
                    }
                    catch { }
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
                    var taskUser = string.IsNullOrEmpty(t.Username) ? "local" : t.Username;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            Console.ForegroundColor = ConsoleColor.Magenta;
                            Console.WriteLine($"\n\n[🔔 皮皮虾唤醒] 正在接管系统执行预定需求: {intent} (归属: {taskUser})");
                            Console.ResetColor();
                            await RunAgent($"[系统级注入] 这是一个系统自动触发的定时任务。你之前设定了在此时执行该任务，用户的原始需求是：【{intent}】。请立刻处理，绝不要尝试手动重新添加定时任务，完成后调用 finish_task 结束。", true, 0, taskUser);
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
string GetInstalledSkillsContext(string username)
{
    var skillsDir = Path.Combine(AppContext.BaseDirectory, "skills", username);
    if (!Directory.Exists(skillsDir)) return "当前未安装任何专属技能";

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
            }
            catch { }
        }
        else
        {
            sb.AppendLine($"  技能功能摘要: [未生成摘要]");
            sb.AppendLine($"  说明：该技能已安装。请先 read_file 查看 {absolutePath}/skill.md，然后使用 write_file 在此目录下生成一个名为 summary.txt 的文件，里面只写一句话功能概括即可。");
        }
    }
    return sb.Length > 0 ? sb.ToString() : "当前未安装任何专属技能";
}

async Task Think(string username, CancellationToken ct)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    for (int i = 0; !ct.IsCancellationRequested; i++)
    {
        Console.Write($"\r {"-\\|/"[i % 4]} {username}皮皮虾正在思考中...");
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
    if (Console.IsInputRedirected || Console.IsOutputRedirected)
    {
        Console.WriteLine("[警告] 检测到非交互式终端，无法安全读取密码，请在 appsettings.json 中手动配置。");
        return "";
    }
    var pwd = new StringBuilder();
    while (true)
    {
        try
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
        catch { break; } // 捕获 Linux 下可能抛出的异常
    }
    Console.WriteLine();
    return pwd.ToString();
}
async Task<string> RunCmd(string? cmd, Action<string, string>? pushUpdate, bool isBackground = false, CancellationToken ct = default)
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
    var shellArg = isWin ? $"/c chcp 65001 >nul 2>&1 & {cmd}" : $"-c \"{cmd}\"";
    var shellExe = isWin ? "cmd.exe" : "/bin/bash";
    var consoleEncoding = new UTF8Encoding(false);

    if (isBackground)
    {
        _ = Task.Run(async () =>
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
            p.OutputDataReceived += (sender, e) => { if (e.Data != null) Console.WriteLine($"[后台] {e.Data}"); };
            p.ErrorDataReceived += (sender, e) => { if (e.Data != null) { Console.ForegroundColor = ConsoleColor.DarkYellow; Console.WriteLine($"[后台警告] {e.Data}"); Console.ResetColor(); } };
            try { p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); await p.WaitForExitAsync(ct); }
            catch { }
            finally { if (!string.IsNullOrEmpty(askPassPath) && File.Exists(askPassPath)) try { File.Delete(askPassPath); } catch { } }
        }, ct);
        return $"[已转入后台运行] 进程已在后台剥离启动: {cmd}。因为是后台任务，所以你将不会直接收到此命令的后续输出。如果需要知道运行状态，请通过其他命令（如 ps、curl 或检查日志文件）来验证。";
    }
    else
    {
        // ================= 前台阻塞模式 (终极防卡死版) =================
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

        var lastOutputTime = DateTime.UtcNow;
        var startTime = DateTime.UtcNow;     // 新增：记录绝对起点时间
        var idleTimeoutMs = 60000 * 3;       // 60秒无任何输出判定卡死
        var absoluteTimeoutMs = 300000 * 2;      // 新增：绝对超时时间，最长允许跑 5 分钟 (300秒)
        var isKilledByWatchdog = false;

        // 新增：最大输出行数防御，防止内存 OOM
        int currentLines = 0;
        const int MaxLinesLimit = 5000;

        p.OutputDataReceived += (sender, e) =>
        {
            if (e.Data == null) return;
            lastOutputTime = DateTime.UtcNow;

            // 【熔断器】：超过最大行数，丢弃后续输出保护内存
            if (Interlocked.Increment(ref currentLines) > MaxLinesLimit)
            {
                if (currentLines == MaxLinesLimit + 1)
                {
                    var warn = "\n[系统拦截] ⚠️ 输出日志超过 5000 行限制，已自动掐断后续输出截获以保护内存...";
                    Console.WriteLine(warn);
                    outputBuilder.AppendLine(warn);
                    pushUpdate?.Invoke("tool_result", warn);
                }
                return;
            }

            Console.WriteLine(e.Data);
            outputBuilder.AppendLine(e.Data);
            pushUpdate?.Invoke("tool_result", e.Data);
        };

        p.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data == null) return;
            lastOutputTime = DateTime.UtcNow;

            if (Interlocked.Increment(ref currentLines) > MaxLinesLimit) return; // 同样受总行数限制

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(e.Data);
            Console.ResetColor();
            errorBuilder.AppendLine(e.Data);
            pushUpdate?.Invoke("tool_result", e.Data);
        };

        try
        {
            p.EnableRaisingEvents = true;
            p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();

            // 用于安全解除 await 阻塞的信号源
            var processExitTcs = new TaskCompletionSource();
            p.Exited += (s, e) => processExitTcs.TrySetResult();
            if (p.HasExited) processExitTcs.TrySetResult();

            // 启动独立看门狗轮询任务
            using var watchdogCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!p.HasExited && !watchdogCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(2000, watchdogCts.Token);
                    var now = DateTime.UtcNow;

                    bool isIdle = (now - lastOutputTime).TotalMilliseconds > idleTimeoutMs;
                    bool isOvertime = (now - startTime).TotalMilliseconds > absoluteTimeoutMs;

                    if (isIdle || isOvertime)
                    {
                        if (p.HasExited) break;
                        isKilledByWatchdog = true;

                        // 【核心防御】：无论 OS 杀不杀得掉进程，强行让主线程向下走，坚决不卡死！
                        processExitTcs.TrySetResult();

                        try
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            var warnMsg = isOvertime
                                ? $"\n[守护进程] ⚠️ 任务执行超过了绝对时间限制 ({absoluteTimeoutMs / 1000}秒)，已强制猎杀进程！"
                                : "\n[守护进程] ⚠️ 检测到命令超过 1 分钟无任何输出，判定为阻塞等待输入或死循环，已强制猎杀进程！";
                            Console.WriteLine(warnMsg);
                            Console.ResetColor();

                            errorBuilder.AppendLine(warnMsg);
                            pushUpdate?.Invoke("tool_result", warnMsg);
                            p.Kill(true);
                        }
                        catch { }
                        break;
                    }
                }
            }, ct);

            // 主线程等待区：有了看门狗兜底，这里绝对不可能永久卡死
            await using (ct.Register(() => { processExitTcs.TrySetCanceled(); try { p.Kill(true); } catch { } }))
            {
                await processExitTcs.Task;
                await Task.Delay(200); // 给流缓冲留一点点收尾时间
            }

            watchdogCts.Cancel();
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

        if (isKilledByWatchdog)
        {
            finalErr += "\n\n[系统提示] 你的命令因为超时或没有输出被强制杀死了。如果你的命令需要长时间运行且不需要即时反馈，请设置 is_background=true；如果是因为需要交互确认（如 [Y/n]），请加上 -y 之类的免交互参数！";
        }

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

        bool isConfig = Path.GetFileName(path).Equals("appsettings.json", StringComparison.OrdinalIgnoreCase);

        if (File.Exists(path) && !string.IsNullOrEmpty(oldContent))
        {
            var normalizedCurrent = currentText.Replace("\r\n", "\n");
            var normalizedOld = oldContent.Replace("\r\n", "\n");
            var normalizedNew = content?.Replace("\r\n", "\n") ?? "";
            if (!normalizedCurrent.Contains(normalizedOld))
                return "[修改失败] 未精准匹配到 old_content，请确认内容。如果你不确定，请先 read_file 查看完整内容。";
            var finalContent = normalizedCurrent.Replace(normalizedOld, normalizedNew);
            File.WriteAllText(path, finalContent.Replace("\n", Environment.NewLine), targetEncoding);

            // 局部修改成功后，触发热重载
            if (isConfig) ReloadConfig();

            return $"[修改成功] 文件局部已更新：{path}";
        }

        File.WriteAllText(path, content ?? "", targetEncoding);

        // 全量写入成功后，触发热重载
        if (isConfig) ReloadConfig();

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

async Task<string> DelegateTaskAsync(string callUser, string username, string taskMessage)
{
    if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(taskMessage))
        return "[委派失败] 节点名称或任务内容为空。";

    if (!GlobalConfig.PeerNodes!.TryGetValue(username, out var peerInfo) || string.IsNullOrEmpty(peerInfo.Url))
        return $"[委派失败] 未在通讯录中找到{username}皮皮虾，或其 URL 未配置。";

    try
    {
        var reqUrl = peerInfo.Url.TrimEnd('/') + "/api/agent_task";
        var reqBody = new ChatReq { Message = taskMessage, ModelIndex = 0, Caller = callUser };
        var jsonBody = JsonSerializer.Serialize(reqBody, AppJsonContext.Default.ChatReq);
        using var request = new HttpRequestMessage(HttpMethod.Post, reqUrl);
        // 【核心修复】：直接把目标节点自己的名字传过去，这样它的历史记录就能存到自己的名下
        request.Headers.Add("X-Username", Uri.EscapeDataString(username));
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadAsStringAsync();
        return $"[{username}皮皮虾执行完毕并返回结果]:\n{result}";
    }
    catch (Exception ex)
    {
        return $"[委派通信异常] 无法连接到节点 '{username}' 或对方执行出错: {ex.Message}";
    }
}
async Task<string> InstallSkill(string? slug, string username)
{
    if (string.IsNullOrEmpty(slug)) return "❌ slug不能为空";
    var sb = new StringBuilder();
    sb.AppendLine($"🚀 启动安装程序: {slug} (专属分配至: {username})...");
    try
    {
        var downloadUrls = (GlobalConfig.SkillHubDownloadUrls != null && GlobalConfig.SkillHubDownloadUrls.Count > 0)
            ? GlobalConfig.SkillHubDownloadUrls.Select(url => url.Replace("{slug}", slug)).ToList()
            : [$"https://wry-manatee-359.convex.site/api/v1/download?slug={slug}"];

        // 修改点：将目标文件夹按 username 隔离
        string skillsFolder = Path.Combine(AppContext.BaseDirectory, "skills", username);
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

        bool isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        using var p = new Process();
        if (isWin)
        {
            p.StartInfo = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"Expand-Archive -Path '{zipPath}' -DestinationPath '{targetFolder}' -Force\"") { CreateNoWindow = true };
        }
        else
        {
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
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "x64"
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
            bat.AppendLine("chcp 65001 >nul");
            bat.AppendLine("setlocal enabledelayedexpansion");
            bat.AppendLine($"set \"PID={currentPid}\"");
            bat.AppendLine($"set \"DL_URL={downloadUrl}\"");
            bat.AppendLine($"set \"TARGET={currentExePath}\"");
            bat.AppendLine($"set \"ARCHIVE={archivePath}\"");
            bat.AppendLine($"set \"EXTRACT={extractDir}\"");
            bat.AppendLine();
            bat.AppendLine("echo [AutoUpdate] 正在等待进程退出...");
            bat.AppendLine(":waitloop");
            bat.AppendLine("tasklist /FI \"PID eq %PID%\" | find \"%PID%\" >nul");
            bat.AppendLine("if %ERRORLEVEL%==0 (");
            bat.AppendLine("  timeout /t 1 /nobreak >nul");
            bat.AppendLine("  goto waitloop");
            bat.AppendLine(")");
            bat.AppendLine("echo [AutoUpdate] 进程已退出，开始下载压缩包...");
            bat.AppendLine("powershell -NoProfile -Command \"Invoke-WebRequest -Uri '%DL_URL%' -OutFile '%ARCHIVE%'\" || goto fail");
            bat.AppendLine("echo [AutoUpdate] 下载完成，开始解压...");
            bat.AppendLine("powershell -NoProfile -Command \"Expand-Archive -Path '%ARCHIVE%' -DestinationPath '%EXTRACT%' -Force\" || goto fail");
            bat.AppendLine("echo [AutoUpdate] 准备替换文件...");
            bat.AppendLine($"if not exist \"%EXTRACT%\\{exeName}\" goto fail");
            bat.AppendLine($"copy /y \"%EXTRACT%\\{exeName}\" \"%TARGET%\" || goto fail");
            bat.AppendLine("echo [AutoUpdate] 替换成功，正在重启...");
            bat.AppendLine("start \"\" \"%TARGET%\"");
            bat.AppendLine("rd /s /q \"%EXTRACT%\"");
            bat.AppendLine("del \"%ARCHIVE%\"");
            bat.AppendLine("del \"%~f0\"");
            bat.AppendLine("exit /b 0");
            bat.AppendLine(":fail");
            bat.AppendLine("echo [AutoUpdate] 更新失败，未能完成替换！");
            bat.AppendLine("pause"); // 失败时依然暂停，留存案发现场
            bat.AppendLine("del \"%~f0\"");
            bat.AppendLine("exit /b 1");
            File.WriteAllText(scriptPath, bat.ToString(), new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"") { CreateNoWindow = false, UseShellExecute = true });
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
async Task HandleRequestAsync(HttpListenerContext context, int webPort)
{
    var req = context.Request;
    var res = context.Response;
    res.Headers.Add("Access-Control-Allow-Origin", "*");
    res.Headers.Add("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
    res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
    var rawUsername = req.Headers["X-Username"];
    var username = string.IsNullOrWhiteSpace(rawUsername) ? "WebUser" : Uri.UnescapeDataString(rawUsername);
    if (string.IsNullOrWhiteSpace(username)) username = "WebUser";
    try
    {
        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 200;
            res.Close();
            return;
        }
        var url = req.Url;
        if (url == null) { res.StatusCode = 400; res.Close(); return; }
        string path = url.AbsolutePath;
        switch (path)
        {
            // --- 首页渲染 ---
            case "/":
                string htmlContent = GetWebUIHtml()
                    .Replace("{{LAN_IP}}", GetLocalIpAddress())
                    .Replace("{{WEB_PORT}}", webPort.ToString());
                byte[] buffer = Encoding.UTF8.GetBytes(htmlContent);
                res.ContentType = "text/html; charset=utf-8";
                res.ContentLength64 = buffer.Length;
                await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                break;

            // --- 获取已知的所有 Agent 列表 ---
            case "/api/agents" when req.HttpMethod == "GET":
                var agentFiles = Directory.GetFiles(recordsDir, "*_history.json")
                                          .Select(f => Path.GetFileName(f).Replace("_history.json", ""))
                                          .Distinct()
                                          .ToList();
                var agentsJsonStr = JsonSerializer.Serialize(agentFiles, AppJsonContext.Default.ListString);
                res.ContentType = "application/json; charset=utf-8";
                await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(agentsJsonStr));
                break;

            // --- 获取配置 ---
            case "/api/config" when req.HttpMethod == "GET":
                byte[] cfgBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(GlobalConfig, AppJsonContext.Default.AppConfig));
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(cfgBytes, 0, cfgBytes.Length);
                break;

            // --- 保存配置 ---
            case "/api/config" when req.HttpMethod == "POST":
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                {
                    var body = await reader.ReadToEndAsync();
                    var newCfg = JsonSerializer.Deserialize(body, AppJsonContext.Default.AppConfig);
                    bool portChanged = false;
                    if (newCfg != null)
                    {
                        int currentPort = int.TryParse(GetConfig("WebPort", "5050"), out var cp) ? cp : 5050;
                        portChanged = newCfg.WebPort != currentPort;
                        File.WriteAllText("appsettings.json", JsonSerializer.Serialize(newCfg, AppJsonContext.Default.AppConfig), Encoding.UTF8);
                        sudoPassword = newCfg.SudoPassword ?? "";
                        GlobalConfig = newCfg;
                    }
                    res.ContentType = "application/json";
                    var resp = Encoding.UTF8.GetBytes(portChanged ? "{\"status\":\"port_changed\"}" : "{\"status\":\"ok\"}");
                    await res.OutputStream.WriteAsync(resp, 0, resp.Length);
                }
                break;

            // --- 获取定时任务列表 ---
            case "/api/tasks":
                string tasksJson = "[]";
                lock (tasksPath)
                {
                    if (File.Exists(tasksPath))
                    {
                        try
                        {
                            var allTasks = JsonSerializer.Deserialize(File.ReadAllText(tasksPath, Encoding.UTF8), AppJsonContext.Default.ListTaskItem) ?? new();
                            var userTasks = allTasks.Where(t => t.Username == username).ToList();
                            tasksJson = JsonSerializer.Serialize(userTasks, AppJsonContext.Default.ListTaskItem);
                        }
                        catch { }
                    }
                }
                res.ContentType = "application/json";
                byte[] tBytes = Encoding.UTF8.GetBytes(tasksJson);
                await res.OutputStream.WriteAsync(tBytes, 0, tBytes.Length);
                break;

            // --- 中断当前 AI 任务 ---
            case "/api/cancel":
                if (userCts.TryGetValue(username, out var cts)) cts.Cancel();
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"cancelled\"}"));
                break;


            // --- 暴露当前智能体工作状态 ---
            case "/api/status" when req.HttpMethod == "GET":
                {
                    bool isBusy = false;
                    string actionStr = "摸鱼中...";
                    string activeKey = username;

                    if (userLocks.TryGetValue(username, out var lck) && lck.CurrentCount < 100)
                    {
                        isBusy = true;
                    }
                    if (isBusy)
                    {
                        if (userLiveStream.TryGetValue(activeKey, out var stream))
                        {
                            PushMsg? lastThink = null;
                            PushMsg? lastTool = null;
                            lock (stream)
                            {
                                lastThink = stream.LastOrDefault(m => m.Type == "thinking");
                                lastTool = stream.LastOrDefault(m => m.Type == "tool");
                            }

                            // 本地函数：去除换行并控制长度，防止撑爆气泡
                            string TruncateStr(string s, int max)
                            {
                                if (string.IsNullOrEmpty(s)) return s;
                                var flat = s.Replace("\r", "").Replace("\n", " ");
                                return flat.Length > max ? flat.Substring(0, max) + "..." : flat;
                            }

                            var actionLines = new List<string>();

                            // 第一行：思考过程
                            if (lastThink != null)
                                actionLines.Add($"🤔: {TruncateStr(lastThink.Content, 200)}");
                            else
                                actionLines.Add($"🤔 正在分析决策中...");

                            // 第二/三行：调用的工具和参数
                            if (lastTool != null)
                            {
                                var lines = lastTool.Content.Split('\n');
                                if (lines.Length > 1)
                                {
                                    actionLines.Add($"🔧: {TruncateStr(lines[0].Replace("[调用工具]", "").Trim(), 200)}");
                                    actionLines.Add($"⚡: {TruncateStr(lines[1].Trim(), 200)}");
                                }
                                else
                                {
                                    actionLines.Add($"🔧: {TruncateStr(lines[0].Replace("[调用工具]", "").Trim(), 200)}");
                                }
                            }

                            actionStr = string.Join("\n", actionLines);
                        }
                        else
                        {
                            actionStr = "🤔 正在分析决策中...";
                        }
                    }
                    string statusJson = $"{{\"isWorking\":{isBusy.ToString().ToLower()}, \"currentAction\":{JsonSerializer.Serialize(actionStr, AppJsonContext.Default.String)}}}";
                    res.ContentType = "application/json; charset=utf-8";
                    await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(statusJson));
                    break;
                }


            case "/api/clear" when req.HttpMethod == "POST":
                GetHistory(username).Clear();
                try { File.Delete(Path.Combine(recordsDir, $"{username}_history.json")); } catch { }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n✅ [生命周期] 用户已手动清理上下文！PiPiClaw 已就绪。");
                Console.ResetColor();

                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"cleared\"}"));
                break;

            case "/api/attach" when req.HttpMethod == "POST":
                {
                    bool isBusy = userLocks.TryGetValue(username, out var lck) && lck.CurrentCount < 100;
                    if (!isBusy)
                    {
                        res.ContentType = "application/json";
                        await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"status\":\"idle\"}"));
                        break;
                    }

                    res.ContentType = "text/plain; charset=utf-8";
                    res.SendChunked = true;
                    using var attachWriter = new StreamWriter(res.OutputStream, new UTF8Encoding(false));
                    attachWriter.AutoFlush = true;
                    userConnections[username] = (msg) =>
                    {
                        try { attachWriter.Write(JsonSerializer.Serialize(msg, AppJsonContext.Default.PushMsg) + "|||END|||"); } catch { }
                    };

                    // 先把期间积攒的缓存推给刚上线的前端
                    if (userLiveStream.TryGetValue(username, out var lst))
                    {
                        List<PushMsg> copy;
                        lock (lst) copy = lst.ToList();
                        foreach (var m in copy)
                        {
                            try { attachWriter.Write(JsonSerializer.Serialize(m, AppJsonContext.Default.PushMsg) + "|||END|||"); } catch { }
                        }
                    }

                    // 保持连接不断开，直到任务彻底执行完毕释放锁
                    while (userLocks.TryGetValue(username, out var lck2) && lck2.CurrentCount < 100)
                    {
                        await Task.Delay(1000);
                        try { attachWriter.Write(" "); } catch { break; }
                    }
                    userConnections.TryRemove(username, out _);
                    break;
                }

            case "/api/chat":
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                {
                    var body = await reader.ReadToEndAsync();
                    var chatReqObj = JsonSerializer.Deserialize(body, AppJsonContext.Default.ChatReq);

                    res.ContentType = "text/plain; charset=utf-8";
                    res.SendChunked = true;

                    using var writer = new StreamWriter(res.OutputStream, new UTF8Encoding(false));
                    writer.AutoFlush = true;

                    // 绑定当前的通信句柄
                    userConnections[username] = (msg) =>
                    {
                        try { writer.Write(JsonSerializer.Serialize(msg, AppJsonContext.Default.PushMsg) + "|||END|||"); } catch { }
                    };

                    if (chatReqObj != null)
                    {
                        // 去掉了老版本的 onUpdate 参数传递
                        await RunAgent(chatReqObj.Message, false, chatReqObj.ModelIndex, username);
                    }
                    userConnections.TryRemove(username, out _);
                }
                break;

            // --- 历史记录 ---
            case "/api/history":
                string historyJson = JsonSerializer.Serialize(GetHistory(username), AppJsonContext.Default.ListChatMessage);
                res.ContentType = "application/json";
                byte[] hBytes = Encoding.UTF8.GetBytes(historyJson);
                await res.OutputStream.WriteAsync(hBytes, 0, hBytes.Length);
                break;

            // --- 接收友军皮皮虾的任务指派 ---
            case "/api/agent_task" when req.HttpMethod == "POST":
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                {

                    var body = await reader.ReadToEndAsync();
                    var chatReqObj = JsonSerializer.Deserialize(body, AppJsonContext.Default.ChatReq);
                    if (chatReqObj != null)
                    {
                        var caller = chatReqObj.Caller ?? "未知节点";
                        var injectMsg = $"[系统最高优先级提示：你的队友【{caller}】主动找你沟通/求助。注意：你目前可能正在挂起等待其他任务的结果，但请你立刻放下手头的事，优先解答【{caller}】的问题或处理它的请求，直接用自然语言回复，系统会自动将你的话通过通信链路返回给对方！]\n对方发来的消息：{chatReqObj.Message}";
                        var responseText = await RunAgent(injectMsg, false, chatReqObj.ModelIndex, username, chatReqObj.Caller);
                        res.ContentType = "text/plain; charset=utf-8";
                        var respBytes = Encoding.UTF8.GetBytes(responseText);
                        await res.OutputStream.WriteAsync(respBytes);
                    }
                }
                break;
            default:
                res.StatusCode = 404;
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Request Error] {ex.Message}");
        res.StatusCode = 500;
    }
    finally
    {
        // 无论成功失败，必须关闭连接释放资源
        try { res.Close(); } catch { }
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
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleRequestAsync(context, webPort);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Web Error] {ex.Message}");
                }
            });
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
    <meta name="color-scheme" content="light dark" />
    <script src="https://cdn.jsdelivr.net/npm/marked/marked.min.js"></script>
    <title>PiPiClaw</title>
    <script src="https://cdn.jsdelivr.net/npm/qrcodejs@1.0.0/qrcode.min.js"></script>
    <script>
        if (localStorage.getItem('theme') === 'light') {
            document.documentElement.setAttribute('data-theme', 'light');
        }
    </script>
    <style>
        :root {
            --bg-depth: #0e1420;
            --bg-mid: #0b101a;
            --bg-card: rgba(255, 255, 255, .08);
            --pipi-magenta: #5ac8fa;
            --pipi-cyan: #5ac8fa;
            --text-main: #e8edf5;
            --text-muted: #9aa6b6;
            --cyan-glow: rgba(90, 200, 250, .35);
            --font-mono: 'Fira Code', 'Consolas', 'Courier New', monospace;
            --shadow-color: rgba(0, 0, 0, .45);
            --shadow-color-hover: rgba(0, 0, 0, .55);
            --grid-line: rgba(255, 255, 255, .03);
            --glass-stroke: rgba(255, 255, 255, .12);
            --glass-surface: rgba(255, 255, 255, .06);
            --layer-solid: #101827;
            --input-bg: rgba(255, 255, 255, .07);
            --input-focus: rgba(255, 255, 255, .14);
            --input-border: rgba(255, 255, 255, .12);
            --chat-box-bg: rgba(255, 255, 255, .07);
            --chat-box-border: rgba(255, 255, 255, .12);
            --msg-user-bg: linear-gradient(145deg, rgba(90, 200, 250, .18), rgba(255, 255, 255, .06));
            --msg-ai-bg: linear-gradient(145deg, rgba(167, 139, 250, .18), rgba(255, 255, 255, .06));
            --term-bg: rgba(6, 10, 18, .9);
            --term-border: rgba(255, 255, 255, .1);
            --term-text: #c8e1ff;
            --term-action: #8bd1ff;
            --term-result: #9fb0c5;
            --cmd-bg: rgba(255, 255, 255, .05);
            --cmd-hover-bg: rgba(255, 255, 255, .1);
            --sb-track: rgba(255, 255, 255, .06);
            --sb-thumb: rgba(90, 200, 250, .35);
            --sb-thumb-hover: rgba(90, 200, 250, .65);
        }
        [data-theme="light"] {
            --bg-depth: #abe2b5;
            --bg-mid: #bed6f5;
            --bg-card: rgba(255, 255, 255, .9);
            --pipi-magenta: #418dde;
            --pipi-cyan: #4a90e2;
            --text-main: #1f2a3d;
            --text-muted: #596273;
            --cyan-glow: rgba(74, 144, 226, .35);
            --shadow-color: rgba(0, 0, 0, .08);
            --shadow-color-hover: rgba(0, 0, 0, .12);
            --grid-line: rgba(255, 255, 255, .25);
            --glass-stroke: rgba(255, 255, 255, .5);
            --glass-surface: rgba(255, 255, 255, .72);
            --layer-solid: #edf1f7;
            --input-bg: rgba(255, 255, 255, .85);
            --input-focus: #ffffff;
            --input-border: rgba(0, 0, 0, .08);
            --chat-box-bg: rgba(255, 255, 255, .82);
            --chat-box-border: rgba(0, 0, 0, .08);
            --msg-user-bg: linear-gradient(145deg, #e3f2fd, #bbdefb);
            --msg-ai-bg: linear-gradient(145deg, #f5f5f5, #e0e0e0);
            --term-bg: #f6f7fb;
            --term-border: rgba(0, 0, 0, .08);
            --term-text: #2f3d4f;
            --term-action: #3b82f6;
            --term-result: #64748b;
            --cmd-bg: rgba(74, 144, 226, .08);
            --cmd-hover-bg: rgba(74, 144, 226, .16);
            --sb-track: rgba(0, 0, 0, .08);
            --sb-thumb: rgba(74, 144, 226, .4);
            --sb-thumb-hover: rgba(74, 144, 226, .65);
        }
        ::-webkit-scrollbar {
            display: none;
            width: 0;
            height: 0;
            background: transparent;
        }

        * {
            scrollbar-width: none; 
            -ms-overflow-style: none; 
        }
        html {
            background-color: var(--bg-depth);
            transition: background-color 0.4s ease;
        }
        * {
            box-sizing: border-box
        }
        body {
            font-family: var(--font-mono);
            background-color: var(--bg-depth);
            background-image: linear-gradient(135deg, var(--bg-depth), var(--bg-mid) 60%, var(--layer-solid));
            color: var(--text-main);
            margin: 0;
            padding: 20px;
            height: 100vh;
            height: 100dvh;
            display: flex;
            flex-direction: column;
            align-items: center;
            overflow: hidden;
            position: relative;
            text-size-adjust: 100%;
            -webkit-text-size-adjust: 100%;
            transition: background-color 0.4s ease, color 0.4s ease;
        }
        body::before {
            content: "";
            position: fixed;
            inset: 0;
            background: linear-gradient(120deg, rgba(255, 255, 255, .04), transparent 30%, rgba(255, 255, 255, .06) 60%, transparent 100%);
            pointer-events: none;
            z-index: 0;
            opacity: .85;
            backdrop-filter: blur(12px);
            transition: background 0.4s ease, opacity 0.4s ease;
        }
        body::after {
            content: "";
            position: fixed;
            inset: 0;
            background:
                radial-gradient(circle at 20% 20%, rgba(90, 200, 250, .14), transparent 35%),
                radial-gradient(circle at 80% 15%, rgba(124, 111, 241, .14), transparent 40%),
                radial-gradient(circle at 40% 75%, rgba(90, 200, 250, .12), transparent 40%);
            z-index: -1;
            filter: blur(20px);
        }
        .container {
            width: 100%;
            max-width: 1000px;
            z-index: 2;
            display: flex;
            flex-direction: column;
            gap: 8px;
            flex: 1;
            min-height: 0;
        }
        .header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            animation: slideDown .6s ease-out;
            gap: 12px;
        }
       .header h1 {
    font-size: 2.4em;
    font-weight: 800;
    margin: 0;
    letter-spacing: 2px;
    display: inline-flex;
    align-items: center;
    gap: 12px;
    justify-content: center;
    flex: 1;
    text-align: center;
    /* 解决层级和投影冲突，投影移到伪元素或父级处理更安全，这里先去掉了导致 bug 的 drop-shadow */
}

.header h1 span {
    background: linear-gradient(120deg, var(--pipi-cyan), var(--pipi-magenta));
    background-size: 200% auto;
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
    /* 这里同时绑定流光 shineText 和 抖动 glitch 特效 */
    animation: shineText 8s ease infinite, glitch 5s infinite;
}
        .header p {
            color: var(--text-muted);
            font-size: .9em;
            margin-top: 6px;
            opacity: .9;
            letter-spacing: 1px;
        }
        .header-btn {
            background: var(--glass-surface);
            border: 1px solid var(--glass-stroke);
            color: var(--text-main);
            width: 46px;
            height: 46px;
            border-radius: 12px;
            cursor: pointer;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 1.1em;
            font-weight: 700;
            transition: all 0.3s ease;
            flex-shrink: 0;
            box-shadow: 0 12px 25px var(--shadow-color);
            backdrop-filter: blur(12px);
            letter-spacing: 0;
        }
        .header-btn:hover {
            transform: translateY(-1px) scale(1.03);
            box-shadow: 0 14px 28px var(--shadow-color-hover);
            border-color: var(--pipi-cyan);
        }
        .theme-toggle {
            font-size: 1.3em;
        }
        .collapse-toggle[aria-expanded="true"] {
            border-color: var(--pipi-cyan);
            color: var(--pipi-cyan);
            box-shadow: 0 0 10px rgba(0, 242, 254, .2);
        }
        .box {
            background: var(--glass-surface);
            padding: 25px;
            border-radius: 16px;
            border: 1px solid var(--glass-stroke);
            box-shadow:
                0 18px 40px var(--shadow-color),
                inset 0 1px 0 rgba(255, 255, 255, .12);
            position: relative;
            overflow: hidden;
            backdrop-filter: blur(14px);
            animation: slideUp .6s ease-out both;
            transition: transform .25s, box-shadow .25s, background .4s, border-color .4s;
        }
        .box:hover {
            box-shadow: 0 20px 46px var(--shadow-color-hover), inset 0 1px 0 rgba(255, 255, 255, .16);
            transform: translateY(-1px);
        }
        h2 {
            margin: 0 0 12px 0;
            color: var(--text-main);
            font-size: 1.05em;
            text-transform: uppercase;
            letter-spacing: 1px;
            display: flex;
            align-items: center;
            gap: 10px;
            transition: color 0.4s;
        }
        h2::before {
            content: "";
            width: 6px;
            height: 18px;
            background: var(--pipi-cyan);
            box-shadow: 0 0 10px var(--pipi-cyan);
            border-radius: 3px;
        }
        .logo-mark {
            width: 42px;
            height: 42px;
            object-fit: contain;
            border-radius: 7px;
        }
        .logo-badge {
            width: 22px;
            height: 22px;
            object-fit: contain;
            vertical-align: middle;
        }
        .collapsible .collapse-header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 12px;
        }
        .collapse-toggle {
            user-select: none;
        }
        .collapse-toggle:hover {
            box-shadow: 0 0 12px rgba(0, 242, 254, .25);
        }
        .collapse-body {
            margin-top: 15px;
        }
        .collapsible.collapsed .collapse-body {
            display: none;
        }
        .modal {
            position: fixed;
            inset: 0;
            background: rgba(12, 18, 30, .65);
            display: none;
            align-items: center;
            justify-content: center;
            padding: 20px;
            z-index: 2000;
            backdrop-filter: blur(14px);
        }
        .modal.show {
            display: flex;
        }
        .modal-dialog {
            width: min(820px, calc(100% - 30px));
            max-height: 90dvh;
            overflow: auto;
        }
        .modal-header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 12px;
            margin-bottom: 12px;
        }
        .modal-close {
            width: 40px;
            height: 40px;
            font-size: 0.9em;
        }
        .config-grid {
            display: grid;
            grid-template-columns: 1fr;
            gap: 15px;
        }
        .config-row {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 15px;
        }
        @media (max-width:600px) {
            .config-row {
                grid-template-columns: 1fr;
            }
        }
        label {
            display: block;
            color: var(--text-muted);
            font-size: .75em;
            margin-bottom: 6px;
            text-transform: uppercase;
            letter-spacing: 1px;
        }
        input {
            width: 100%;
            padding: 12px 14px;
            border-radius: 8px;
            border: 1px solid var(--input-border);
            background: var(--input-bg);
            color: var(--text-main);
            font-family: var(--font-mono);
            font-size: .95em;
            transition: all .2s;
        }
        input:focus {
            outline: none;
            border-color: var(--pipi-cyan);
            box-shadow: 0 0 15px rgba(0, 242, 254, .2);
            background: var(--input-focus);
        }
        .btn-submit {
            width: 100%;
            margin-top: 14px;
            padding: 12px 18px;
            border-radius: 10px;
            background: rgba(0, 242, 254, .1);
            border: 1px solid var(--pipi-cyan);
            color: var(--pipi-cyan);
            font-weight: 800;
            font-family: var(--font-mono);
            cursor: pointer;
            transition: all .25s;
            letter-spacing: 2px;
            text-transform: uppercase;
        }
        .btn-submit:hover {
            background: var(--pipi-cyan);
            color: #000;
            box-shadow: 0 0 25px rgba(0, 242, 254, .6);
            transform: translateY(-1px);
        }
        .btn-submit:active {
            transform: translateY(0);
        }
        #terminalBox {
            flex: 1;
            display: flex;
            flex-direction: column;
            min-height: 0;
        }
            &::-webkit-scrollbar {
                display: none;
            }
        .chat-box {

            flex: 1;
            min-height: 0;
            overflow-y: auto;
            padding: 5px;
            display: flex;
            flex-direction: column;
            gap: 18px;
            scroll-behavior: smooth;
            transition: background 0.4s, border-color 0.4s, box-shadow 0.4s;
        }
        .msg {
            max-width: 92%;
            display: flex;
            flex-direction: column;
            animation: msgPop .35s cubic-bezier(.175, .885, .32, 1.275) both;
        }
        .msg-header {
            font-size: .75em;
            margin-bottom: 6px;
            opacity: .9;
            display: flex;
            align-items: center;
            gap: 8px;
            text-transform: uppercase;
            font-weight: 800;
            letter-spacing: 1px;
        }
        .msg.user {
            align-self: flex-end;
        }
        .msg.user .msg-header {
            color: var(--pipi-cyan);
            justify-content: flex-end;
        }
        .msg.user .msg-content {
            background: var(--msg-user-bg);
            border: 1px solid rgba(255, 255, 255, .1);
            padding: 14px 16px;
            border-radius: 14px 14px 4px 14px;
            white-space: pre-wrap;
            word-break: break-word;
            transition: background 0.4s;
        }
        .msg.ai {
            align-self: flex-start;
            width: 100%;
        }
        .msg.ai .msg-header {
            color: var(--pipi-magenta);
        }
        .msg.ai .msg-content {
            background: var(--msg-ai-bg);
            border: 1px solid rgba(255, 255, 255, .08);
            padding: 14px 16px;
            border-radius: 14px 14px 14px 4px;
            white-space: pre-wrap;
            word-break: break-word;
            transition: background 0.4s;
        }
        .exec-terminal {
            background: var(--term-bg);
            border: 1px solid var(--term-border);
            border-radius: 12px;
            padding: 10px;
            margin: 10px 0 0 0;
            font-size: .82em;
            color: var(--term-text);
            height: 160px;
            overflow-y: auto;
            box-shadow: inset 0 1px 0 rgba(255, 255, 255, .08), 0 8px 20px var(--shadow-color);
            transition: all 0.4s;
        }
        .exec-terminal .log-action {
            color: var(--term-action);
            font-weight: 800;
            display: block;
            margin-bottom: 4px;
        }
        .exec-terminal .log-result {
            color: var(--term-result);
            display: block;
            margin-bottom: 10px;
            padding-left: 8px;
            border-left: 2px solid var(--chat-box-border);
        }
        .input-area {
            display: flex;
            gap: 12px;
            align-items: stretch;
            border-top: 1px solid var(--chat-box-border);
            padding-top: 8px;
            transition: border-color 0.4s;
        }
        .input-main {
            position: relative;
            flex: 1;
            display: flex;
            min-height: 64px;
        }
        textarea {
            width: 100%;
            min-height: 100px;
            max-height: 200px;
            background: var(--input-bg);
            border: 1px solid var(--input-border);
            color: var(--text-main);
            font-family: var(--font-mono);
            font-size: 1em;
            line-height: 1.5;
            padding: 14px 16px;
            border-radius: 14px;
            resize: vertical;
            outline: none;
            transition: all .2s;
            box-shadow: inset 0 1px 0 rgba(255, 255, 255, .08), 0 10px 24px var(--shadow-color);
        }
        textarea:disabled {
            opacity: 0.9;
            cursor: not-allowed;
        }
        textarea:focus {
            border-color: var(--pipi-cyan);
            background: var(--input-focus);
            box-shadow: 0 12px 26px var(--shadow-color-hover), inset 0 1px 0 rgba(255, 255, 255, .12);
        }
        .loading-overlay {
            position: absolute;
            inset: 0;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 10px;
            border-radius: 14px;
            background: var(--layer-solid);
            color: var(--pipi-cyan);
            font-size: .95em;
            letter-spacing: 1px;
            opacity: 0;
            visibility: hidden;
            pointer-events: none;
            transition: opacity .2s ease, visibility .2s ease;
            box-shadow: 0 12px 28px var(--shadow-color);
        }
        .loading-overlay.show {
            opacity: 1;
            visibility: visible;
            pointer-events: auto;
        }
        .loader-bars {
            display: inline-flex;
            gap: 6px;
            align-items: center;
        }
        .loader-bars span {
            width: 6px;
            height: 14px;
            background: var(--pipi-cyan);
            animation: barDance 1s infinite;
            border-radius: 3px;
        }
        .loader-bars span:nth-child(2) {
            animation-delay: .2s;
        }
        .loader-bars span:nth-child(3) {
            animation-delay: .4s;
        }
        .loader-text {
            white-space: nowrap;
            color: var(--text-main);
        }
        .btn-wrapper {
            width: 70px;
            min-height: 64px;
            display: flex;
            align-self: stretch;
        }
        .btn-send {
            width: 100%;
            height: 100%;
            border-radius: 6px;
            border: 1px solid var(--glass-stroke);
            background: linear-gradient(145deg, var(--pipi-cyan), #368ddc);
            color: #fff;
            cursor: pointer;
            display: flex;
            align-items: center;
            justify-content: center;
            transition: transform .2s, box-shadow .2s, background .3s;
            box-shadow: 0 12px 26px var(--shadow-color);
            backdrop-filter: blur(10px);
        }
        .btn-send:hover {
            transform: translateY(-1px) scale(1.02);
            box-shadow: 0 16px 30px var(--shadow-color-hover);
            background: linear-gradient(145deg, #5ac8fa, #368ddc);
        }
        .btn-send svg {
            width: 24px;
            height: 24px;
            fill: currentColor;
        }
        .btn-cancel {
            width: 100%;
            height: 100%;
            border-radius: 6px;
            border: 1px solid var(--glass-stroke);
            background: linear-gradient(145deg, var(--pipi-magenta), #6f5bd6);
            color: #fff;
            cursor: pointer;
            display: flex;
            align-items: center;
            justify-content: center;
            transition: transform .2s, box-shadow .2s, background .3s;
            box-shadow: 0 12px 26px var(--shadow-color);
            backdrop-filter: blur(10px);
        }
        .btn-cancel:hover {
            transform: translateY(-1px) scale(1.02);
            box-shadow: 0 16px 30px var(--shadow-color-hover);
            background: linear-gradient(145deg, #8f7df4, #6f5bd6);
        }
        .btn-cancel svg {
            width: 22px;
            height: 22px;
            fill: currentColor;
        }
        #qrcode-container {
            position: fixed;
            bottom: 30px;
            left: 30px;
            z-index: 999;
            background: var(--glass-surface);
            padding: 12px;
            border-radius: 14px;
            box-shadow: 0 14px 28px var(--shadow-color);
            border: 1px solid var(--glass-stroke);
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 8px;
            transition: transform .25s, box-shadow .25s;
            backdrop-filter: blur(10px);
        }
        #qrcode-container:hover {
            transform: translateY(-2px) scale(1.05);
            transform-origin: bottom left;
            box-shadow: 0 18px 36px var(--shadow-color-hover);
        }
        #qrcode-container span {
            color: var(--text-main);
            font-size: 10px;
            font-weight: 800;
            font-family: sans-serif;
        }
        .model-select {
            position: absolute;
            left: 12px;
            bottom: 12px;
            background: var(--input-bg);
            color: var(--pipi-cyan);
            border: 1px solid var(--input-border);
            border-radius: 8px;
            padding: 4px 8px;
            font-size: 0.8em;
            font-family: var(--font-mono);
            outline: none;
            cursor: pointer;
            font-weight: bold;
        }
        textarea {
            padding-bottom: 45px;
        }
        .config-model-item {
            border: 1px solid var(--glass-stroke);
            background: var(--cmd-bg);
            padding: 15px;
            border-radius: 12px;
            margin-bottom: 15px;
            position: relative;
        }
        .btn-remove-model {
            position: absolute;
            top: 2px;
            right: 2px;
            background: transparent;
            color: var(--pipi-magenta);
            border: none;
            cursor: pointer;
            font-size: 1.1em;
        }
        .btn-add-model {
            width: 100%;
            padding: 10px;
            background: rgba(90, 200, 250, 0.05);
            color: var(--pipi-cyan);
            border: 1px dashed var(--pipi-cyan);
            border-radius: 10px;
            cursor: pointer;
            margin-bottom: 20px;
            transition: all 0.2s;
        }
        .btn-add-model:hover {
            background: rgba(90, 200, 250, 0.15);
        }
        #tasksBox {
            flex-shrink: 0; /* 防止面板本身被异常压缩 */
        }
        #tasksContainer {
            max-height: 160px; /* 限制最大高度，避免撑爆页面 */
            overflow-y: auto;  /* 任务多了自动出滚动条 */
            padding-right: 6px;
        }
        #userLoginOverlay {
            position: fixed; inset: 0; background: var(--bg-depth); z-index: 9999;
            display: flex; flex-direction: column; align-items: center; justify-content: center;
        }
        .login-box {
            background: var(--glass-surface); padding: 30px; border-radius: 16px;
            border: 1px solid var(--pipi-cyan); text-align: center;
        }
        @media (max-width:600px) {
            :root {
                font-size: 15px;
            }
            body {
                padding: 10px;
            }
            .header h1 {
                font-size: 1.4em;
                letter-spacing: 1px;
            }
            .header-btn {
                width: 38px;
                height: 38px;
                font-size: 1em;
            }
            .box {
                padding: 15px;
            }
            .chat-box {
                padding: 12px;
            }
            #qrcode-container {
                display: none;
            }
            .btn-wrapper {
                width: 60px;
                min-height: 60px;
            }
            textarea {
                padding: 10px;
                font-size: .95em;
            }
        }
        @keyframes slideUp {
            from {
                opacity: 0;
                transform: translateY(40px)
            }
            to {
                opacity: 1;
                transform: translateY(0)
            }
        }
        @keyframes slideDown {
            from {
                opacity: 0;
                transform: translateY(-40px)
            }
            to {
                opacity: 1;
                transform: translateY(0)
            }
        }
        @keyframes msgPop {
            0% {
                opacity: 0;
                transform: scale(.95) translateY(14px)
            }
            100% {
                opacity: 1;
                transform: scale(1) translateY(0)
            }
        }
        @keyframes scanLight {
            0% {
                left: -100%
            }
            100% {
                left: 200%
            }
        }
        @keyframes breatheBg {
            0% {
                transform: scale(1);
                opacity: .5
            }
            100% {
                transform: scale(1.1);
                opacity: .8
            }
        }
        @keyframes barDance {
            0%,
            100% {
                height: 8px;
                background: var(--pipi-cyan)
            }
            50% {
                height: 20px;
                background: var(--pipi-magenta)
            }
        }
        @keyframes shineText {
            0% {
                background-position: 0% center
            }

            100% {
                background-position: 200% center
            }
        }
        @keyframes glitch {
            0%,
            100% {
                transform: none
            }
            92% {
                transform: translate(1px, 1px) skewX(2deg);
                filter: hue-rotate(90deg)
            }
            94% {
                transform: translate(-1px, -1px) skewX(-2deg)
            }
            96% {
                transform: translate(2px, 0) skewX(0)
            }
        }
        .intro-text {
            line-height: 1.6;
            margin-bottom: 12px;
        }
        .intro-text strong {
            color: var(--pipi-cyan);
            font-weight: normal;
        }
        .cmd-suggestions {
            display: flex;
            flex-direction: column;
            gap: 10px;
            margin-top: 15px;
        }
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
            box-shadow: 0 4px 15px rgba(0, 242, 254, 0.15);
            border-left-color: var(--pipi-magenta);
        }
        .cmd-item::before {
            content: ">_ ";
            color: var(--pipi-magenta);
            font-weight: bold;
            margin-right: 5px;
        }
        .btn-col {
            display: flex;
            flex-direction: column;
            gap: 8px;
            width: 70px;
            align-self: stretch;
        }
        .btn-tasks {
            width: 100%;
            height: 28px;
            border-radius: 6px;
            border: 1px solid var(--glass-stroke);
            background: rgba(167, 139, 250, 0.1);
            color: var(--pipi-magenta);
            font-size: 0.75em;
            font-weight: bold;
            cursor: pointer;
            transition: all 0.2s;
            backdrop-filter: blur(10px);
            flex-shrink: 0;
        }
        .btn-tasks:hover {
            background: rgba(167, 139, 250, 0.25);
            border-color: var(--pipi-magenta);
        }
        .btn-wrapper {
            width: 100%;
            flex: 1; 
            min-height: 50px;
        }
        @media (max-width:600px) {
            .btn-col { width: 60px; }
            .btn-tasks { font-size: 0.7em; height: 26px; }
            .btn-wrapper { min-height: 46px; }
        }


.footer-attribution {
    text-align: center;
    font-size: 0.75em;
    color: var(--text-muted);
    margin-top: 12px;
    padding-top: 10px;
    border-top: 1px dashed var(--glass-stroke);
    display: flex;
    justify-content: center;
    align-items: center;
    gap: 12px;
    opacity: 0.6;
    transition: opacity 0.3s ease;
    letter-spacing: 0.5px;
}
.footer-attribution:hover {
    opacity: 1;
}
.footer-attribution strong {
    color: var(--pipi-cyan);
    font-weight: 600;
}
.footer-attribution a {
    color: var(--text-main);
    text-decoration: none;
    display: flex;
    align-items: center;
    transition: color 0.3s, text-shadow 0.3s, transform 0.2s;
}
.footer-attribution a:hover {
    color: var(--pipi-magenta);
    text-shadow: 0 0 10px rgba(167, 139, 250, 0.4);
    transform: translateY(-1px);
}
.footer-attribution .divider {
    color: var(--glass-stroke);
}
/* ==================== 关于面板专属样式 ==================== */
.about-content {
    text-align: center;
    padding: 15px 0 5px 0;
}
.about-badges {
    display: flex;
    justify-content: center;
    flex-wrap: wrap;
    gap: 10px;
    margin: 18px 0;
}
.about-badge {
    background: linear-gradient(135deg, #10b981, #059669); /* 强调绿色的极简感 */
    color: #fff;
    padding: 6px 14px;
    border-radius: 20px;
    font-size: 0.85em;
    font-weight: 800;
    box-shadow: 0 6px 15px rgba(16, 185, 129, 0.25);
    letter-spacing: 1px;
    border: 1px solid rgba(255, 255, 255, 0.2);
}
.about-badge.usb {
    background: linear-gradient(135deg, var(--pipi-cyan), #0284c7);
    box-shadow: 0 6px 15px rgba(2, 132, 199, 0.25);
}
.about-desc {
    color: var(--text-muted);
    font-size: 0.9em;
    line-height: 1.7;
    margin: 15px 0;
    background: rgba(0,0,0,0.1);
    padding: 15px;
    border-radius: 12px;
    border: 1px dashed var(--glass-stroke);
}
[data-theme="light"] .about-desc {
    background: rgba(255,255,255,0.5);
}
.about-links {
    display: flex;
    flex-direction: column;
    gap: 12px;
    margin-top: 25px;
}
.btn-about-link {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 10px;
    padding: 14px;
    border-radius: 12px;
    text-decoration: none;
    font-weight: 800;
    font-size: 0.95em;
    transition: all 0.3s cubic-bezier(0.175, 0.885, 0.32, 1.275);
    letter-spacing: 1px;
}
.btn-github {
    background: rgba(255, 255, 255, 0.05);
    border: 1px solid var(--glass-stroke);
    color: var(--text-main);
}
.btn-github:hover {
    background: rgba(255, 255, 255, 0.1);
    transform: translateY(-2px) scale(1.02);
    box-shadow: 0 8px 20px var(--shadow-color);
}
.btn-qq {
    background: linear-gradient(135deg, #00a4ff, #0088cc);
    color: white !important;
    border: none;
    box-shadow: 0 6px 20px rgba(0, 164, 255, 0.3);
}
.btn-qq:hover {
    transform: translateY(-2px) scale(1.02);
    box-shadow: 0 8px 25px rgba(0, 164, 255, 0.45);
}
/* --- 加群二维码专属样式 --- */
.qq-qr-container {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    margin-top: 15px;
    padding: 15px;
    background: rgba(255, 255, 255, 0.03);
    border: 1px dashed var(--glass-stroke);
    border-radius: 14px;
    transition: transform 0.3s cubic-bezier(0.175, 0.885, 0.32, 1.275), box-shadow 0.3s, background 0.3s;
}
.qq-qr-container:hover {
    transform: translateY(-2px) scale(1.02);
    box-shadow: 0 10px 25px var(--shadow-color);
    background: rgba(255, 255, 255, 0.06);
    border-color: var(--pipi-cyan);
}
.qq-qr-box {
    background: #ffffff;
    padding: 8px;
    border-radius: 10px;
    margin-bottom: 12px;
    box-shadow: 0 4px 15px rgba(0, 0, 0, 0.15);
}
.qq-qr-text {
    font-size: 0.85em;
    font-weight: 800;
    color: var(--text-muted);
    letter-spacing: 1.5px;
}
[data-theme="light"] .qq-qr-container {
    background: rgba(0, 0, 0, 0.02);
}
[data-theme="light"] .qq-qr-container:hover {
    background: rgba(0, 0, 0, 0.06);
}

/* ==================== Agent 切换器样式 ==================== */
.agent-switcher {
    background: var(--glass-surface);
    border: 1px solid var(--glass-stroke);
    color: var(--pipi-cyan);
    border-radius: 8px;
    padding: 6px 12px;
    font-size: 0.9em;
    font-family: var(--font-mono);
    font-weight: 800;
    outline: none;
    cursor: pointer;
    backdrop-filter: blur(12px);
    margin: 0 10px;
    box-shadow: 0 4px 15px var(--shadow-color);
    transition: all 0.3s ease;
    max-width: 180px;
    text-overflow: ellipsis;
}
.agent-switcher:hover, .agent-switcher:focus {
    border-color: var(--pipi-cyan);
    box-shadow: 0 6px 20px rgba(90, 200, 250, 0.2);
    background: rgba(255, 255, 255, 0.1);
}
.agent-switcher option {
    background: var(--bg-depth);
    color: var(--text-main);
    font-weight: bold;
}


    </style>
</head>
<body>
    <div id="userLoginOverlay">
        <div class="login-box">
            <h2 style="color:var(--pipi-cyan);">请输入认领的皮皮虾名</h2>
            <input type="text" id="usernameInput" placeholder="例如：雷霆皮皮虾" style="margin-bottom:15px;"/>
            <button class="btn-submit" onclick="confirmUsername()">进入系统</button>
        </div>
    </div>
    <div id="qrcode-container" title="手机扫码中控">
        <div id="qrcode"></div>
        <span>手机扫码打开这个界面</span>
    </div>
    <div class="container">
       <div class="header">
            <button type="button" class="header-btn collapse-toggle" id="configToggle" onclick="toggleConfig()"
                aria-expanded="false" title="展开/收起设置">⚙</button>
            <button type="button" class="header-btn" onclick="clearContext()" title="手动清理上下文记忆">🧹</button>
            <h1>
                <span>PiPiClaw</span>
            </h1>
            <select id="agentSwitcher" class="agent-switcher" onchange="switchAgent(this.value)" title="切换当前工作的 Agent 身份">
                <option value="">载入中...</option>
            </select>
            <div style="display: flex; gap: 8px;">
                <button class="header-btn" onclick="toggleAboutModal()" title="关于 PiPiClaw">💡</button>
                <button class="header-btn theme-toggle" onclick="toggleTheme()" aria-label="切换主题" title="切换深/浅色主题">
                    <span id="theme-icon">🌙</span>
                </button>
            </div>
        </div>
        <div class="box" id="terminalBox" style="animation-delay:.2s;">
            <h2><span style="color:var(--pipi-magenta);">⌨️</span> 交互终端 (Terminal)</h2>
            <div class="chat-box" id="chatBox">
                <div class="msg ai">
                    <div class="msg-header">
                        皮皮虾 // 系统
                    </div>
                    <div class="msg-content"><div class="intro-text" style="margin-bottom: 12px; line-height: 1.5;"><strong>神经链接已建立。等待指令……</strong><br><br>【简介与食用指南】<br>这是一个能够全自动执行终端命令、读写文件、规划任务的本地 AI 智能体，能力不限于运维。<br>只要像吩咐人类一样说话，它就会自己写脚本、查日志、执行系统命令或调用 Skill-Hub 上的一万+ 生态技能来帮你办事。<br><br><span style="color:var(--text-muted); font-size: 0.9em;">试试直接点击或粘贴以下命令：</span></div><div class="cmd-suggestions" style="display: flex; flex-direction: column; gap: 8px;"><div class="cmd-item" onclick="document.getElementById('chatInput').value='帮我扫描一下当前目录，看有没有 C# 相关的源码文件';">帮我扫描一下当前目录，看有没有 C# 相关的源码文件</div><div class="cmd-item" onclick="document.getElementById('chatInput').value='用 C# 写一个能控制树莓派 GPIO 针脚电平的简单脚本，并帮我运行它测试一下';">用 C# 写一个能控制树莓派 GPIO 针脚电平的简单脚本，并帮我运行它测试一下</div><div class="cmd-item" onclick="document.getElementById('chatInput').value='帮我查一下系统当前的内存占用情况，并把结果写进 memory_log.txt';">帮我查一下系统当前的内存占用情况，并把结果写进 memory_log.txt</div><div class="cmd-item" onclick="document.getElementById('chatInput').value='每天下午3点，帮我屏幕截图看一下我在干什么？';">每天下午3点，帮我屏幕截图看一下我在干什么？</div></div></div>
                    </div>
                </div>
            </div>
            <div class="input-area">
                <div class="input-main">
                    <textarea id="chatInput" placeholder="输入任务指令... (按 Enter 发送)"></textarea>
                    <select id="modelSelect" class="model-select"></select>
                    <div class="loading-overlay" id="loadingOverlay" aria-live="polite">
                        <div class="loader-bars"><span></span><span></span><span></span></div>
                        <span class="loader-text">正在烹饪中...</span>
                    </div>
                </div>
                <div class="btn-col">
                    <button type="button" class="btn-tasks" onclick="toggleTasksModal()" title="任务管理面板"> ⏰ </button>

                    <div class="btn-wrapper" id="sendWrapper">
                        <button class="btn-send" type="button" onclick="sendMsg()" aria-label="Send">
                            <svg viewBox="0 0 24 24"><path d="M2.01 21L23 12 2.01 3 2 10l15 2-15 2z"></path></svg>
                        </button>
                    </div>
                    <div class="btn-wrapper" id="cancelWrapper" style="display:none;">
                        <button class="btn-cancel" type="button" onclick="cancelTask()" aria-label="Cancel">
                            <svg viewBox="0 0 24 24"><path d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z" /></svg>
                        </button>
                    </div>
                </div>
            </div>
            <div class="footer-attribution">
                <span>Made with 🧋 by <strong>奶茶叔叔</strong></span>
                <span class="divider">/</span>
                <a href="https://github.com/anan1213095357/PiPiClaw" target="_blank" title="前往 GitHub 查看源码">
                    <svg viewBox="0 0 24 24" width="15" height="15" fill="currentColor" style="vertical-align: middle; margin-right: 5px;"><path d="M12 2C6.477 2 2 6.477 2 12c0 4.42 2.865 8.166 6.839 9.489.5.092.682-.217.682-.482 0-.237-.008-.866-.013-1.7-2.782.603-3.369-1.34-3.369-1.34-.454-1.156-1.11-1.462-1.11-1.462-.908-.62.069-.608.069-.608 1.003.07 1.531 1.03 1.531 1.03.892 1.529 2.341 1.087 2.91.831.092-.646.35-1.086.636-1.336-2.22-.253-4.555-1.11-4.555-4.943 0-1.091.39-1.984 1.029-2.683-.103-.253-.446-1.27.098-2.647 0 0 .84-.269 2.75 1.025A9.578 9.578 0 0112 6.836c.85.004 1.705.114 2.504.336 1.909-1.294 2.747-1.025 2.747-1.025.546 1.377.203 2.394.1 2.647.64.699 1.028 1.592 1.028 2.683 0 3.842-2.339 4.687-4.566 4.935.359.309.678.919.678 1.852 0 1.336-.012 2.415-.012 2.743 0 .267.18.578.688.48C19.138 20.161 22 16.416 22 12c0-5.523-4.477-10-10-10z"></path></svg>
                    PiPiClaw 开放源代码
                </a>
            </div>
            </div> </div>


        </div>
    </div>
    <div class="modal" id="tasksModal" aria-hidden="true" role="dialog" aria-labelledby="tasksTitle">
        <div class="modal-dialog box" style="animation:none; max-width: 600px;">
            <div class="modal-header">
                <h2 id="tasksTitle"><span style="color:var(--pipi-magenta);">⏰</span> 任务调度中心 (Tasks)</h2>
                <button type="button" class="header-btn modal-close" onclick="closeTasksModal()" aria-label="关闭面板">✖</button>
            </div>
            <div class="collapse-body" style="max-height: 60vh; overflow-y: auto; padding-right: 5px;">
                <div>
                    <h3 style="font-size: 0.9em; color: var(--text-main); margin: 0 0 10px 0; display:flex; justify-content:space-between;">
                        <span>⏳ 延时任务 (< 2小时)</span>
                    </h3>
                    <div id="delayedTasksContainer" style="margin-bottom: 25px;"></div>
                </div>
                <div style="border-top: 1px dashed var(--glass-stroke); padding-top: 15px;">
                    <h3 style="font-size: 0.9em; color: var(--text-main); margin: 0 0 10px 0;">📅 定时任务 (及长周期任务)</h3>
                    <div id="scheduledTasksContainer"></div>
                </div>
            </div>
        </div>
    </div>
    <div class="modal" id="configModal" aria-hidden="true" role="dialog" aria-labelledby="configTitle">
        <div class="modal-dialog box" id="configBox" style="animation:none;">
            <div class="modal-header">
                <h2 id="configTitle"><span style="color:var(--pipi-cyan);">🛰️</span> 核心链路配置 (Config)</h2>
                <button type="button" class="header-btn modal-close" onclick="closeConfig()"
                    aria-label="关闭设置">✖</button>
            </div>
            <div class="collapse-body" id="configBody">
                <div class="config-grid">
                    <div>
                        <h3 style="font-size: 0.9em; color: var(--text-main); margin: 0 0 10px 0;">🤖 大模型配置</h3>
                        <div
                            style="color:var(--text-muted); font-size:0.8em;">
                            ⚠️ 提示：端点地址 (Endpoint) 必须兼容 OpenAI API 格式。
                        </div>
                    </div>

                    <div id="modelsConfigContainer"></div>
                    <button type="button" class="btn-add-model" onclick="addModelConfigUI()">+ 添加一个新的模型节点配置</button>
                    <div style="margin-top: 20px; border-top: 1px dashed var(--glass-stroke); padding-top: 20px;">
                        <h3 style="font-size: 0.9em; color: var(--text-main); margin: 0 0 10px 0;">📦 技能下载节点配置 <a href="https://www.clawhub.ai" target="_blank" style="float: right; display: inline-block; padding: 8px 18px; font-size: 14px; font-weight: 500; color: #1f2937; text-decoration: none; background: rgba(255, 255, 255, 0.25); backdrop-filter: blur(12px); -webkit-backdrop-filter: blur(12px); border: 1px solid rgba(255, 255, 255, 0.6); border-radius: 8px; box-shadow: 0 4px 12px rgba(0, 0, 0, 0.05); letter-spacing: 0.5px;">点我进入技能市场</a></h3>
                        <div style="color:var(--text-muted); font-size:0.75em; margin-bottom:15px;">
                          ⚠️ 提示：请使用 {slug} 作为技能名称的占位符。安装时将从上到下按顺序尝试。
                        </div>
                        <div id="urlsConfigContainer"></div>
                        <button type="button" class="btn-add-model" onclick="addUrlConfigUI()">+ 添加一个新的下载节点</button>
                    </div>
                    <div style="border-top: 1px dashed var(--glass-stroke); "></div>
                    
                    <div style="margin-top: 20px; border-top: 1px dashed var(--glass-stroke); padding-top: 20px;">
                        <h3 style="font-size: 0.9em; color: var(--text-main); margin: 0 0 10px 0;">🤝 友军通讯录配置 (Peer Nodes)</h3>
                        <div style="color:var(--text-muted); font-size:0.75em; margin-bottom:15px;">
                          ⚠️ 提示：在此处登记队友名称和 URL，以后让它“安排任务给XXX”时，它会自己查通讯录，无需你每次发地址。
                        </div>
                        <div id="peerNodesConfigContainer"></div>
                        <button type="button" class="btn-add-model" onclick="addPeerNodeRow()">+ 添加一个新的友军节点</button>
                    </div>

                    <div style="border-top: 1px dashed var(--glass-stroke); "></div>
                    
                    <div>
                        <h3 style="font-size: 0.9em; color: var(--text-main); margin: 0 0 10px 0;">🔧 运维配置</h3>
                        <div style="color:var(--text-muted); font-size:0.75em;">
                              ⚠️ 提示：密码将用于全自动执行sh脚本，端口请勿确保被占用！。
                        </div>
                    </div>
                    <div class="config-row">
                        <div class="form-group">
                            <label>提权密码 (SudoPassword)</label>
                            <input type="password" id="sudoPassword" placeholder="自动执行 sudo 时使用的密码" />
                        </div>
                        <div class="form-group">
                            <label>Web 端口 (WebPort)</label>
                            <input type="number" id="webPort" placeholder="5050" min="1" max="65535" />
                        </div>
                        <div style="margin-top: 15px;grid-column: span 2;">
                            <label>自定义系统提示词 (System Prompt)</label>
                            <textarea id="systemPrompt" placeholder="留空则使用默认人设。例如：你是一个二次元猫娘程序员..." style="width: 100%; min-height: 80px;"></textarea>
                        </div>
                    </div>
                </div>
                <button class="btn-submit" type="button" onclick="saveConfig()">保存并上传配置</button>
            </div>
        </div>
    </div>
    <div class="modal" id="aboutModal" aria-hidden="true" role="dialog" aria-labelledby="aboutTitle">
    <div class="modal-dialog box" style="animation:none; max-width: 480px;">
        <div class="modal-header">
            <h2 id="aboutTitle"><span style="color:var(--pipi-cyan);">💡</span> 探索 PiPiClaw</h2>
            <button type="button" class="header-btn modal-close" onclick="closeAboutModal()" aria-label="关闭面板">✖</button>
        </div>
        <div class="collapse-body about-content">
            <h3 style="font-size: 1.8em; margin: 0 0 5px 0; color: var(--text-main); font-weight: 900; letter-spacing: 1px;">皮皮虾 PiPiClaw</h3>
            <div style="color: var(--pipi-cyan); font-weight: 800; font-size: 0.95em;">跨平台全能本地 AI 自动化终端</div>

            <div class="about-badges">
                <span class="about-badge">🌱 极简绿色免安装</span>
                <span class="about-badge usb">💾 随身U盘便携版</span>
            </div>

            <div class="about-desc">
                <strong>无依赖 · 零污染 · 超小体积</strong><br/><br/>
                拔插即用，将我装进U盘即可带走你的专属智能体。<br/>
                随时随地接管系统、全自动执行脚本、秒级调用 <strong>Skill-Hub 10000+</strong> 生态技能库。
            </div>

            <div class="about-links">
                <a href="https://github.com/anan1213095357/PiPiClaw" target="_blank" class="btn-about-link btn-github">
                    <svg viewBox="0 0 24 24" width="22" height="22" fill="currentColor"><path d="M12 2C6.477 2 2 6.477 2 12c0 4.42 2.865 8.166 6.839 9.489.5.092.682-.217.682-.482 0-.237-.008-.866-.013-1.7-2.782.603-3.369-1.34-3.369-1.34-.454-1.156-1.11-1.462-1.11-1.462-.908-.62.069-.608.069-.608 1.003.07 1.531 1.03 1.531 1.03.892 1.529 2.341 1.087 2.91.831.092-.646.35-1.086.636-1.336-2.22-.253-4.555-1.11-4.555-4.943 0-1.091.39-1.984 1.029-2.683-.103-.253-.446-1.27.098-2.647 0 0 .84-.269 2.75 1.025A9.578 9.578 0 0112 6.836c.85.004 1.705.114 2.504.336 1.909-1.294 2.747-1.025 2.747-1.025.546 1.377.203 2.394.1 2.647.64.699 1.028 1.592 1.028 2.683 0 3.842-2.339 4.687-4.566 4.935.359.309.678.919.678 1.852 0 1.336-.012 2.415-.012 2.743 0 .267.18.578.688.48C19.138 20.161 22 16.416 22 12c0-5.523-4.477-10-10-10z"></path></svg>
                    探索 GitHub 开源主页
                </a>
                <a href="https://qm.qq.com/q/kqkAVjWuQg" target="_blank" class="btn-about-link btn-qq">
                    <svg t="1774312802555" class="icon" viewBox="0 0 1024 1024" version="1.1" xmlns="http://www.w3.org/2000/svg" p-id="1954" width="22" height="22"><path d="M511.09761 957.257c-80.159 0-153.737-25.019-201.11-62.386-24.057 6.702-54.831 17.489-74.252 30.864-16.617 11.439-14.546 23.106-11.55 27.816 13.15 20.689 225.583 13.211 286.912 6.767v-3.061z" fill="#FAAD08" p-id="1955"></path><path d="M496.65061 957.257c80.157 0 153.737-25.019 201.11-62.386 24.057 6.702 54.83 17.489 74.253 30.864 16.616 11.439 14.543 23.106 11.55 27.816-13.15 20.689-225.584 13.211-286.914 6.767v-3.061z" fill="#FAAD08" p-id="1956"></path><path d="M497.12861 474.524c131.934-0.876 237.669-25.783 273.497-35.34 8.541-2.28 13.11-6.364 13.11-6.364 0.03-1.172 0.542-20.952 0.542-31.155C784.27761 229.833 701.12561 57.173 496.64061 57.162 292.15661 57.173 209.00061 229.832 209.00061 401.665c0 10.203 0.516 29.983 0.547 31.155 0 0 3.717 3.821 10.529 5.67 33.078 8.98 140.803 35.139 276.08 36.034h0.972z" fill="#000000" p-id="1957"></path><path d="M860.28261 619.782c-8.12-26.086-19.204-56.506-30.427-85.72 0 0-6.456-0.795-9.718 0.148-100.71 29.205-222.773 47.818-315.792 46.695h-0.962C410.88561 582.017 289.65061 563.617 189.27961 534.698 185.44461 533.595 177.87261 534.063 177.87261 534.063 166.64961 563.276 155.56661 593.696 147.44761 619.782 108.72961 744.168 121.27261 795.644 130.82461 796.798c20.496 2.474 79.78-93.637 79.78-93.637 0 97.66 88.324 247.617 290.576 248.996a718.01 718.01 0 0 1 5.367 0C708.80161 950.778 797.12261 800.822 797.12261 703.162c0 0 59.284 96.111 79.783 93.637 9.55-1.154 22.093-52.63-16.623-177.017" fill="#000000" p-id="1958"></path><path d="M434.38261 316.917c-27.9 1.24-51.745-30.106-53.24-69.956-1.518-39.877 19.858-73.207 47.764-74.454 27.875-1.224 51.703 30.109 53.218 69.974 1.527 39.877-19.853 73.2-47.742 74.436m206.67-69.956c-1.494 39.85-25.34 71.194-53.24 69.956-27.888-1.238-49.269-34.559-47.742-74.435 1.513-39.868 25.341-71.201 53.216-69.974 27.909 1.247 49.285 34.576 47.767 74.453" fill="#FFFFFF" p-id="1959"></path><path d="M683.94261 368.627c-7.323-17.609-81.062-37.227-172.353-37.227h-0.98c-91.29 0-165.031 19.618-172.352 37.227a6.244 6.244 0 0 0-0.535 2.505c0 1.269 0.393 2.414 1.006 3.386 6.168 9.765 88.054 58.018 171.882 58.018h0.98c83.827 0 165.71-48.25 171.881-58.016a6.352 6.352 0 0 0 1.002-3.395c0-0.897-0.2-1.736-0.531-2.498" fill="#FAAD08" p-id="1960"></path><path d="M467.63161 256.377c1.26 15.886-7.377 30-19.266 31.542-11.907 1.544-22.569-10.083-23.836-25.978-1.243-15.895 7.381-30.008 19.25-31.538 11.927-1.549 22.607 10.088 23.852 25.974m73.097 7.935c2.533-4.118 19.827-25.77 55.62-17.886 9.401 2.07 13.75 5.116 14.668 6.316 1.355 1.77 1.726 4.29 0.352 7.684-2.722 6.725-8.338 6.542-11.454 5.226-2.01-0.85-26.94-15.889-49.905 6.553-1.579 1.545-4.405 2.074-7.085 0.242-2.678-1.834-3.786-5.553-2.196-8.135" fill="#000000" p-id="1961"></path><path d="M504.33261 584.495h-0.967c-63.568 0.752-140.646-7.504-215.286-21.92-6.391 36.262-10.25 81.838-6.936 136.196 8.37 137.384 91.62 223.736 220.118 224.996H506.48461c128.498-1.26 211.748-87.612 220.12-224.996 3.314-54.362-0.547-99.938-6.94-136.203-74.654 14.423-151.745 22.684-215.332 21.927" fill="#FFFFFF" p-id="1962"></path><path d="M323.27461 577.016v137.468s64.957 12.705 130.031 3.91V591.59c-41.225-2.262-85.688-7.304-130.031-14.574" fill="#EB1C26" p-id="1963"></path><path d="M788.09761 432.536s-121.98 40.387-283.743 41.539h-0.962c-161.497-1.147-283.328-41.401-283.744-41.539l-40.854 106.952c102.186 32.31 228.837 53.135 324.598 51.926l0.96-0.002c95.768 1.216 222.4-19.61 324.6-51.924l-40.855-106.952z" fill="#EB1C26" p-id="1964"></path></svg>
                    加入官方交流群聊
                </a>
                <div class="qq-qr-container" title="微信/QQ扫码加群">
                    <div id="qq-qrcode" class="qq-qr-box"></div>
                    <div class="qq-qr-text">扫码加入官方交流群</div>
                </div>
            </div>
        </div>
    </div>
</div>
    <script>
        let currentUsername = localStorage.getItem('username') || '';


        function confirmUsername() {
            let val = document.getElementById('usernameInput').value.trim();
            if(!val) return alert("用户名不能为空！");
            currentUsername = val;
            localStorage.setItem('username', val);
            document.getElementById('userLoginOverlay').style.display = 'none';
            loadConfig(); loadHistory(); // 此时再加载历史记录
        }

        // 覆盖原生 fetch，给所有 api 请求自动带上 X-Username
        const originalFetch = window.fetch;
        window.fetch = async function() {
            let [resource, config] = arguments;
            if(resource.includes('/api/')) {
                config = config || {};
                config.headers = config.headers || {};
                config.headers['X-Username'] = encodeURIComponent(currentUsername);
            }
            return originalFetch(resource, config);
        };
        // 删除了旧的 logo 注入逻辑，新增了关于面板控制逻辑
        const aboutModal = document.getElementById('aboutModal');
        function openAboutModal() {
            if (!aboutModal) return;
            aboutModal.classList.add('show');
            aboutModal.setAttribute('aria-hidden', 'false');
        }
        function closeAboutModal() {
            if (!aboutModal) return;
            aboutModal.classList.remove('show');
            aboutModal.setAttribute('aria-hidden', 'true');
        }
        function toggleAboutModal() {
            if (aboutModal && aboutModal.classList.contains('show')) closeAboutModal();
            else openAboutModal();
        }
        if (aboutModal) {
            aboutModal.addEventListener('click', (e) => {
                if (e.target === aboutModal) closeAboutModal();
            });
        }

        function toggleTheme() {
            const root = document.documentElement;
            const icon = document.getElementById('theme-icon');
            if (root.getAttribute('data-theme') === 'light') {
                root.removeAttribute('data-theme');
                if (icon) icon.innerText = '🌙';
                localStorage.setItem('theme', 'dark');
            } else {
                root.setAttribute('data-theme', 'light');
                if (icon) icon.innerText = '☀️';
                localStorage.setItem('theme', 'light');
            }
        }
        window.addEventListener('DOMContentLoaded', () => {
            if (localStorage.getItem('theme') === 'light') {
                const icon = document.getElementById('theme-icon');
                if (icon) icon.innerText = '☀️';
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
            if (e.key === 'Escape') { closeConfig(); closeTasksModal(); closeAboutModal(); }
        });
        const host = window.location.hostname;
        const targetUrl = (host === 'localhost' || host === '127.0.0.1')
            ? 'http://{{LAN_IP}}:{{WEB_PORT}}/'
            : window.location.href;
        if (typeof QRCode !== 'undefined') {
            new QRCode(document.getElementById("qrcode"), {
                text: targetUrl,
                width: 100,
                height: 100,
                colorDark: "#000000",
                colorLight: "#ffffff",
                correctLevel: QRCode.CorrectLevel.H
            });
            const qqQrBox = document.getElementById("qq-qrcode");
            if (qqQrBox) {
                new QRCode(qqQrBox, {
                    text: "https://qm.qq.com/q/kqkAVjWuQg",
                    width: 130,   // 稍微大一点以保证扫码成功率
                    height: 130,
                    colorDark: "#000000",
                    colorLight: "#ffffff",
                    correctLevel: QRCode.CorrectLevel.M
                });
            }
        } else {
            const qrContainer = document.getElementById("qrcode-container");
            if (qrContainer) qrContainer.style.display = 'none';
        }
        document.getElementById('chatInput').addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                sendMsg();
            }
        });
        function escapeHtml(s) {
            if (s == null) return '';
            return String(s)
                .replaceAll('&', '&amp;')
                .replaceAll('<', '&lt;')
                .replaceAll('>', '&gt;')
                .replaceAll('"', '&quot;')
                .replaceAll("'", "&#39;");
        }
        function renderModelConfigUI(models) {
            const container = document.getElementById('modelsConfigContainer');
            const select = document.getElementById('modelSelect');
            container.innerHTML = '';
            select.innerHTML = '';
            if (!models || models.length === 0) models = [{ Model: '', ApiKey: '', Endpoint: '' }];
            models.forEach((m, i) => {
                const opt = document.createElement('option');
                opt.value = i;
                opt.text = m.Model || `未命名节点 ${i + 1}`;
                select.appendChild(opt);
                container.insertAdjacentHTML('beforeend', `
                    <div class="config-model-item">
                        ${models.length > 1 ? `<button type="button" class="btn-remove-model" onclick="this.parentElement.remove()" title="删除此节点">✖</button>` : ''}
                        <div class="config-row" style="margin-bottom:12px;">
                        <div class="form-group">
                            <label>模型名 (Model)</label>
                            <input type="text" class="cfg-model" value="${escapeHtml(m.Model)}" placeholder="e.g. qwen3.5-plus" />
                        </div>
                        <div class="form-group">
                            <label>端点地址 (Endpoint)</label>
                            <input type="text" class="cfg-endpoint" value="${escapeHtml(m.Endpoint)}" placeholder="https://..." />
                        </div>
                        </div>
                        <div class="form-group">
                        <label>密钥 (ApiKey)</label>
                        <input type="password" class="cfg-apikey" value="${escapeHtml(m.ApiKey)}" placeholder="sk-..." />
                        </div>
                    </div>
                `);
            });
        }
        function addModelConfigUI() {
            const currentModels = getModelsFromUI();
            currentModels.push({ Model: '', ApiKey: '', Endpoint: '' });
            renderModelConfigUI(currentModels);
        }
        function getModelsFromUI() {
                return Array.from(document.querySelectorAll('#modelsConfigContainer .config-model-item')).map(el => ({
                Model: el.querySelector('.cfg-model').value.trim(),
                Endpoint: el.querySelector('.cfg-endpoint').value.trim(),
                ApiKey: el.querySelector('.cfg-apikey').value.trim()
            }));
        }
        function renderUrlConfigUI(urls) {
            const container = document.getElementById('urlsConfigContainer');
            container.innerHTML = '';
            if (!urls || urls.length === 0) urls = ["https://wry-manatee-359.convex.site/api/v1/download?slug={slug}"];
            urls.forEach((u, i) => {
                container.insertAdjacentHTML('beforeend', `
                    <div class="config-model-item" style="padding: 10px 15px; margin-bottom: 10px;">
                        ${urls.length > 1 ? `<button type="button" class="btn-remove-model" onclick="this.parentElement.remove()" title="删除此节点">✖</button>` : ''}
                        <div class="form-group" style="margin: 0;">
                            <label>下载地址 ${i + 1}</label>
                            <input type="text" class="cfg-url" value="${escapeHtml(u)}" placeholder="https://.../{slug}.zip" />
                        </div>
                    </div>
                `);
            });
        }
        function addUrlConfigUI() {
            const currentUrls = getUrlsFromUI();
            currentUrls.push("");
            renderUrlConfigUI(currentUrls);
        }
        function getUrlsFromUI() {
            return Array.from(document.querySelectorAll('.cfg-url')).map(input => input.value.trim());
        }
        // --- 新增：友军节点逻辑 ---
        function renderPeerNodesConfigUI(peerNodes) {
            const container = document.getElementById('peerNodesConfigContainer');
            container.innerHTML = '';
            if (peerNodes && Object.keys(peerNodes).length > 0) {
                for (const [key, info] of Object.entries(peerNodes)) {
                    const url = info.Url || info.url || '';
                    const role = info.Role || info.role || '';
                    const desc = info.Description || info.description || '';
                    addPeerNodeRow(key, url, role, desc);
                }
            }
        }

        function addPeerNodeRow(key = '', url = '', role = '', desc = '') {
            const container = document.getElementById('peerNodesConfigContainer');
            container.insertAdjacentHTML('beforeend', `
                <div class="config-model-item" style="padding: 10px 15px; margin-bottom: 10px;">
                    <button type="button" class="btn-remove-model" onclick="this.parentElement.remove()" title="删除此节点">✖</button>
                    <div class="config-row" style="margin: 0;">
                        <div class="form-group">
                            <label>队友名称 (如: 张三)</label>
                            <input type="text" class="cfg-peer-name" value="${escapeHtml(key)}" placeholder="输入辨识名称" />
                        </div>
                        <div class="form-group">
                            <label>队友地址 (URL)</label>
                            <input type="text" class="cfg-peer-url" value="${escapeHtml(url)}" placeholder="http://192.x.x.x:5050" />
                        </div>
                        <div class="form-group" style="margin-top: 10px;">
                            <label>岗位头衔 (Role)</label>
                            <input type="text" class="cfg-peer-role" value="${escapeHtml(role)}" placeholder="如：总编辑、分析师..." />
                        </div>
                        <div class="form-group" style="margin-top: 10px;">
                            <label>能力说明 (Description)</label>
                            <input type="text" class="cfg-peer-desc" value="${escapeHtml(desc)}" placeholder="负责控制灯光或信息检索..." />
                        </div>
                    </div>
                </div>
            `);
        }


        function getPeerNodesFromUI() {
            const peerNodes = {};
            document.querySelectorAll('#peerNodesConfigContainer .config-model-item').forEach(el => {
                const name = el.querySelector('.cfg-peer-name').value.trim();
                const url = el.querySelector('.cfg-peer-url').value.trim();
                const role = el.querySelector('.cfg-peer-role').value.trim();
                const desc = el.querySelector('.cfg-peer-desc').value.trim();
                if (name && url) {
                    peerNodes[name] = { Url: url, Role: role, Description: desc };
                }
            });
            return peerNodes;
        }

        async function loadConfig() {
            try {
                const res = await fetch('/api/config');
                if (!res.ok) return;
                const data = await res.json();
                document.getElementById('sudoPassword').value = data.SudoPassword || '';
                document.getElementById('webPort').value = data.WebPort || 5050;
                document.getElementById('systemPrompt').value = data.SystemPrompt || '';
                renderModelConfigUI(data.Models);
                renderUrlConfigUI(data.SkillHubDownloadUrls);
                renderPeerNodesConfigUI(data.PeerNodes);
            } catch { }
        }
        async function saveConfig() {
            const portVal = parseInt(document.getElementById('webPort').value) || 5050;
            if (portVal < 1 || portVal > 65535) {
                alert('端口号必须在 1 到 65535 之间'); return;
            }
            const modelsData = getModelsFromUI();
            if (modelsData.length === 0) {
                alert('至少需要保留一个模型配置'); return;
            }
            let urlsData = getUrlsFromUI().filter(u => u !== "");
            if (urlsData.length === 0) {
                // 如果全部删空了，给个保底地址防止崩溃
                urlsData = ["https://wry-manatee-359.convex.site/api/v1/download?slug={slug}"];
            }
            const peerNodesData = getPeerNodesFromUI();
            const cfg = {
                Models: modelsData,
                SudoPassword: document.getElementById('sudoPassword').value,
                WebPort: portVal,
                SkillHubDownloadUrls: urlsData,
                SystemPrompt: document.getElementById('systemPrompt').value,
                PeerNodes: peerNodesData
            };
            const btn = document.querySelector('#configBody .btn-submit');
            const originalText = btn ? btn.innerHTML : '';
            try {
                if (btn) btn.innerHTML = '正在上传...';
                const res = await fetch('/api/config', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(cfg)
                });
                if (!res.ok) throw new Error('Config upload failed');
                renderModelConfigUI(modelsData);
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

        // --- 新增：任务弹窗控制逻辑 ---
        const tasksModal = document.getElementById('tasksModal');
        function openTasksModal() {
            if (!tasksModal) return;
            tasksModal.classList.add('show');
            tasksModal.setAttribute('aria-hidden', 'false');
        }
        function closeTasksModal() {
            if (!tasksModal) return;
            tasksModal.classList.remove('show');
            tasksModal.setAttribute('aria-hidden', 'true');
        }
        function toggleTasksModal() {
            if (tasksModal && tasksModal.classList.contains('show')) closeTasksModal();
            else openTasksModal();
        }
        if (tasksModal) {
            tasksModal.addEventListener('click', (e) => {
                if (e.target === tasksModal) closeTasksModal();
            });
        }
        // 更新了 Escape 键的监听，兼顾两个弹窗
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') { closeConfig(); closeTasksModal(); }
        });

        // --- 替换原有的 fetchTasks 函数：分发逻辑与按钮红点提示 ---
        async function fetchTasks() {
            try {
                const res = await fetch('/api/tasks');
                if (!res.ok) return;
                const tasks = await res.json();

                const pending = (tasks || []).filter(t => t.status === 'pending');

                // 动态更新按钮数字
                const btnTasks = document.querySelector('.btn-tasks');
                if (btnTasks) {
                    btnTasks.innerHTML = pending.length > 0 ? `⏰ (${pending.length})` : `⏰ 任务`;
                    btnTasks.style.color = pending.length > 0 ? '#fff' : 'var(--pipi-magenta)';
                    btnTasks.style.background = pending.length > 0 ? 'var(--pipi-magenta)' : 'rgba(167, 139, 250, 0.1)';
                }

                const delayedContainer = document.getElementById('delayedTasksContainer');
                const scheduledContainer = document.getElementById('scheduledTasksContainer');
                if (!delayedContainer || !scheduledContainer) return;

                const now = new Date();
                const delayedTasks = [];
                const scheduledTasks = [];

                // 区分延时还是定时任务 (<2小时为延时)
                pending.forEach(t => {
                    const execTime = new Date(t.execute_at);
                    const diffHours = (execTime - now) / (1000 * 60 * 60);
                    if (diffHours > 0 && diffHours < 2) delayedTasks.push(t);
                    else scheduledTasks.push(t);
                });

                // 渲染单个任务的卡片模板
                const renderTask = (t, colorClass) => {
                    const dateObj = new Date(t.execute_at);
                    const timeStr = dateObj.toLocaleString('zh-CN', {month:'2-digit', day:'2-digit', hour:'2-digit', minute:'2-digit', second:'2-digit'});
                    const loopTag = t.interval_minutes > 0 ? `<span style="background:var(--pipi-cyan); color:#000; padding:2px 4px; border-radius:4px; font-size:0.8em; margin-left:6px;">每${t.interval_minutes}分</span>` : '';

                    return `
                    <div class="task-item" style="border-left:3px solid var(--pipi-${colorClass}); padding:12px; margin-bottom:10px; background:var(--cmd-bg); border-radius:6px;">
                        <div style="display:flex; justify-content:space-between; align-items:center;">
                            <div style="font-size:0.85em; color:var(--text-muted); font-family:var(--font-mono);">${timeStr} ${loopTag}</div>
                            <div style="font-size:0.7em; color:var(--text-muted); opacity:0.6;">ID: ${t.id.substring(0,6)}</div>
                        </div>
                        <div style="font-weight:bold; margin-top:6px; font-size:0.95em;">${escapeHtml(t.user_intent)}</div>
                    </div>`;
                };

                delayedContainer.innerHTML = delayedTasks.length > 0 
                    ? delayedTasks.map(t => renderTask(t, 'cyan')).join('') 
                    : '<div style="color:var(--text-muted); font-size:0.85em; padding:10px 0; opacity:0.6;">暂无延时任务在挂起队列中</div>';

                scheduledContainer.innerHTML = scheduledTasks.length > 0 
                    ? scheduledTasks.map(t => renderTask(t, 'magenta')).join('') 
                    : '<div style="color:var(--text-muted); font-size:0.85em; padding:10px 0; opacity:0.6;">暂无定时任务在挂起队列中</div>';

            } catch { }
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
        async function clearContext() {
            if (!confirm('确定要彻底清空当前的上下文记忆吗？')) return;
            try {
                const res = await fetch('/api/clear', { method: 'POST' });
                if (res.ok) {
                    const chatBox = document.getElementById('chatBox');
                    // 清空面板并显示提示
                    chatBox.innerHTML = `
                    <div class="msg ai">
                        <div class="msg-header">皮皮虾 // 系统</div>
                        <div class="msg-content">
                            <div style="color:var(--pipi-magenta); font-weight:bold;">
✨ 历史上下文与文件记录已手动清空！随时可以开始新任务。
                            </div>
                        </div>
                    </div>`;
                }
            } catch (e) {
                console.warn('[clearContext] failed:', e);
                alert('清理失败，请检查网络或控制台。');
            }
        }
        async function cancelTask() {
            try { currentAbortController?.abort(); } catch { }
            try { await fetch('/api/cancel', { method: 'POST' }); } catch (e) { console.warn('[cancelTask] /api/cancel failed:', e); }
            setBusy(false);
        }




        // 统一的流数据解析器
async function processStream(response, contentBoxId) {
    const chatBox = document.getElementById('chatBox');
    let contentBox = document.getElementById(contentBoxId);
    let currentTerminalBox = null;
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    try {
        while (true) {
            const { value, done } = await reader.read();
            if (done) break;
            buffer += decoder.decode(value, { stream: true });
            const parts = buffer.split('|||END|||');
            buffer = parts.pop();
            for (const part of parts) {
                const t = part.trim();
                if (!t) continue;
                try {
                    const data = JSON.parse(t);
                    if (data.type === 'clear_chat') {
                        chatBox.innerHTML = `
                        <div class="msg ai">
                            <div class="msg-header">皮皮虾 // 任务完成</div>
                            <div class="msg-content" id="${contentBoxId}">
                                <div style="color:var(--pipi-magenta); margin-bottom:10px; font-weight:bold;">
                                ✨ 历史上下文与网页记录已全自动清空！随时可以开始新任务。
                                </div>
                            </div>
                        </div>`;
                        contentBox = document.getElementById(contentBoxId);
                        currentTerminalBox = null;
                        continue;
                    }
                    if (data.type === 'thinking') {
                        currentTerminalBox = contentBox.querySelector('.exec-terminal');
                        if (!currentTerminalBox) {
                            currentTerminalBox = document.createElement('div');
                            currentTerminalBox.className = 'exec-terminal';
                            contentBox.appendChild(currentTerminalBox);
                        }
                        currentTerminalBox.insertAdjacentHTML('beforeend', `<span class="log-action" style="color:var(--pipi-magenta); opacity:0.8; font-style:italic;">[思考] ${escapeHtml(data.content)}</span>`);
                        currentTerminalBox.scrollTop = currentTerminalBox.scrollHeight;
                        continue;
                    }
                    if (data.type === 'tool' || data.type === 'tool_result') {
                        currentTerminalBox = contentBox.querySelector('.exec-terminal');
                        if (!currentTerminalBox) {
                            currentTerminalBox = document.createElement('div');
                            currentTerminalBox.className = 'exec-terminal';
                            contentBox.appendChild(currentTerminalBox);
                        }
                        if (data.type === 'tool' || data.type === 'tool_result') {
                            currentTerminalBox = contentBox.querySelector('.exec-terminal');
                            if (!currentTerminalBox) {
                                currentTerminalBox = document.createElement('div');
                                currentTerminalBox.className = 'exec-terminal';
                                contentBox.appendChild(currentTerminalBox);
                            }

                            // 1. 使用 insertAdjacentHTML 代替 innerHTML +=，极大地提升渲染性能
                            if (data.type === 'tool') {
                                currentTerminalBox.insertAdjacentHTML('beforeend', `<span class="log-action">&gt;&gt; ${escapeHtml(data.content)}</span>`);
                            } else {
                                currentTerminalBox.insertAdjacentHTML('beforeend', `<span class="log-result">${escapeHtml(data.content)}</span>`);
                            }

                            const MAX_LINES = 200; // 你可以自己调整阈值，保留最近的 200 行
                            if (currentTerminalBox.childElementCount > MAX_LINES) {
                                // 移除最顶部的老旧日志节点
                                while (currentTerminalBox.childElementCount > MAX_LINES) {
                                    currentTerminalBox.removeChild(currentTerminalBox.firstElementChild);
                                }
                                // 在顶部插入一个提示，告诉用户部分日志已被截断
                                if (currentTerminalBox.firstElementChild && currentTerminalBox.firstElementChild.getAttribute('data-truncated') !== 'true') {
                                    currentTerminalBox.insertAdjacentHTML('afterbegin', `<span class="log-result" data-truncated="true" style="color: var(--pipi-magenta); opacity: 0.8; font-style: italic;">... [日志输出过长，早期输出已被自动截断] ...<br/></span>`);
                                }
                            }

                            currentTerminalBox.scrollTop = currentTerminalBox.scrollHeight;
                        }
                    }
                    if (data.type === 'final') {
                        const finalWrap = document.createElement('div');
                        finalWrap.style.marginTop = '12px';
                        contentBox.appendChild(finalWrap);
                        finalWrap.innerHTML = marked.parse(String(data.content ?? ''));
                    }
                    chatBox.scrollTop = chatBox.scrollHeight;
                } catch(e) {}
            }
        }
    } catch(e) {
        if (e && e.name !== 'AbortError') {
            const errWrap = document.createElement('div');
            errWrap.style.cssText = 'margin-top:8px; color:var(--pipi-magenta); font-size:0.85em;';
            errWrap.textContent = '[连接中断]';
            contentBox.appendChild(errWrap);
        }
    } finally {
        setBusy(false);
    }
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
    currentAbortController = new AbortController();
    const selectedModelIdx = parseInt(document.getElementById('modelSelect').value) || 0;
    try {
        const response = await fetch('/api/chat', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ message: text, modelIndex: selectedModelIdx }),
            signal: currentAbortController.signal
        });
        await processStream(response, uniqueId);
    } catch(e) {
        setBusy(false);
    }
}

// 核心重连吸附功能
async function checkStatusAndAttach() {
    try {
        const res = await fetch('/api/attach', { method: 'POST' });
        const contentType = res.headers.get('content-type');
        if (contentType && contentType.includes('application/json')) {
            return; // 处于空闲状态，不需要重连
        }

        // 处于繁忙状态，准备吸附数据流
        setBusy(true);

        // 核心修改：直接抓取页面上最后一次 AI 的对话框内容区
        const aiContents = document.querySelectorAll('.msg.ai .msg-content');
        let targetId;

        if (aiContents.length > 0) {
            // 直接拿最后一个框的 ID
            targetId = aiContents[aiContents.length - 1].id;
        } else {
            // 极端情况保底：如果被清空了真的没历史记录，才新建一个
            targetId = 'ai_live_' + Date.now();
            document.getElementById('chatBox').insertAdjacentHTML('beforeend', 
                `<div class="msg ai"><div class="msg-header">皮皮虾 // 工具</div><div class="msg-content" id="${targetId}"></div></div>`
            );
        }

        currentAbortController = new AbortController();

        // 把流直接怼进目标 ID 的框里
        await processStream(res, targetId);
    } catch (e) {
        console.warn("Attach failed", e);
    }
}

window.addEventListener('DOMContentLoaded', () => {
    if(currentUsername) {
        document.getElementById('userLoginOverlay').style.display = 'none';
        loadConfig();
        // 加载完固化的历史记录后，紧接着探针检查是否需要吸附后台的进行中任务
        loadHistory().then(checkStatusAndAttach);
        loadAgents();
    } else {
        document.getElementById('usernameInput').focus();
    }
});

function confirmUsername() {
    let val = document.getElementById('usernameInput').value.trim();
    if(!val) return alert("用户名不能为空！");
    currentUsername = val;
    localStorage.setItem('username', val);
    document.getElementById('userLoginOverlay').style.display = 'none';
    loadConfig();
    loadHistory().then(checkStatusAndAttach);
    loadAgents();
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
                            const thinkText = msg.reasoning_content || msg.content || msg.ReasoningContent || msg.Content || "";
                            if (thinkText) {
                                const thinkSpan = document.createElement('span');
                                thinkSpan.className = 'log-action';
                                thinkSpan.style.cssText = 'color:var(--pipi-magenta); opacity:0.8; font-style:italic;';
                                thinkSpan.textContent = `[思考] ${thinkText}`;
                                currentTerminalBox.appendChild(thinkSpan);
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
                            finalWrap.innerHTML = marked.parse(msg.content);
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
            } catch (e) {
                console.warn('[loadHistory] failed:', e);
            }
        }


// 获取并渲染所有已知的 Agent 列表
async function loadAgents() {
    try {
        const res = await fetch('/api/agents');
        if (res.ok) {
            const agents = await res.json();
            const switcher = document.getElementById('agentSwitcher');
            switcher.innerHTML = '';

            // 确保当前的 username 即使是刚刚新建的也包含在内
            if (currentUsername && !agents.includes(currentUsername)) {
                agents.unshift(currentUsername);
            }

            agents.forEach(agent => {
                if (!agent) return;
                const opt = document.createElement('option');
                opt.value = agent;
                opt.text = `[ ${agent} ]`;
                if (agent === currentUsername) opt.selected = true;
                switcher.appendChild(opt);
            });

            // 添加一个唤醒新 Agent 的入口
            const optNew = document.createElement('option');
            optNew.value = "_NEW_AGENT_";
            optNew.text = "➕ 唤醒新 Agent...";
            switcher.appendChild(optNew);
        }
    } catch(e) {}
}

// 核心切换逻辑
function switchAgent(newVal) {
    if (!newVal) return;

    // 如果选择的是添加新 Agent，则弹回登录覆盖层
    if (newVal === "_NEW_AGENT_") {
        document.getElementById('agentSwitcher').value = currentUsername; // 恢复下拉框显示
        document.getElementById('usernameInput').value = '';
        document.getElementById('userLoginOverlay').style.display = 'flex';
        document.getElementById('usernameInput').focus();
        return;
    }

    if (newVal === currentUsername) return;

    // 1. 切换本地状态
    currentUsername = newVal;
    localStorage.setItem('username', newVal);

    // 2. 清空聊天面板残影
    document.getElementById('chatBox').innerHTML = ''; 

    // 3. 重新建立新 Agent 的连接、配置和历史记录
    loadConfig();
    loadHistory().then(checkStatusAndAttach);
    loadAgents(); // 刷新列表高亮状态
}

        loadConfig();
    </script>
</body>
</html>
""";
    return html;
}
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
        for (var i = 0; i < s.Length; i++)
        {
            v1[0] = i + 1;
            for (var j = 0; j < t.Length; j++) v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + (s[i] == t[j] ? 0 : 1));
            for (var j = 0; j < v0.Length; j++) v0[j] = v1[j];
        }
        return 1.0 - ((double)v1[t.Length] / max);
    }
}
public class ModelConfig
{
    [JsonPropertyName("ApiKey")] public string ApiKey { get; set; } = "";
    [JsonPropertyName("Model")] public string Model { get; set; } = "";
    [JsonPropertyName("Endpoint")] public string Endpoint { get; set; } = "";
}
public class AppConfig
{
    [JsonPropertyName("Models")] public List<ModelConfig> Models { get; set; } = [new ModelConfig()];
    [JsonPropertyName("SudoPassword")] public string SudoPassword { get; set; } = "";
    [JsonPropertyName("WebPort")] public int WebPort { get; set; } = 5050;
    [JsonPropertyName("SkillHubSearchUrl")] public string SkillHubSearchUrl { get; set; } = "http://lb-3zbg86f6-0gwe3n7q8t4sv2za.clb.gz-tencentclb.com/api/v1/search";
    [JsonPropertyName("SystemPrompt")] public string SystemPrompt { get; set; } = "";
    [JsonPropertyName("PeerNodes")] public Dictionary<string, PeerNodeInfo> PeerNodes { get; set; } = new();

    [JsonPropertyName("SkillHubDownloadUrls")]

    public List<string> SkillHubDownloadUrls { get; set; } = ["https://skillhub-1388575217.cos.ap-guangzhou.myqcloud.com/skills/{slug}.zip", "https://wry-manatee-359.convex.site/api/v1/download?slug={slug}"];
}
public class PeerNodeInfo
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("Url")] public string Url { get; set; } = "";
    [JsonPropertyName("Role")] public string Role { get; set; } = "";
    [JsonPropertyName("Description")] public string Description { get; set; } = "";
}
public class TaskItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("execute_at")] public string ExecuteAt { get; set; } = "";
    [JsonPropertyName("user_intent")] public string UserIntent { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("interval_minutes")] public int IntervalMinutes { get; set; } = 0;
    [JsonPropertyName("username")] public string Username { get; set; } = "local";
}
public class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Content { get; set; }
    [JsonPropertyName("reasoning_content")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ReasoningContent { get; set; }
    [JsonPropertyName("name")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
    [JsonPropertyName("tool_call_id")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ToolCallId { get; set; }
    [JsonPropertyName("tool_calls")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ToolCall>? ToolCalls { get; set; }
    public ChatMessage DeepClone()
    {
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(this, AppJsonContext.Default.ChatMessage), AppJsonContext.Default.ChatMessage) ?? new ChatMessage();
    }
}
public class ToolCall
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public ToolFunction Function { get; set; } = new();
}
public class ToolFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "";
}
public class LlmRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();
    [JsonPropertyName("tools")] public JsonElement Tools { get; set; }
    [JsonPropertyName("enable_search")] public bool EnableSearch { get; set; }
}
public class LlmResponse
{
    [JsonPropertyName("choices")] public List<LlmChoice>? Choices { get; set; }
}
public class LlmChoice
{
    [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
}
public class ChatReq
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("modelIndex")] public int ModelIndex { get; set; } = 0;
    [JsonPropertyName("caller")] public string Caller { get; set; } = "";
}
public class PushMsg
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(List<TaskItem>))]
[JsonSerializable(typeof(TaskItem))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(LlmRequest))]
[JsonSerializable(typeof(LlmResponse))]
[JsonSerializable(typeof(ChatReq))]
[JsonSerializable(typeof(PushMsg))]
[JsonSerializable(typeof(ModelConfig))]
[JsonSerializable(typeof(Dictionary<string, PeerNodeInfo>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
internal partial class AppJsonContext : JsonSerializerContext { }
