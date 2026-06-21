using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using PromeRotation;
using PromeRotation.Extensions;
using PromeRotation.Helpers;
using PromeRotation.LogSystem;
using WhiteShaftGenerator.Models;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace WhiteShaftGenerator.Recorder;

public sealed class WhiteShaftGeneratorService : IDisposable
{
    private const int DefaultDeduplicationMs = 300;
    private const int AbilityEffectDeduplicationMs = 400;
    private const int ExportSkillMergeMs = 2500;
    private const int ExportTargetableMergeMs = 1000;
    private const int ExportTargetIconMergeMs = 1000;
    private const int ExportStatusNearSkillSuppressMs = 5000;
    private const int OutputPathMaxLength = 512;
    private const int CurrentConfigVersion = 4;
    private const uint AutoAttackActionId = 872;
    private const string RecorderVersion = "PR WhiteShaftGenerator 2.4";

    private readonly object _sync = new();
    private readonly List<RecordedEntry> _entries = new();
    private readonly Dictionary<string, DateTime> _recentKeys = new(StringComparer.Ordinal);
    private IDisposable? _eventSubscription;
    private RecorderConfig _config = new();
    private long _nextEntryId = 1;
    private DateTime _sessionStartUtc;
    private DateTime? _sessionEndUtc;
    private bool _sessionActive;
    private string _status = "空闲";
    private string _lastExportPath = string.Empty;
    private string _outputDirectoryInput = string.Empty;
    private string _configPath = string.Empty;

    public void Initialize()
    {
        LoadConfig();
        _eventSubscription = global::PromeRotation.Plugin.Instance.LogSystem.Hub.Subscribe(HandleEvent);
    }

    public void Dispose()
    {
        _eventSubscription?.Dispose();
        _eventSubscription = null;
        SaveConfig();
    }

    public void Draw()
    {
        var optionChanged = false;
        var enabled = _config.RecordingEnabled;
        if (ImGui.Checkbox("启用记录", ref enabled))
        {
            _config.RecordingEnabled = enabled;
            if (!enabled)
                EndSession("记录关闭", export: false);
            optionChanged = true;
        }

        ImGui.SameLine();
        var autoExport = _config.AutoExportOnBattleEnd;
        if (ImGui.Checkbox("战斗结束自动导出", ref autoExport))
        {
            _config.AutoExportOnBattleEnd = autoExport;
            optionChanged = true;
        }

        ImGui.Spacing();
        ImGui.Text("事件类型:");
        optionChanged |= DrawOption("敌方读条", value => _config.RecordCastStart = value, _config.RecordCastStart);
        ImGui.SameLine();
        optionChanged |= DrawOption("敌方技能效果", value => _config.RecordActionEffect = value, _config.RecordActionEffect);
        ImGui.SameLine();
        var recordAutoAttack = _config.RecordAutoAttack;
        if (ImGui.Checkbox("普通攻击", ref recordAutoAttack))
        {
            _config.RecordAutoAttack = recordAutoAttack;
            if (!recordAutoAttack)
                RemoveAutoAttackEntries("已移除普通攻击");
            optionChanged = true;
        }
        ImGui.SameLine();
        optionChanged |= DrawOption("Boss增益/队伍减益", value => _config.RecordStatus = value, _config.RecordStatus);
        optionChanged |= DrawOption("点名/连线", value => _config.RecordTargetMarks = value, _config.RecordTargetMarks);
        ImGui.SameLine();
        optionChanged |= DrawOption("地图/对象", value => _config.RecordEnvironment = value, _config.RecordEnvironment);
        ImGui.SameLine();
        optionChanged |= DrawOption("场地标记", value => _config.RecordWaymarks = value, _config.RecordWaymarks);

        var dedupe = _config.DeduplicationMs;
        if (ImGui.InputInt("去重间隔(ms)", ref dedupe))
        {
            _config.DeduplicationMs = Math.Clamp(dedupe, 0, 600000);
            optionChanged = true;
        }

        if (optionChanged)
            SaveConfig();

        ImGui.Separator();
        ImGui.Text($"状态: {_status}");
        ImGui.Text($"战斗中: {(IsInCombat() ? "是" : "否")}");
        ImGui.Text($"记录中: {(_sessionActive ? "是" : "否")}");
        ImGui.Text($"开始: {FormatSessionTime(_sessionStartUtc)}");
        ImGui.Text($"结束: {FormatSessionTime(_sessionEndUtc)}");
        ImGui.Text($"条目数量: {GetEntryCount()} / 已选 {GetSelectedEntryCount()}");
        if (!string.IsNullOrWhiteSpace(_lastExportPath))
            ImGui.TextWrapped($"最近导出: {_lastExportPath}");

        if (ImGui.Button("开始新记录"))
        {
            if (IsInCombat())
                StartSession("手动开始");
            else
                _status = "等待进入战斗";
        }

        ImGui.SameLine();
        if (ImGui.Button("立即导出"))
            ExportSession();

        ImGui.SameLine();
        if (ImGui.Button("导出选中"))
            ExportSelectedSession();

        ImGui.SameLine();
        if (ImGui.Button("清空"))
            ClearSession("手动清空");

        if (ImGui.Button("全选"))
            SetAllEntriesSelected(true);

        ImGui.SameLine();
        if (ImGui.Button("全不选"))
            SetAllEntriesSelected(false);

        ImGui.SameLine();
        if (ImGui.Button("删除未选"))
            RemoveUnselectedEntries();

        ImGui.Separator();
        ImGui.InputText("导出目录", ref _outputDirectoryInput, OutputPathMaxLength);
        ImGui.SameLine();
        if (ImGui.Button("保存目录"))
        {
            _config.OutputDirectory = _outputDirectoryInput.Trim();
            SaveConfig();
        }

        ImGui.SameLine();
        if (ImGui.Button("打开目录"))
            OpenOutputDirectory();

        DrawRecentEntries();
    }

    private static bool DrawOption(string label, Action<bool> setter, bool current)
    {
        var value = current;
        if (!ImGui.Checkbox(label, ref value))
            return false;

        setter(value);
        return true;
    }

