using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

// ReSharper disable UnusedMember.Local

namespace YaeAchievement.Parsers;

/// <summary>
/// 全量同步任务导出器 (v7.1.0 対応フィールド):
///   cmd 7638  QuestListNotify            -> 任务簿 (子任务状态; start_time=4, accept_time=9)
///   cmd 3062 FinishedParentQuestNotify  -> 父任务完成历史 (成员关系=完成; 无完成时间戳, accept_time=15)
///
/// 仅处理任务包 (纯增量, 不碰 Yae 原生成就/背包流)。Utils.cs 把任务包喂给 AddPacket, 结束后 Export 导出:
///   uigf_quest_record_v1_<时间>.json   UIGF Quest Record v1.1 (默认, 纯数据)
///   full_sync_<时间>.json              任务账号状态 (仅 --full-sync 时)
/// 不产生任何 .bin 中间文件。
/// </summary>
public static class FullSyncExporter {

    public const uint QuestListCmd = 7638;   // QuestListNotify (Genshin 7.1)
    public const uint ParentCmd = 3062;     // FinishedParentQuestNotify (Genshin 7.1)

    /// <summary>是否额外导出 full_sync_*.json (--full-sync)。</summary>
    public static bool EmitFullSync { get; set; }

    /// <summary>任务捕获模式 (--quest/--quest-uigf): Utils.cs 按此注册白名单并分发全量同步包。</summary>
    public static bool QuestMode { get; set; }

    private static readonly string[] QuestStateNames = [
        "NONE", "UNSTARTED", "UNFINISHED", "FINISHED", "REWARD_TAKEN", "FAILED"
    ];

    private static string QuestStateName(ulong s) => s < (ulong) QuestStateNames.Length ? QuestStateNames[s] : $"UNKNOWN_{s}";

    private static readonly Dictionary<ulong, SyncQuest> Quests = [];
    private static readonly Dictionary<ulong, SyncParent> Parents = [];

    /// <summary>离线模式: 直接对已有 packet_dump.bin 执行导出 (读入累加器, 不启动游戏)。</summary>
    public static void ExportDump(string path) {
        var dumpPath = Path.GetFullPath(path);
        if (!File.Exists(dumpPath)) {
            AnsiConsole.WriteLine($"文件不存在: {dumpPath}");
            return;
        }
        Clear();
        using var fs = File.OpenRead(dumpPath);
        var h = new byte[8];
        var b = new byte[1 << 20];
        while (fs.Position < fs.Length) {
            try { fs.ReadExactly(h, 0, 8); } catch (EndOfStreamException) { break; }
            var c = BitConverter.ToUInt32(h, 0);
            var l = BitConverter.ToInt32(h, 4);
            if (l < 0 || l > (1 << 24)) break; // len=0 是合法的空负载包
            if (l > b.Length) b = new byte[l];
            try { fs.ReadExactly(b, 0, l); } catch (EndOfStreamException) { break; }
            AddPacket(c, b.AsSpan(0, l));
        }
        AnsiConsole.WriteLine($"离线导出模式: {dumpPath}");
        Export(dumpPath);
    }

    public static void Clear() {
        Quests.Clear();
        Parents.Clear();
    }

    /// <summary>把单个网包加入累加器 (进程内解析, 不落盘 .bin)。</summary>
    public static void AddPacket(uint cmdId, ReadOnlySpan<byte> payload) {
        if (cmdId == QuestListCmd) {
            foreach (var ld in ProtoWalker.Walk(payload).GetLD(15)) {
                var q = ParseQuest(ld.Data);
                if (q != null) Quests[q.QuestId] = q;
            }
        } else if (cmdId == ParentCmd) {
            foreach (var ld in ProtoWalker.Walk(payload).GetLD(4)) {
                var p = ParseParent(ld.Data);
                if (p != null) Parents[p.ParentQuestId] = p;
            }
        }
    }

    /// <summary>导出 UIGF Quest Record (默认) + full_sync (--full-sync)。sourceLabel: 实时捕获为空, 离线为 dump 路径。</summary>
    public static void Export(string sourceLabel) {
        AnsiConsole.WriteLine($"全量同步解析: 子任务 {Quests.Count} | 完成历史 {Parents.Count}");
        var now = DateTime.Now;
        var source = string.IsNullOrEmpty(sourceLabel) ? [] : new List<string> { sourceLabel };

        // UIGF Quest Record v1.1 (默认输出, 纯数据)
        ExportQuestRecord(now, source);

        // full_sync (可选)
        if (EmitFullSync) {
            var fsJson = new FullSyncJson {
                Source = source,
                SubQuests = Quests.Count,
                GeneratedAt = DateTime.UtcNow.ToString("o"),
                Quests = [.. Quests.Values.OrderBy(q => q.QuestId)],
                ParentQuests = [.. Parents.Values.OrderBy(p => p.ParentQuestId)],
            };
            var fsPath = Path.GetFullPath($"full_sync_{now:yyyyMMddHHmmss}.json");
            File.WriteAllText(fsPath, JsonSerializer.Serialize(fsJson, FullSyncJsonContext.Default.FullSyncJson));
            AnsiConsole.WriteLine($"已导出任务账号状态 -> {fsPath}");
        }
    }

