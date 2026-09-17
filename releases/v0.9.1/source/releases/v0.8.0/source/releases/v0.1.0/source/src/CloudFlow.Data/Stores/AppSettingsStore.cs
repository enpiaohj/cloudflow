using System.Text.Json;
using CloudFlow.Core.Operations;

namespace CloudFlow.Data.Stores;

/// <summary>
/// 应用设置的读写。刻意不复用 <see cref="JsonFileStore{T}"/>：那个基类既没有锁也没有
/// 原子替换，而设置是"设置页改、别的页面读"的共享状态，半截写入会让读方拿到坏 JSON。
/// 写法对齐 <see cref="PersonalAccountStore"/>（自持 lock + 临时文件 + Move 覆盖）。
///
/// 读取**永不抛异常**：文件不存在、内容是坏 JSON、值是旧版本残留 —— 一律回退默认值。
/// 设置坏了不该让应用起不来，用户得有机会进设置页把它改回来。
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly object _lock = new();

    private AppSettings _current;

    public AppSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? CloudFlowPaths.AppSettingsFile;
        _current = ReadFromDisk();
    }

    /// <summary>当前生效的设置。构造时已读盘并归一化，之后由 <see cref="Save"/> 维护。</summary>
    public AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// 设置发生变化时触发。参数是**归一化之后**的值 —— 订阅方不需要再判一次合法性，
    /// 否则每个订阅方都会各自实现一遍"这个间隔能不能用"。
    /// 在锁外触发：订阅方的处理里很可能又要读 <see cref="Current"/>。
    /// </summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>写入并落盘。返回值是实际生效的设置（可能因为越界而被归一化过）。</summary>
    public AppSettings Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = settings.Normalize();

        lock (_lock)
        {
            // 先落盘再更新内存：写失败（磁盘满 / 无权限）时内存与文件仍然一致，
            // 而不是内存里"已经改了"、重启后又变回去。
            WriteToDisk(normalized);
            _current = normalized;
        }

        Changed?.Invoke(this, normalized);
        return normalized;
    }

    private AppSettings ReadFromDisk()
    {
        if (!File.Exists(_filePath))
        {
            return AppSettings.Default;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var dto = JsonSerializer.Deserialize<SettingsDto>(json, Options);
            // 反序列化结果本身可能带着越界值（手改过的文件），Normalize 兜住
            return (dto?.ToSettings() ?? AppSettings.Default).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return AppSettings.Default;
        }
    }

    private void WriteToDisk(AppSettings settings)
    {
        // 目录先于临时文件创建：首次运行时目录还不存在。
        // 建的是 _filePath 自己的目录，不是全局 Root —— 构造时允许传入任意路径，
        // 建 Root 会在那种情况下建错地方，然后写文件失败。
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(SettingsDto.From(settings), Options));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    /// <summary>
    /// 落盘用的中间表示：字段全部可空，且枚举以字符串形式进出。
    ///
    /// 为什么不直接序列化 <see cref="AppSettings"/>：那样一个字段写错就会让**整份**设置
    /// 回退 —— 内置的枚举转换器遇到 "Neon" 这种拼错的名字会抛 JsonException，
    /// 而反序列化是按整份文档做的，用户在同一文件里改好的每页条数也跟着没了。
    /// 逐字段解析之后，坏掉的那个字段回退默认，同文件的其余字段照常生效。
    /// 顺带保证 settings.json 里写的是 "Dark" 而不是 2。
    /// </summary>
    private sealed class SettingsDto
    {
        public string? Theme { get; set; }

        public string? ApprovalPolicy { get; set; }

        public int? AutoRefreshSeconds { get; set; }

        public int? DefaultPageSize { get; set; }

        public bool? AutoDetectPublicIp { get; set; }

        public static SettingsDto From(AppSettings settings) => new()
        {
            Theme = settings.Theme.ToString(),
            ApprovalPolicy = settings.ApprovalPolicy.ToString(),
            AutoRefreshSeconds = settings.AutoRefreshSeconds,
            DefaultPageSize = settings.DefaultPageSize,
            AutoDetectPublicIp = settings.AutoDetectPublicIp
        };

        public AppSettings ToSettings() => new()
        {
            // 无法识别的名字 → 各自字段的默认值。这里不能拿 default(T) 当哨兵：
            // default(ApprovalPolicy) 是 Off，那是个**合法**档位，
            // 混进来之后 Normalize 就再也分不出"用户选了 Off"和"文件里是乱码"。
            Theme = ParseEnum(Theme, AppThemeKind.System),
            // 全限定：这里的 ApprovalPolicy 是上面的 string 属性，直接写 ApprovalPolicy.HighRiskOnly
            // 会被解析成"在 string 上找 HighRiskOnly"
            ApprovalPolicy = ParseEnum(ApprovalPolicy, Core.Operations.ApprovalPolicy.HighRiskOnly),
            AutoRefreshSeconds = AutoRefreshSeconds ?? 0,
            DefaultPageSize = DefaultPageSize ?? 10,
            AutoDetectPublicIp = AutoDetectPublicIp ?? true
        };

        private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
            where TEnum : struct, Enum =>
            Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : fallback;
    }
}
