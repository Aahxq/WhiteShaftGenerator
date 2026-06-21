using System.Text.Json.Serialization;

namespace WhiteShaftGenerator.Models;

public sealed class TimelineDocument
{
    public TimelineMetadata Meta { get; set; } = new();
    public TimelineNode Root { get; set; } = new()
    {
        Id = 1,
        Name = "白轴记录",
        Type = "serial"
    };
}

public sealed class TimelineMetadata
{
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public int JobId { get; set; }
    public int TerritoryId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Remark { get; set; } = string.Empty;
}

public sealed class TimelineNode
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "serial";
    public bool Enabled { get; set; } = true;
    public string? Remark { get; set; }
    public float? DelayMs { get; set; }
    public ConditionDto? Condition { get; set; }
    public ConditionDto[]? Conditions { get; set; }
    public ActionDto? Action { get; set; }
    public ActionDto[]? Actions { get; set; }
    public TimelineNode[]? Children { get; set; }
}

public sealed class ConditionDto
{
    public string Type { get; set; } = string.Empty;
    public ushort? ActionId { get; set; }
    public string? Regex { get; set; }
    public bool Immediate { get; set; }
    public string? Target { get; set; }
    public ushort? BuffId { get; set; }
    public string? Mode { get; set; }
    public float? Value { get; set; }
    public bool Negate { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Params { get; set; }
}

public sealed class ActionDto
{
    public string Type { get; set; } = string.Empty;
    public string? Message { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class PureTimelineDocument
{
    public int Version { get; set; } = 1;
    public PureTimelineMetadata Meta { get; set; } = new();
    public List<PureTimelineAnchor> Anchors { get; set; } = new();
    public List<PureTimelineEntry> Entries { get; set; } = new();
}

public sealed class PureTimelineMetadata
{
    public string Name { get; set; } = string.Empty;
    public int TerritoryId { get; set; }
    public int JobId { get; set; }
    public string Author { get; set; } = string.Empty;
    public string AcrAuthor { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Opener { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
}

public sealed class PureTimelineAnchor
{
    public Guid Guid { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public float Time { get; set; }
    public bool IsPhaseAnchor { get; set; }
    public bool IsEndAnchor { get; set; }
    public bool IsCommentAnchor { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Remark { get; set; }
    public PureTimelineSyncRule? Sync { get; set; }
}

public sealed class PureTimelineSyncRule
{
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, string> Params { get; set; } = new();
    public float WindowBefore { get; set; }
    public float WindowAfter { get; set; }
}

public sealed class PureTimelineEntry
{
    public Guid Guid { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid StartAnchorGuid { get; set; }
    public float Offset { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Remark { get; set; }
    public TimelineNode EntryGroup { get; set; } = new()
    {
        Type = "serial",
        Enabled = true
    };
}