    private static SyncQuest? ParseQuest(ReadOnlySpan<byte> data) {
        var f = ProtoWalker.Walk(data);
        var qid = f.GetFirstVarint(1);
        var st = f.GetFirstVarint(2);
        if (qid == null || st == null || st.Value > 15) return null;
        return new SyncQuest {
            QuestId = qid.Value,
            State = st.Value,
            StateName = QuestStateName(st.Value),
            StartTime = f.GetFirstVarint(4) ?? 0,   // start_time=字段4 (官方/LunaGC 约定)
            AcceptTime = f.GetFirstVarint(9) ?? 0,  // accept_time=字段9 (入册即记, 未接取任务也有)
            ParentQuestId = f.GetFirstVarint(6) ?? 0,
            FinishProgress = f.GetPackedList(10).FirstOrDefault(),
        };
    }

    private static SyncParent? ParseParent(ReadOnlySpan<byte> data) {
        var f = ProtoWalker.Walk(data);
        var pid = f.GetFirstVarint(14);
        if (pid == null) return null;
        // ParentQuest 无 finish_time 字段; Genshin 7.1: parent_quest_id=14, accept_time=15
        return new SyncParent { ParentQuestId = pid.Value, AcceptTime = f.GetFirstVarint(15) ?? 0 };
    }

    /// <summary>导出 UIGF Quest Record v1.1 (纯数据: 完成历史 + 任务簿, 展示由消费端解析)。</summary>
    private static void ExportQuestRecord(DateTime now, List<string> source) {
        var outJson = new QuestRecordJson {
            Info = new QuestRecordInfo {
                ExportApp = "YaeAchievement(quest)",
                ExportAppVersion = "1.0.0",
                UigfQuestVersion = "v1.1",
                ExportTimestamp = (long) DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ExportTime = now.ToString("yyyy-MM-dd HH:mm:ss"),
                Timezone = "UTC+8",
                Source = new QuestRecordSource {
                    Uid = "",
                    PacketDumps = source,
                    GameVersion = TryGetGameVersion(),
                },
            },
            List = new QuestRecordList {
                FinishedParentQuests = [.. Parents.Values.OrderBy(p => p.ParentQuestId)],
                QuestBook = [.. Quests.Values.OrderBy(q => q.QuestId).Select(q => new QuestBookItem {
                    QuestId = q.QuestId,
                    ParentQuestId = q.ParentQuestId,
                    State = q.State,
                    StartTime = q.StartTime,
                    AcceptTime = q.AcceptTime,
                    FinishProgress = q.FinishProgress,
                })],
            },
        };
        var outPath = Path.GetFullPath($"uigf_quest_record_v1_{now:yyyyMMddHHmmss}.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(outJson, FullSyncJsonContext.Default.QuestRecordJson));
        AnsiConsole.WriteLine($"已导出 UIGF Quest Record (完成历史 {outJson.List.FinishedParentQuests.Count} / 任务簿 {outJson.List.QuestBook.Count}) -> {outPath}");
    }

    private static string TryGetGameVersion() {
        try {
            var v = GlobalVars.AchievementInfo.Version;
            return string.IsNullOrEmpty(v) ? "" : v;
        } catch {
            return "";
        }
    }
}

// ================= 数据模型 =================

// ---------- UIGF Quest Record (format.md v1.0 / quest-record.schema.json) ----------

public sealed class QuestRecordSource {
    public string Uid { get; set; } = "";
    public List<string> PacketDumps { get; set; } = [];
    public string GameVersion { get; set; } = "";
}

public sealed class QuestRecordInfo {
    public string ExportApp { get; set; } = "YaeAchievement(quest)";
    public string ExportAppVersion { get; set; } = "1.0.0";
    public string UigfQuestVersion { get; set; } = "v1.1";
    public long ExportTimestamp { get; set; }
    public string ExportTime { get; set; } = "";
    public string Timezone { get; set; } = "UTC+8";
    public QuestRecordSource Source { get; set; } = new();
}

public sealed class QuestBookItem {
    public ulong QuestId { get; set; }
    public ulong ParentQuestId { get; set; }
    public ulong State { get; set; }
    public ulong StartTime { get; set; }
    public ulong AcceptTime { get; set; }
    public ulong FinishProgress { get; set; }
}

public sealed class QuestRecordList {
    public List<SyncParent> FinishedParentQuests { get; set; } = [];
    public List<QuestBookItem> QuestBook { get; set; } = [];
}

public sealed class QuestRecordJson {
    public QuestRecordInfo Info { get; set; } = new();
    public QuestRecordList List { get; set; } = new();
}

internal static class ProtoWalker {