    private void HandleEvent(LogSystemEvent ev)
    {
        if (!_config.RecordingEnabled)
            return;

        if (ev is LogSystemBattleStartedEvent)
        {
            StartSession("战斗开始");
            return;
        }

        if (ev is LogSystemBattleEndedEvent)
        {
            EndSession("战斗结束", export: _config.AutoExportOnBattleEnd);
            return;
        }

        if (!IsInCombat())
        {
            if (_sessionActive)
                EndSession("离开战斗", export: _config.AutoExportOnBattleEnd);

            return;
        }

        if (!_sessionActive)
        {
            StartSession("战斗中事件");
        }

        var entry = TryBuildEntry(ev);
        if (entry == null)
            return;

        var debounceMs = entry.Kind is RecordedEventKind.ActionEffect or RecordedEventKind.AutoAttack
            ? Math.Max(_config.DeduplicationMs, AbilityEffectDeduplicationMs)
            : _config.DeduplicationMs;

        if (IsDuplicate(entry.DeduplicationKey, debounceMs, ev.Timestamp))
            return;

        lock (_sync)
        {
            if (IsEnvironmentDuplicateInSession(entry))
                return;

            entry.Id = _nextEntryId++;
            entry.Selected = true;
            _entries.Add(entry);
        }
    }

    private RecordedEntry? TryBuildEntry(LogSystemEvent ev)
    {
        return ev switch
        {
            LogSystemCastStartEvent cast when _config.RecordCastStart && IsHostileNpc(cast.SourceId) => new RecordedEntry
            {
                TimestampUtc = ToUtc(cast.Timestamp),
                Kind = RecordedEventKind.CastStart,
                Name = ResolveActionName(cast.ActionId),
                ActionId = cast.ActionId,
                SourceId = cast.SourceId,
                TargetId = cast.TargetId,
                SourceName = ResolveActorName(cast.SourceId),
                TargetName = cast.TargetId == cast.SourceId ? "无目标" : ResolveActorName(cast.TargetId),
                DurationMilliseconds = cast.DurationMilliseconds,
                Summary = $"读条 {ResolveActionName(cast.ActionId)}",
                Detail = $"Source={FormatHex(cast.SourceId)} Target={FormatHex(cast.TargetId)} Cast={cast.CastTime:F2}s",
                DeduplicationKey = $"cast:{cast.SourceId}:{cast.ActionId}:{cast.TargetId}"
            },
            LogSystemActionEffectEvent effect when ShouldRecordActionEffect(effect) => new RecordedEntry
            {
                TimestampUtc = ToUtc(effect.Timestamp),
                Kind = IsAutoAttack(effect.ActionId) ? RecordedEventKind.AutoAttack : RecordedEventKind.ActionEffect,
                Name = ResolveActionName(effect.ActionId),
                ActionId = effect.ActionId,
                SourceId = effect.SourceId,
                TargetId = effect.TargetId,
                SourceName = ResolveActorName(effect.SourceId),
                TargetName = ResolveActorName(effect.TargetId),
                Summary = $"敌方技能效果 {ResolveActionName(effect.ActionId)}",
                Detail = $"Source={FormatHex(effect.SourceId)} Target={FormatHex(effect.TargetId)} EffectType={effect.EffectType}",
                DeduplicationKey = $"effect:{effect.SourceId}:{effect.ActionId}:{effect.TargetId}:{effect.EffectType}"
            },
            LogSystemStatusAddEvent status when _config.RecordStatus && IsRelevantStatusAdd(status) => new RecordedEntry
            {
                TimestampUtc = ToUtc(status.Timestamp),
                Kind = RecordedEventKind.StatusAdd,
                Name = ResolveStatusName(status.StatusId),
                StatusId = status.StatusId,
                SourceId = status.SourceId,
                TargetId = status.TargetId,
                SourceName = ResolveActorName(status.SourceId),
                TargetName = ResolveActorName(status.TargetId),
                DurationMilliseconds = status.DurationMilliseconds,
                StackCount = status.StackCount,
                Summary = BuildStatusSummary(status),
                Detail = $"Source={FormatHex(status.SourceId)} Target={FormatHex(status.TargetId)} Stack={status.StackCount} Duration={status.Duration:F2}s",
                DeduplicationKey = $"statusadd:{status.SourceId}:{status.TargetId}:{status.StatusId}:{status.StackCount}"
            },
            LogSystemTargetIconEvent marker when _config.RecordTargetMarks && IsRelevantTargetMarker(marker.SourceId, marker.TargetId) => new RecordedEntry
            {
                TimestampUtc = ToUtc(marker.Timestamp),
                Kind = RecordedEventKind.TargetIcon,
                Name = BuildTargetIconName(marker.MarkerId, marker.TargetId),
                SourceId = marker.SourceId,
                TargetId = marker.TargetId,
                SourceName = ResolveActorName(marker.SourceId),
                TargetName = ResolveActorName(marker.TargetId),
                Value = marker.MarkerId,
                Summary = BuildTargetIconName(marker.MarkerId, marker.TargetId),
                Detail = $"Source={FormatHex(marker.SourceId)} Target={FormatHex(marker.TargetId)}",
                DeduplicationKey = $"targeticon:{marker.TargetId}:{marker.MarkerId}"
            },
            LogSystemMarkerEvent marker when _config.RecordTargetMarks && IsRelevantTargetMarker(marker.SourceId, marker.TargetId) => new RecordedEntry
            {
                TimestampUtc = ToUtc(marker.Timestamp),
                Kind = RecordedEventKind.TargetIcon,
                Name = BuildTargetIconName(marker.MarkerId, marker.TargetId),
                SourceId = marker.SourceId,
                TargetId = marker.TargetId,
                SourceName = ResolveActorName(marker.SourceId),
                TargetName = ResolveActorName(marker.TargetId),
                Value = marker.MarkerId,
                Summary = BuildTargetIconName(marker.MarkerId, marker.TargetId),
                Detail = $"Source={FormatHex(marker.SourceId)} Target={FormatHex(marker.TargetId)}",
                DeduplicationKey = $"marker:{marker.SourceId}:{marker.TargetId}:{marker.MarkerId}"
            },
            LogSystemTetherEvent tether when _config.RecordTargetMarks && IsRelevantTether(tether.SourceId, tether.TargetId) => new RecordedEntry
            {
                TimestampUtc = ToUtc(tether.Timestamp),
                Kind = RecordedEventKind.Tether,
                Name = $"连线 {tether.TetherId}",
                SourceId = tether.SourceId,
                TargetId = tether.TargetId,
                SourceName = ResolveActorName(tether.SourceId),
                TargetName = ResolveActorName(tether.TargetId),
                Value = tether.TetherId,
                Summary = $"连线 {tether.TetherId}",
                Detail = $"Source={FormatHex(tether.SourceId)} Target={FormatHex(tether.TargetId)}",
                DeduplicationKey = $"tether:{tether.SourceId}:{tether.TargetId}:{tether.TetherId}"
            },
            LogSystemMapEffectEvent map when _config.RecordEnvironment => new RecordedEntry
            {
                TimestampUtc = ToUtc(map.Timestamp),
                Kind = RecordedEventKind.MapEffect,
                Name = "MapEffect",
                Value = map.Position,
                Summary = $"MapEffect {map.Position}",
                Detail = $"Position={map.Position} Param1={map.Param1} Param2={map.Param2}",
                DeduplicationKey = $"mapeffect:{map.Position}:{map.Param1}:{map.Param2}"
            },
            LogSystemObjectCreateEvent obj when _config.RecordEnvironment && IsRelevantEnvironmentObject(obj.SourceId, obj.ObjectKind) => new RecordedEntry
            {
                TimestampUtc = ToUtc(obj.Timestamp),
                Kind = RecordedEventKind.ObjectCreate,
                Name = $"对象出现 {obj.DataId}",
                SourceId = obj.SourceId,
                SourceName = ResolveActorName(obj.SourceId),
                DataId = obj.DataId,
                Position = obj.SourcePos,
                Summary = $"对象出现 {obj.DataId}",
                Detail = $"Source={FormatHex(obj.SourceId)} Kind={obj.ObjectKind}",
                DeduplicationKey = $"objectcreate:{obj.DataId}"
            },
            LogSystemTargetableEvent obj when _config.RecordEnvironment && IsRelevantEnvironmentObject(obj.SourceId, obj.ObjectKind) => new RecordedEntry
            {
                TimestampUtc = ToUtc(obj.Timestamp),
                Kind = RecordedEventKind.Targetable,
                Name = obj.IsTargetable ? $"可选中 {obj.DataId}" : $"不可选中 {obj.DataId}",
                SourceId = obj.SourceId,
                SourceName = ResolveActorName(obj.SourceId),
                DataId = obj.DataId,
                Position = obj.SourcePos,
                Summary = obj.IsTargetable ? $"对象可选中 {obj.DataId}" : $"对象不可选中 {obj.DataId}",
                Detail = $"Source={FormatHex(obj.SourceId)} Kind={obj.ObjectKind}",
                DeduplicationKey = $"targetable:{obj.DataId}:{obj.IsTargetable}"
            },
            LogSystemWaymarkEvent waymark when _config.RecordWaymarks => new RecordedEntry
            {
                TimestampUtc = ToUtc(waymark.Timestamp),
                Kind = RecordedEventKind.Waymark,
                Name = $"场地标记 {waymark.MarkerId}",
                Value = waymark.MarkerId,
                Position = waymark.Position,
                Summary = waymark.Active ? $"放置场地标记 {waymark.MarkerId}" : $"移除场地标记 {waymark.MarkerId}",
                Detail = $"Active={waymark.Active} Pos={FormatPosition(waymark.Position)}",
                DeduplicationKey = $"waymark:{waymark.MarkerId}:{waymark.Active}:{FormatPosition(waymark.Position)}"
            },
            LogSystemTerritoryEvent territory when _config.RecordEnvironment => new RecordedEntry
            {
                TimestampUtc = ToUtc(territory.Timestamp),
                Kind = RecordedEventKind.Territory,
                Name = territory.TerritoryName,
                Value = territory.TerritoryId,
                Summary = $"区域 {territory.TerritoryName}",
                Detail = $"TerritoryId={territory.TerritoryId}",
                DeduplicationKey = $"territory:{territory.TerritoryId}"
            },
            _ => null
        };
    }

