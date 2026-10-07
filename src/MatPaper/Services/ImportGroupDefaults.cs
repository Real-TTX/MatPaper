using System.Text.Json;
using System.Text.Json.Nodes;
using MatPaper.Data;

namespace MatPaper.Services;

/// <summary>
/// Works out what a rule in a group really uses: its own settings, completed by the group's target.
/// An empty storage location, owner or project comes from the group; "common area", "skip inbox" and
/// the tags of the group are added to the rule's own. The result is a detached copy - the stored rule is
/// never changed by a run.
/// </summary>
public static class ImportGroupDefaults
{
    public static IReadOnlyList<long> ParseTagIds(string? csv)
        => (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

    public static ImportTask Effective(ImportTask rule, ImportGroup? group)
    {
        var copy = new ImportTask
        {
            Id = rule.Id,
            Name = rule.Name,
            Type = rule.Type,
            IsEnabled = rule.IsEnabled,
            CronExpression = rule.CronExpression,
            SettingsJson = rule.SettingsJson,
            SyncState = rule.SyncState,
            GroupId = rule.GroupId,
            Priority = rule.Priority,
            UpdateState = rule.UpdateState,
            CreateDate = rule.CreateDate,
            CreateUserId = rule.CreateUserId,
            UpdateDate = rule.UpdateDate,
            UpdateUserId = rule.UpdateUserId
        };

        if (group is null)
        {
            return copy;
        }

        JsonObject settings;
        try
        {
            settings = JsonNode.Parse(string.IsNullOrWhiteSpace(rule.SettingsJson) ? "{}" : rule.SettingsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return copy;
        }

        InheritId(settings, "StorageLocationId", group.StorageLocationId);
        InheritId(settings, "OwnerUserId", group.OwnerUserId);
        InheritId(settings, "ProjectId", group.ProjectId);
        if (group.IsCommon) { settings["IsCommon"] = true; }
        if (group.SkipInbox) { settings["SkipInbox"] = true; }

        var groupTags = ParseTagIds(group.TagIds);
        if (groupTags.Count > 0)
        {
            var tags = new HashSet<long>();
            if (settings["TagIds"] is JsonArray existing)
            {
                foreach (var node in existing)
                {
                    if (node is not null && node.GetValueKind() == JsonValueKind.Number) { tags.Add(node.GetValue<long>()); }
                }
            }

            foreach (var id in groupTags) { tags.Add(id); }
            settings["TagIds"] = new JsonArray(tags.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
        }

        copy.SettingsJson = settings.ToJsonString();
        return copy;
    }

    private static void InheritId(JsonObject settings, string key, long? fromGroup)
    {
        if (fromGroup is null) { return; }
        var own = settings[key];
        var empty = own is null || own.GetValueKind() == JsonValueKind.Null
            || (own.GetValueKind() == JsonValueKind.Number && own.GetValue<long>() == 0);
        if (empty) { settings[key] = fromGroup.Value; }
    }
}