    public readonly record struct LD(byte[] Data);

    public static Dictionary<int, List<object>> Walk(ReadOnlySpan<byte> b) {
        var fields = new Dictionary<int, List<object>>();
        var off = 0;
        while (off < b.Length) {
            if (!TryReadVarint(b, ref off, out var tag)) break;
            var field = (int) (tag >> 3);
            var wire = (int) (tag & 7);
            if (field == 0) break;
            switch (wire) {
                case 0:
                    if (TryReadVarint(b, ref off, out var v)) {
                        Add(fields, field, v);
                    }
                    break;
                case 1:
                    if (off + 8 > b.Length) return fields;
                    Add(fields, field, BitConverter.ToUInt64(b.Slice(off, 8)));
                    off += 8;
                    break;
                case 2:
                    if (!TryReadVarint(b, ref off, out var len) || len > 16 * 1024 * 1024 || off + (int) len > b.Length) {
                        return fields;
                    }
                    Add(fields, field, new LD(b.Slice(off, (int) len).ToArray()));
                    off += (int) len;
                    break;
                case 5:
                    if (off + 4 > b.Length) return fields;
                    Add(fields, field, BitConverter.ToUInt32(b.Slice(off, 4)));
                    off += 4;
                    break;
                default:
                    return fields;
            }
        }
        return fields;
    }

    private static void Add(Dictionary<int, List<object>> fields, int field, object value) {
        if (!fields.TryGetValue(field, out var list)) {
            list = [];
            fields[field] = list;
        }
        list.Add(value);
    }

    public static ulong? GetFirstVarint(this Dictionary<int, List<object>> fields, int field) {
        if (!fields.TryGetValue(field, out var list)) return null;
        foreach (var v in list) {
            if (v is ulong u) return u;
        }
        return null;
    }

    /// <summary>取 repeated uint32 字段, 兼容 packed(字节块内 varint) 与 unpacked(逐个 varint)。</summary>
    public static List<ulong> GetPackedList(this Dictionary<int, List<object>> fields, int field) {
        var outList = new List<ulong>();
        if (!fields.TryGetValue(field, out var list)) return outList;
        foreach (var v in list) {
            switch (v) {
                case ulong u:
                    outList.Add(u);
                    break;
                case LD ld:
                    var off = 0;
                    while (off < ld.Data.Length && TryReadVarint(ld.Data.AsSpan(), ref off, out var n)) {
                        outList.Add(n);
                    }
                    break;
            }
        }
        return outList;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> b, ref int off, out ulong value) {
        value = 0;
        var shift = 0;
        while (off < b.Length && shift < 64) {
            var x = b[off++];
            value |= (ulong) (x & 0x7F) << shift;
            if (x < 0x80) return true;
            shift += 7;
        }
        return false;
    }
}

internal static class ProtoWalkerExt {
    public static List<ProtoWalker.LD> GetLD(this Dictionary<int, List<object>> fields, int field) {
        if (!fields.TryGetValue(field, out var list)) return [];
        return list.OfType<ProtoWalker.LD>().ToList();
    }
}

// ================= 数据模型 =================

public sealed class SyncQuest {
    public ulong QuestId { get; set; }
    public ulong State { get; set; }
    public string StateName { get; set; } = "";
    public ulong StartTime { get; set; }
    public ulong AcceptTime { get; set; }
    public ulong ParentQuestId { get; set; }
    public ulong FinishProgress { get; set; }
}

public sealed class SyncParent {
    public ulong ParentQuestId { get; set; }
    /// <summary>父任务接取时间 (Genshin 7.1 ParentQuest.accept_time=字段15)。完成时间协议不提供。</summary>
    public ulong AcceptTime { get; set; }
}

public sealed class FullSyncJson {
    public List<string> Source { get; set; } = [];
    public int SubQuests { get; set; }
    public string GeneratedAt { get; set; } = "";
    public List<SyncQuest> Quests { get; set; } = [];
    public List<SyncParent> ParentQuests { get; set; } = [];
}

[JsonSerializable(typeof(FullSyncJson))]
[JsonSerializable(typeof(QuestRecordJson))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower
)]
public sealed partial class FullSyncJsonContext : JsonSerializerContext;