    private static bool IsRelevantStatusAdd(LogSystemStatusAddEvent status)
    {
        if (status.SourceId != 0 && IsPartyMember(status.SourceId))
            return false;

        if (IsPartyMember(status.TargetId))
            return IsHostileNpc(status.SourceId);

        if (!IsHostileNpc(status.TargetId))
            return false;

        return status.SourceId == 0 || IsHostileNpc(status.SourceId);
    }

    private bool ShouldRecordActionEffect(LogSystemActionEffectEvent effect)
    {
        if (!IsHostileNpc(effect.SourceId))
            return false;

        if (IsAutoAttack(effect.ActionId))
            return _config.RecordAutoAttack;

        if (!_config.RecordActionEffect)
            return false;

        return HasActionName(effect.ActionId);
    }

    private static bool IsAutoAttack(uint actionId)
    {
        if (actionId == AutoAttackActionId)
            return true;

        var row = Svc.Data.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(actionId);
        var name = row?.Name.ToString();
        return string.Equals(name, "攻击", StringComparison.Ordinal)
            || string.Equals(name, "攻撃", StringComparison.Ordinal)
            || string.Equals(name, "Attack", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasActionName(uint actionId)
    {
        var row = Svc.Data.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(actionId);
        var name = row?.Name.ToString();
        return !string.IsNullOrWhiteSpace(name);
    }

    private static string BuildStatusSummary(LogSystemStatusAddEvent status)
    {
        var name = ResolveStatusName(status.StatusId);
        return IsPartyMember(status.TargetId)
            ? $"队伍减益 {name}"
            : $"Boss增益 {name}";
    }

    private static string BuildTargetIconName(uint markerId, ulong targetId)
    {
        var target = ResolveActorName(targetId);
        return string.IsNullOrWhiteSpace(target) || target.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? "点名 未知目标"
            : $"点名 {target}";
    }

    private static bool IsRelevantTargetMarker(ulong sourceId, ulong targetId)
        => IsPartyMember(targetId) || IsHostileNpc(sourceId);

    private static bool IsRelevantTether(ulong sourceId, ulong targetId)
        => IsHostileNpc(sourceId) || IsPartyMember(sourceId) || IsPartyMember(targetId);

    private static bool IsRelevantEnvironmentObject(ulong sourceId, byte objectKind)
    {
        if ((ObjectKind)objectKind == ObjectKind.Pc)
            return false;

        var obj = TryGetObject(sourceId);
        return obj == null || obj.ObjectKind != ObjectKind.Pc;
    }

    private static bool IsHostileNpc(ulong entityId)
    {
        if (TryGetObject(entityId) is not IBattleChara battleChara)
            return false;

        return battleChara.ObjectKind != ObjectKind.Pc && battleChara.IsEnemy();
    }

    private static bool IsPartyMember(ulong entityId)
    {
        if (entityId == 0)
            return false;

        var player = global::PromeRotation.Core.Core.Me;
        if (player != null && MatchesObjectId(player, entityId))
            return true;

        try
        {
            return PartyHelper.GetUIParty().Any(member => MatchesObjectId(member, entityId));
        }
        catch
        {
            return false;
        }
    }

    private static IGameObject? TryGetObject(ulong objectOrEntityId)
    {
        try
        {
            if (objectOrEntityId <= uint.MaxValue)
            {
                var byEntityId = Svc.Objects.SearchByEntityId((uint)objectOrEntityId);
                if (byEntityId != null)
                    return byEntityId;
            }

            for (var i = 0; i < Svc.Objects.Length; i++)
            {
                var obj = Svc.Objects[i];
                if (obj == null)
                    continue;

                if (MatchesObjectId(obj, objectOrEntityId))
                    return obj;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool MatchesObjectId(IGameObject obj, ulong objectOrEntityId)
        => obj.EntityId == objectOrEntityId || obj.GameObjectId == objectOrEntityId;

    private void StartSession(string reason)
    {
        lock (_sync)
        {
            _entries.Clear();
            _recentKeys.Clear();
            _nextEntryId = 1;
            _sessionStartUtc = DateTime.UtcNow;
            _sessionEndUtc = null;
            _sessionActive = true;
            _status = reason;
        }
    }

    private void EndSession(string reason, bool export)
    {
        List<RecordedEntry> snapshot;
        lock (_sync)
        {
            if (!_sessionActive)
                return;

            snapshot = _entries.ToList();
            _sessionEndUtc = DateTime.UtcNow;
            _sessionActive = false;
            _status = reason;
        }

        if (export && snapshot.Count > 0)
            ExportSession(snapshot, reason);
    }

    private void ClearSession(string reason)
    {
        lock (_sync)
        {
            _entries.Clear();
            _recentKeys.Clear();
            _nextEntryId = 1;
            _sessionEndUtc = null;
            _sessionActive = false;
            _status = reason;
        }
    }

    private void ExportSession()
    {
        List<RecordedEntry> snapshot;
        lock (_sync)
        {
            snapshot = GetExportableEntries(_entries);
        }

        ExportSession(snapshot, "手动导出");
    }

    private void ExportSelectedSession()
    {
        List<RecordedEntry> snapshot;
        lock (_sync)
        {
            snapshot = GetExportableEntries(_entries.Where(entry => entry.Selected));
        }

        ExportSession(snapshot, "导出选中");
    }

    private void ExportSession(List<RecordedEntry> entries, string reason)
    {
        if (entries.Count == 0)
        {
            _status = "没有可导出的记录";
            return;
        }

        try
        {
            var exportEntries = SimplifyEntriesForExport(entries);
            var document = BuildPureTimelineDocument(exportEntries, reason);
            var outputDirectory = ResolveOutputDirectory();
            Directory.CreateDirectory(outputDirectory);

            var exportedAt = DateTime.Now;
            var jsonPath = Path.Combine(outputDirectory, BuildFileName(document.Meta.Name, exportedAt, ".json"));
            var cactbotPath = Path.Combine(outputDirectory, BuildFileName(document.Meta.Name, exportedAt, ".txt"));
            var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            var cactbotTimeline = BuildCactbotTimeline(exportEntries);

            File.WriteAllText(jsonPath, json);
            File.WriteAllText(cactbotPath, cactbotTimeline, Encoding.UTF8);
            _lastExportPath = $"JSON: {jsonPath}\nCactbot: {cactbotPath}";
            _status = $"已导出 JSON/TXT {exportEntries.Count} 条";
        }
        catch (Exception ex)
        {
            _status = $"导出失败: {ex.Message}";
            Svc.Log.Error(ex, "[WhiteShaftGenerator] 导出失败");
        }
    }

    private static List<RecordedEntry> SimplifyEntriesForExport(IReadOnlyList<RecordedEntry> entries)
    {
        var result = new List<RecordedEntry>();
        var recentKeys = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var recentSkillNames = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        foreach (var entry in entries.OrderBy(entry => entry.TimestampUtc))
        {
            if (ShouldSuppressExportEntry(entry, recentSkillNames))
                continue;

            var mergeWindowMs = GetExportMergeWindowMs(entry);
            if (mergeWindowMs > 0)
            {
                var key = BuildExportMergeKey(entry);
                if (recentKeys.TryGetValue(key, out var last)
                    && (entry.TimestampUtc - last).TotalMilliseconds <= mergeWindowMs)
                {
                    continue;
                }

                recentKeys[key] = entry.TimestampUtc;
            }

            result.Add(entry);
            RememberRecentSkill(entry, recentSkillNames);
        }

        return result;
    }

    private static bool ShouldSuppressExportEntry(RecordedEntry entry, Dictionary<string, DateTime> recentSkillNames)
    {
        if (entry.Kind != RecordedEventKind.StatusAdd)
            return false;

        var name = NormalizeEventName(entry.Name);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var key = BuildNamedSourceKey(name, entry.SourceName);
        return recentSkillNames.TryGetValue(key, out var last)
               && (entry.TimestampUtc - last).TotalMilliseconds <= ExportStatusNearSkillSuppressMs;
    }

    private static void RememberRecentSkill(RecordedEntry entry, Dictionary<string, DateTime> recentSkillNames)
    {
        if (entry.Kind is not (RecordedEventKind.CastStart or RecordedEventKind.ActionEffect or RecordedEventKind.AutoAttack))
            return;

        var name = NormalizeEventName(entry.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var key = BuildNamedSourceKey(name, entry.SourceName);
        recentSkillNames[key] = entry.TimestampUtc;
    }

    private static int GetExportMergeWindowMs(RecordedEntry entry)
    {
        return entry.Kind switch
        {
            RecordedEventKind.CastStart or RecordedEventKind.ActionEffect or RecordedEventKind.AutoAttack => ExportSkillMergeMs,
            RecordedEventKind.Targetable => ExportTargetableMergeMs,
            RecordedEventKind.TargetIcon => ExportTargetIconMergeMs,
            _ => 0
        };
    }

    private static string BuildExportMergeKey(RecordedEntry entry)
    {
        if (entry.Kind == RecordedEventKind.Targetable)
        {
            var state = entry.Name.StartsWith("可选中", StringComparison.Ordinal) ? "targetable" : "untargetable";
            return $"targetable:{entry.DataId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}:{state}";
        }

        return entry.Kind switch
        {
            RecordedEventKind.CastStart when entry.ActionId is { } actionId => $"cast:{actionId}:{entry.SourceName}",
            RecordedEventKind.ActionEffect when entry.ActionId is { } actionId => $"effect:{actionId}:{entry.SourceName}",
            RecordedEventKind.AutoAttack when entry.ActionId is { } actionId => $"auto:{actionId}:{entry.SourceName}",
            RecordedEventKind.TargetIcon => $"targeticon:{entry.Name}:{entry.TargetName}",
            _ => $"{entry.Kind}:{entry.Name}:{entry.SourceName}"
        };
    }

    private static string BuildNamedSourceKey(string name, string sourceName)
        => $"{name}:{sourceName}";

    private PureTimelineDocument BuildPureTimelineDocument(IReadOnlyList<RecordedEntry> entries, string reason)
    {
        var territoryId = (int)Svc.ClientState.TerritoryType;
        var jobId = (int)(PromeRotation.Core.Core.Me?.ClassJob.RowId ?? 0);
        var territoryName = ResolveTerritoryName((uint)territoryId);
        var jobName = ResolveJobName((uint)jobId);
        var title = $"{territoryName}_{jobName}_白轴";
        var sessionStartUtc = _sessionStartUtc == default
            ? entries.Min(entry => entry.TimestampUtc)
            : _sessionStartUtc;

        var anchors = BuildPureTimelineAnchors(entries, sessionStartUtc);
        var maxTime = anchors.Count == 0 ? 0f : anchors.Max(anchor => anchor.Time);
        anchors.Add(new PureTimelineAnchor
        {
            Name = "结束",
            Time = maxTime + 30f,
            IsEndAnchor = true
        });

        return new PureTimelineDocument
        {
            Meta = new PureTimelineMetadata
            {
                Name = title,
                Author = _config.Author,
                AcrAuthor = _config.Author,
                JobId = jobId,
                TerritoryId = territoryId,
                CreatedAt = DateTime.Now,
                Remark = $"由 {RecorderVersion} 导出，原因: {reason}，记录条目: {entries.Count}"
            },
            Anchors = anchors
        };
    }

    private static List<PureTimelineAnchor> BuildPureTimelineAnchors(IReadOnlyList<RecordedEntry> entries, DateTime sessionStartUtc)
    {
        var anchors = new List<PureTimelineAnchor>
        {
            new()
            {
                Name = "--同步--",
                Time = 0f,
                Sync = new PureTimelineSyncRule
                {
                    Type = "InCombat",
                    WindowBefore = 0f,
                    WindowAfter = 1f
                }
            }
        };

        var firstSyncWritten = false;
        foreach (var entry in entries.OrderBy(entry => entry.TimestampUtc))
            anchors.Add(BuildPureTimelineAnchor(entry, sessionStartUtc, ref firstSyncWritten));

        return anchors;
    }

    private static PureTimelineAnchor BuildPureTimelineAnchor(RecordedEntry entry, DateTime sessionStartUtc, ref bool firstSyncWritten)
    {
        var sync = BuildPureTimelineSync(entry, sessionStartUtc, ref firstSyncWritten);
        return new PureTimelineAnchor
        {
            Name = entry.ToPureTimelineName(),
            Time = (float)Math.Round(GetElapsedSeconds(entry.TimestampUtc, sessionStartUtc), 3),
            Remark = entry.ToDisplayText(sessionStartUtc),
            Sync = sync
        };
    }

    private static PureTimelineSyncRule? BuildPureTimelineSync(RecordedEntry entry, DateTime sessionStartUtc, ref bool firstSyncWritten)
    {
        var type = entry.Kind switch
        {
            RecordedEventKind.CastStart when entry.ActionId is > 0 => "CastStart",
            RecordedEventKind.ActionEffect when entry.ActionId is > 0 => "ActionEffect",
            RecordedEventKind.AutoAttack when entry.ActionId is > 0 => "ActionEffect",
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(type) || entry.ActionId == null)
            return null;

        var rule = new PureTimelineSyncRule
        {
            Type = type,
            Params = new Dictionary<string, string>
            {
                ["ActionId"] = entry.ActionId.Value.ToString(CultureInfo.InvariantCulture)
            }
        };

        if (!firstSyncWritten)
        {
            rule.WindowBefore = (float)Math.Round(GetElapsedSeconds(entry.TimestampUtc, sessionStartUtc), 1);
            rule.WindowAfter = 5f;
            firstSyncWritten = true;
        }
        else if (entry.Kind is RecordedEventKind.ActionEffect or RecordedEventKind.AutoAttack)
        {
            rule.WindowBefore = 0f;
            rule.WindowAfter = 0f;
        }
        else
        {
            rule.WindowBefore = 2.5f;
            rule.WindowAfter = 2.5f;
        }

        return rule;
    }

    private TimelineDocument BuildTimelineDocument(IReadOnlyList<RecordedEntry> entries, string reason)
    {
        var territoryId = (int)Svc.ClientState.TerritoryType;
        var jobId = (int)(PromeRotation.Core.Core.Me?.ClassJob.RowId ?? 0);
        var territoryName = ResolveTerritoryName((uint)territoryId);
        var jobName = ResolveJobName((uint)jobId);
        var title = $"{territoryName}_{jobName}_白轴";
        var nextId = 1;
        var sessionStartUtc = _sessionStartUtc == default
            ? entries.Min(entry => entry.TimestampUtc)
            : _sessionStartUtc;

        var root = new TimelineNode
        {
            Id = nextId++,
            Type = "serial",
            Name = title,
            Remark = $"由 {RecorderVersion} 导出，原因: {reason}，DelayMs 为相邻事件间隔",
            Children = BuildTimelineNodes(entries, sessionStartUtc, ref nextId)
        };

        return new TimelineDocument
        {
            Meta = new TimelineMetadata
            {
                Name = title,
                Author = _config.Author,
                JobId = jobId,
                TerritoryId = territoryId,
                CreatedAt = DateTime.Now,
                Remark = $"记录条目: {entries.Count}"
            },
            Root = root
        };
    }

    private static TimelineNode[] BuildTimelineNodes(IReadOnlyList<RecordedEntry> entries, DateTime sessionStartUtc, ref int nextId)
    {
        var nodes = new List<TimelineNode>(entries.Count);
        var previousUtc = sessionStartUtc;

        foreach (var entry in entries.OrderBy(entry => entry.TimestampUtc))
        {
            var delayMs = Math.Max(0, (float)(entry.TimestampUtc - previousUtc).TotalMilliseconds);
            nodes.Add(BuildTimelineNode(entry, sessionStartUtc, delayMs, ref nextId));
            previousUtc = entry.TimestampUtc;
        }

        return nodes.ToArray();
    }

    private static TimelineNode BuildTimelineNode(RecordedEntry entry, DateTime sessionStartUtc, float delayMs, ref int nextId)
    {
        var displayText = entry.ToDisplayText(sessionStartUtc);
        var condition = BuildCondition(entry);
        var action = new ActionDto
        {
            Type = "CustomLog",
            Message = entry.ToTimelineMessage()
        };

        return new TimelineNode
        {
            Id = nextId++,
            Type = condition == null ? "action" : "condition",
            Name = entry.Name,
            DelayMs = delayMs,
            Remark = displayText,
            Condition = condition,
            Action = condition == null ? action : null,
            Children = condition == null
                ? null
                : new[]
                {
                    new TimelineNode
                    {
                        Id = nextId++,
                        Type = "action",
                        Name = "记录提示",
                        Action = action
                    }
                }
        };
    }

    private static ConditionDto? BuildCondition(RecordedEntry entry)
    {
        return entry.Kind switch
        {
            RecordedEventKind.CastStart when entry.ActionId is > 0 => new ConditionDto
            {
                Type = "CastStart",
                Regex = $"^{entry.ActionId}$",
                Immediate = false
            },
            (RecordedEventKind.ActionEffect or RecordedEventKind.AutoAttack) when entry.ActionId is > 0 => new ConditionDto
            {
                Type = "ActionEffect",
                Regex = $"^{entry.ActionId}$",
                Immediate = false
            },
            _ => null
        };
    }

    private string BuildCactbotTimeline(IReadOnlyList<RecordedEntry> entries)
    {
        var sessionStartUtc = _sessionStartUtc == default
            ? entries.Min(entry => entry.TimestampUtc)
            : _sessionStartUtc;
        var territoryId = (uint)Svc.ClientState.TerritoryType;
        var territoryName = ResolveTerritoryName(territoryId);
        var builder = new StringBuilder();

        builder.AppendLine($"### {EscapeCactbotHeader(territoryName)}");
        builder.AppendLine($"# ZoneId: {territoryId}");
        builder.AppendLine();
        builder.AppendLine("hideall \"--Reset--\"");
        builder.AppendLine("hideall \"--sync--\"");
        builder.AppendLine();
        builder.AppendLine("0.0 \"--sync--\" InCombat { inGameCombat: \"1\" } window 0,1");

        var ordered = entries.OrderBy(entry => entry.TimestampUtc).ToList();
        var firstSyncWritten = false;
        foreach (var entry in ordered)
        {
            var line = BuildCactbotLine(entry, sessionStartUtc, ref firstSyncWritten);
            if (!string.IsNullOrWhiteSpace(line))
                builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static string BuildCactbotLine(RecordedEntry entry, DateTime sessionStartUtc, ref bool firstSyncWritten)
    {
        var time = FormatCactbotTime(entry.TimestampUtc, sessionStartUtc);
        var name = EscapeCactbotName(entry.ToCactbotName(includeActorPart: false));

        if (entry.Kind == RecordedEventKind.CastStart && entry.ActionId is { } castActionId)
        {
            var duration = entry.DurationMilliseconds is > 0
                ? $" duration {FormatCactbotSeconds(entry.DurationMilliseconds.Value / 1000f)}"
                : string.Empty;
            var source = BuildCactbotSourceCondition(entry);
            var window = BuildCactbotSyncWindow(time, ref firstSyncWritten);
            return $"{time} \"{name}\" StartsUsing {{ id: \"{castActionId}\"{source} }}{duration}{window}";
        }

        if (entry.Kind is RecordedEventKind.ActionEffect or RecordedEventKind.AutoAttack
            && entry.ActionId is { } actionId)
        {
            var source = BuildCactbotSourceCondition(entry);
            var window = BuildCactbotSyncWindow(time, ref firstSyncWritten);
            return $"{time} \"{name}\" Ability {{ id: \"{actionId}\"{source} }}{window}";
        }

        return $"{time} \"{name}\"";
    }

    private static string BuildCactbotSyncWindow(string time, ref bool firstSyncWritten)
    {
        if (firstSyncWritten)
            return " window 2.5,2.5";

        firstSyncWritten = true;
        return $" window {time},5";
    }

    private static string BuildCactbotSourceCondition(RecordedEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SourceName) || entry.SourceId == 0)
            return string.Empty;

        if (entry.SourceName.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return $", source: \"{EscapeCactbotConditionValue(entry.SourceName)}\"";
    }

    private void DrawRecentEntries()
    {
        List<RecordedEntry> snapshot;
        lock (_sync)
        {
            snapshot = _entries.ToList();
        }

        ImGui.Separator();
        ImGui.Text($"记录列表 ({snapshot.Count})");
        if (!ImGui.BeginChild("##WhiteShaftGeneratorEntries", new Vector2(0, 320), true))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var entry in snapshot)
        {
            var selected = entry.Selected;
            if (ImGui.Checkbox($"##war_select_{entry.Id}", ref selected))
                SetEntrySelected(entry.Id, selected);

            ImGui.SameLine();
            ImGui.TextWrapped(entry.ToDisplayText(_sessionStartUtc));
        }

        ImGui.EndChild();
    }

    private int GetEntryCount()
    {
        lock (_sync)
        {
            return _entries.Count;
        }
    }

    private int GetSelectedEntryCount()
    {
        lock (_sync)
        {
            return _entries.Count(entry => entry.Selected);
        }
    }

    private void SetEntrySelected(long id, bool selected)
    {
        lock (_sync)
        {
            var entry = _entries.FirstOrDefault(item => item.Id == id);
            if (entry != null)
                entry.Selected = selected;
        }
    }

    private void SetAllEntriesSelected(bool selected)
    {
        lock (_sync)
        {
            foreach (var entry in _entries)
                entry.Selected = selected;
        }
    }

    private void RemoveUnselectedEntries()
    {
        lock (_sync)
        {
            var removed = _entries.RemoveAll(entry => !entry.Selected);
            _status = removed > 0 ? $"已删除未选 {removed} 条" : "没有未选记录";
        }
    }

    private void RemoveAutoAttackEntries(string reason)
    {
        lock (_sync)
        {
            var removed = _entries.RemoveAll(IsAutoAttackEntry);
            _status = removed > 0 ? $"{reason} {removed} 条" : reason;
        }
    }

    private List<RecordedEntry> GetExportableEntries(IEnumerable<RecordedEntry> entries)
    {
        var result = new List<RecordedEntry>();
        var environmentKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!_config.RecordAutoAttack && IsAutoAttackEntry(entry))
                continue;

            if (TryBuildEnvironmentUniqueKey(entry, out var key) && !environmentKeys.Add(key))
                continue;

            result.Add(entry);
        }

        return result;
    }

    private static bool IsAutoAttackEntry(RecordedEntry entry)
        => entry.Kind == RecordedEventKind.AutoAttack || (entry.ActionId is { } actionId && IsAutoAttack(actionId));

    private static bool TryBuildEnvironmentUniqueKey(RecordedEntry entry, out string key)
    {
        if (entry.Kind == RecordedEventKind.ObjectCreate && entry.DataId is { } objectDataId)
        {
            key = $"objectcreate:{objectDataId}";
            return true;
        }

        if (entry.Kind == RecordedEventKind.Targetable && entry.DataId is { } targetableDataId)
        {
            var state = entry.Name.StartsWith("可选中", StringComparison.Ordinal) ? "targetable" : "untargetable";
            key = $"targetable:{targetableDataId}:{state}";
            return true;
        }

        key = string.Empty;
        return false;
    }

    private bool IsEnvironmentDuplicateInSession(RecordedEntry entry)
    {
        if (!TryBuildEnvironmentUniqueKey(entry, out var key))
            return false;

        return _entries.Any(existing => TryBuildEnvironmentUniqueKey(existing, out var existingKey)
                                        && string.Equals(existingKey, key, StringComparison.Ordinal));
    }

    private bool IsDuplicate(string key, int debounceMs, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(key) || debounceMs <= 0)
            return false;

        lock (_sync)
        {
            if (_recentKeys.TryGetValue(key, out var last)
                && (timestamp - last).TotalMilliseconds < debounceMs)
            {
                return true;
            }

            _recentKeys[key] = timestamp;
            return false;
        }
    }

    private void LoadConfig()
    {
        _configPath = Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "Settings", "WhiteShaftGenerator.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);

        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<RecorderConfig>(json) ?? new RecorderConfig();
                MigrateConfig();
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"[WhiteShaftGenerator] 配置读取失败，将使用默认值: {ex.Message}");
                _config = new RecorderConfig();
            }
        }

        if (string.IsNullOrWhiteSpace(_config.Author))
            _config.Author = "PR";

        Directory.CreateDirectory(ResolveOutputDirectory());
        _outputDirectoryInput = _config.OutputDirectory;
    }

    private void MigrateConfig()
    {
        if (_config.ConfigVersion >= CurrentConfigVersion)
            return;

        _config.RecordEnvironment = false;
        _config.RecordWaymarks = false;
        _config.AutoExportOnBattleEnd = false;
        _config.RecordAutoAttack = false;
        _config.ConfigVersion = CurrentConfigVersion;
        SaveConfig();
    }

    private void SaveConfig()
    {
        if (string.IsNullOrWhiteSpace(_configPath))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[WhiteShaftGenerator] 配置保存失败: {ex.Message}");
        }
    }

    private string ResolveOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_config.OutputDirectory))
        {
            return Path.IsPathRooted(_config.OutputDirectory)
                ? _config.OutputDirectory
                : Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, _config.OutputDirectory);
        }

        return Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "PureTimelines", "WhiteShaftGenerator");
    }

    private void OpenOutputDirectory()
    {
        try
        {
            var directory = ResolveOutputDirectory();
            Directory.CreateDirectory(directory);
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            };
            process.Start();
        }
        catch (Exception ex)
        {
            _status = $"打开目录失败: {ex.Message}";
        }
    }

    private static string BuildFileName(string name, DateTime timestamp, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return $"{safe}_{timestamp:yyyyMMdd_HHmmss}{extension}";
    }

    private static DateTime ToUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static bool IsInCombat()
        => global::PromeRotation.Core.GameData.IsInCombat();

    private static string FormatHex(uint value) => $"0x{value:X8}";

    private static string FormatHex(ulong value) => $"0x{value:X}";

    private static string FormatPosition(Vector3 value)
        => $"{value.X.ToString("F2", CultureInfo.InvariantCulture)},{value.Y.ToString("F2", CultureInfo.InvariantCulture)},{value.Z.ToString("F2", CultureInfo.InvariantCulture)}";

    private static string FormatPosition(Vector3? value)
        => value.HasValue ? FormatPosition(value.Value) : "无";

    private static string FormatSessionTime(DateTime value)
        => value == default ? "未开始" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatSessionTime(DateTime? value)
        => value.HasValue ? FormatSessionTime(value.Value) : "记录中/未结束";

    private static string FormatRelativeSeconds(DateTime timestampUtc, DateTime sessionStartUtc)
    {
        var seconds = GetElapsedSeconds(timestampUtc, sessionStartUtc);
        return $"+{seconds.ToString("F1", CultureInfo.InvariantCulture)}s";
    }

    private static double GetElapsedSeconds(DateTime timestampUtc, DateTime sessionStartUtc)
        => Math.Max(0, (timestampUtc - sessionStartUtc).TotalSeconds);

    private static string FormatCactbotTime(DateTime timestampUtc, DateTime sessionStartUtc)
    {
        var seconds = GetElapsedSeconds(timestampUtc, sessionStartUtc);
        return seconds.ToString("F1", CultureInfo.InvariantCulture);
    }

    private static string FormatCactbotSeconds(float seconds)
        => Math.Max(0, seconds).ToString("F1", CultureInfo.InvariantCulture);

    private static string EscapeCactbotName(string value)
        => value.Replace('"', '\'')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');

    private static string EscapeCactbotConditionValue(string value)
        => value.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');

    private static string EscapeCactbotHeader(string value)
        => value.Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');

    private static string NormalizeEventName(string value)
    {
        var name = value.Trim();
        if (!name.EndsWith(")", StringComparison.Ordinal))
            return name;

        var open = name.LastIndexOf('(');
        if (open < 0 || open >= name.Length - 2)
            return name;

        var idPart = name[(open + 1)..^1];
        return idPart.All(char.IsDigit) ? name[..open].TrimEnd() : name;
    }

    private static string ResolveActorName(ulong objectOrEntityId)
    {
        if (objectOrEntityId == 0)
            return "无目标";

        var obj = TryGetObject(objectOrEntityId);
        if (obj is IPlayerCharacter player)
            return ResolveJobDisplayName(player.ClassJob.RowId);

        var objectName = obj?.Name.ToString();
        if (!string.IsNullOrWhiteSpace(objectName))
            return objectName;

        var localPlayer = global::PromeRotation.Core.Core.Me;
        if (localPlayer != null && MatchesObjectId(localPlayer, objectOrEntityId))
            return ResolveJobDisplayName(localPlayer.ClassJob.RowId);

        return FormatHex(objectOrEntityId);
    }

    private static string ResolveActionName(uint actionId)
    {
        var row = Svc.Data.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(actionId);
        var name = row?.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? actionId.ToString(CultureInfo.InvariantCulture) : $"{name}({actionId})";
    }

    private static string ResolveStatusName(uint statusId)
    {
        var row = Svc.Data.GetExcelSheet<Status>()?.GetRowOrDefault(statusId);
        var name = row?.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? statusId.ToString(CultureInfo.InvariantCulture) : $"{name}({statusId})";
    }

    private static string ResolveTerritoryName(uint territoryId)
    {
        var row = Svc.Data.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
        var name = row?.PlaceName.ValueNullable?.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? $"Territory{territoryId}" : name;
    }

    private static string ResolveJobName(uint jobId)
    {
        var row = Svc.Data.GetExcelSheet<ClassJob>()?.GetRowOrDefault(jobId);
        var name = row?.Abbreviation.ToString();
        return string.IsNullOrWhiteSpace(name) ? $"Job{jobId}" : name;
    }

    private static string ResolveJobDisplayName(uint jobId)
    {
        var row = Svc.Data.GetExcelSheet<ClassJob>()?.GetRowOrDefault(jobId);
        var name = row?.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? ResolveJobName(jobId) : name;
    }

    private sealed class RecorderConfig
    {
        public int ConfigVersion { get; set; } = CurrentConfigVersion;
        public bool RecordingEnabled { get; set; } = true;
        public bool AutoExportOnBattleEnd { get; set; }
        public bool RecordCastStart { get; set; } = true;
        public bool RecordActionEffect { get; set; } = true;
        public bool RecordAutoAttack { get; set; }
        public bool RecordStatus { get; set; } = true;
        public bool RecordTargetMarks { get; set; } = true;
        public bool RecordEnvironment { get; set; }
        public bool RecordWaymarks { get; set; }
        public int DeduplicationMs { get; set; } = DefaultDeduplicationMs;
        public string OutputDirectory { get; set; } = string.Empty;
        public string Author { get; set; } = "PR";
    }

    private sealed class RecordedEntry
    {
        public long Id { get; set; }
        public bool Selected { get; set; } = true;
        public DateTime TimestampUtc { get; init; }
        public RecordedEventKind Kind { get; init; }
        public string Name { get; init; } = string.Empty;
        public uint? ActionId { get; init; }
        public uint? StatusId { get; init; }
        public ulong SourceId { get; init; }
        public ulong TargetId { get; init; }
        public string SourceName { get; init; } = string.Empty;
        public string TargetName { get; init; } = string.Empty;
        public uint? DataId { get; init; }
        public uint? Value { get; init; }
        public uint? StackCount { get; init; }
        public int? DurationMilliseconds { get; init; }
        public Vector3? Position { get; init; }
        public string Summary { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string DeduplicationKey { get; init; } = string.Empty;

        public string ToDisplayText(DateTime sessionStartUtc)
        {
            var text = $"{GetDisplayName()}({BuildActorPart()}) {FormatRelativeSeconds(TimestampUtc, sessionStartUtc)}";
            if (Kind == RecordedEventKind.CastStart && DurationMilliseconds is > 0)
                text += $" 读条 {(DurationMilliseconds.Value / 1000f).ToString("F1", CultureInfo.InvariantCulture)}s";

            return text;
        }

        public string ToTimelineMessage()
        {
            var elapsed = TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            return $"{elapsed} [{Kind}] {Summary} {BuildActorPart()} {Detail}";
        }

        public string ToCactbotName(bool includeActorPart)
        {
            return Kind switch
            {
                RecordedEventKind.Targetable when Name.StartsWith("可选中", StringComparison.Ordinal) => "--可选中--",
                RecordedEventKind.Targetable when Name.StartsWith("不可选中", StringComparison.Ordinal) => "--不可选中--",
                _ => BuildCactbotDisplayName(includeActorPart)
            };
        }

        public string ToPureTimelineName()
        {
            return Kind switch
            {
                RecordedEventKind.CastStart => $"{GetDisplayName()} 读条",
                RecordedEventKind.ActionEffect or RecordedEventKind.AutoAttack => AppendSuffixIfMissing(GetDisplayName(), "判定"),
                RecordedEventKind.Targetable when Name.StartsWith("可选中", StringComparison.Ordinal) => "--可选中--",
                RecordedEventKind.Targetable when Name.StartsWith("不可选中", StringComparison.Ordinal) => "--不可选中--",
                RecordedEventKind.ObjectCreate when DataId is { } dataId => $"对象出现 {dataId}",
                RecordedEventKind.StatusAdd => Summary,
                _ => GetDisplayName()
            };
        }

        private string BuildCactbotDisplayName(bool includeActorPart, string suffix = "")
        {
            var baseName = Kind == RecordedEventKind.CastStart ? Name : GetDisplayName();
            var displayName = string.IsNullOrWhiteSpace(suffix)
                ? baseName
                : AppendSuffixIfMissing(baseName, suffix);
            return includeActorPart ? $"{displayName}({BuildActorPart()})" : displayName;
        }

        private static string AppendSuffixIfMissing(string value, string suffix)
        {
            var text = value.Trim();
            return text.EndsWith(suffix, StringComparison.Ordinal) ? text : $"{text} {suffix}";
        }

        private string BuildActorPart()
        {
            var source = string.IsNullOrWhiteSpace(SourceName) ? FormatHex(SourceId) : SourceName;
            var target = string.IsNullOrWhiteSpace(TargetName) ? FormatHex(TargetId) : TargetName;
            return $"来源:{source} 目标:{target}";
        }

        private string GetDisplayName()
        {
            if (Kind == RecordedEventKind.CastStart)
                return Name;

            if (ActionId is { } actionId && Name.EndsWith($"({actionId})", StringComparison.Ordinal))
                return Name[..^($"({actionId})".Length)];

            if (StatusId is { } statusId && Name.EndsWith($"({statusId})", StringComparison.Ordinal))
                return Name[..^($"({statusId})".Length)];

            return Name;
        }
    }

    private enum RecordedEventKind
    {
        CastStart,
        ActionEffect,
        AutoAttack,
        StatusAdd,
        StatusRemove,
        TargetIcon,
        Tether,
        MapEffect,
        ObjectCreate,
        Targetable,
        Waymark,
        Territory
    }
}

