using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2CombatRecorder;

internal sealed class HookFirstShadowComparison
{
    private sealed class CountedEvent
    {
        public int Count { get; set; }
        public required Dictionary<string, object?> Sample { get; init; }
    }

    private static readonly JsonSerializerOptions SummaryOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions KeyOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, Dictionary<string, CountedEvent>> _publicEventsByType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, CountedEvent>> _shadowEventsByType = new(StringComparer.Ordinal);

    public void RecordPublic(Dictionary<string, object?> evt)
    {
        Record(_publicEventsByType, evt);
    }

    public void RecordShadowCandidate(Dictionary<string, object?> evt)
    {
        Record(_shadowEventsByType, evt);
    }

    public string BuildSummaryJson(string battleId)
    {
        var eventTypes = _publicEventsByType.Keys
            .Union(_shadowEventsByType.Keys, StringComparer.Ordinal)
            .OrderBy(eventType => eventType, StringComparer.Ordinal)
            .ToList();

        long totalPublicCount = 0;
        long totalShadowCount = 0;
        var matchedEventTypeCount = 0;
        var byEventType = new Dictionary<string, object?>(StringComparer.Ordinal);
        var mismatches = new List<Dictionary<string, object?>>();

        foreach (var eventType in eventTypes)
        {
            var publicEntries = _publicEventsByType.TryGetValue(eventType, out var publicBucket)
                ? publicBucket
                : new Dictionary<string, CountedEvent>(StringComparer.Ordinal);
            var shadowEntries = _shadowEventsByType.TryGetValue(eventType, out var shadowBucket)
                ? shadowBucket
                : new Dictionary<string, CountedEvent>(StringComparer.Ordinal);

            var publicCount = publicEntries.Values.Sum(entry => entry.Count);
            var shadowCount = shadowEntries.Values.Sum(entry => entry.Count);
            totalPublicCount += publicCount;
            totalShadowCount += shadowCount;

            var missingSamples = BuildDeltaSamples(publicEntries, shadowEntries);
            var extraSamples = BuildDeltaSamples(shadowEntries, publicEntries);
            var missingCount = missingSamples.Sum(sample => (int)sample["count"]!);
            var extraCount = extraSamples.Sum(sample => (int)sample["count"]!);
            var matched = missingCount == 0 && extraCount == 0;
            if (matched)
            {
                matchedEventTypeCount++;
            }

            byEventType[eventType] = new Dictionary<string, object?>
            {
                ["public_count"] = publicCount,
                ["shadow_candidate_count"] = shadowCount,
                ["matched"] = matched,
                ["missing_from_shadow_count"] = missingCount,
                ["extra_in_shadow_count"] = extraCount,
            };

            if (!matched)
            {
                var mismatch = new Dictionary<string, object?>
                {
                    ["event_type"] = eventType,
                    ["public_count"] = publicCount,
                    ["shadow_candidate_count"] = shadowCount,
                    ["missing_from_shadow_count"] = missingCount,
                    ["extra_in_shadow_count"] = extraCount,
                };
                if (missingSamples.Count > 0)
                {
                    mismatch["missing_from_shadow_samples"] = missingSamples.Take(3).ToList();
                }

                if (extraSamples.Count > 0)
                {
                    mismatch["extra_in_shadow_samples"] = extraSamples.Take(3).ToList();
                }

                mismatches.Add(mismatch);
            }
        }

        var summary = new Dictionary<string, object?>
        {
            ["battle_id"] = battleId,
            ["instrumented_event_types"] = eventTypes,
            ["public_event_count"] = totalPublicCount,
            ["shadow_candidate_event_count"] = totalShadowCount,
            ["matched_event_type_count"] = matchedEventTypeCount,
            ["mismatched_event_type_count"] = eventTypes.Count - matchedEventTypeCount,
            ["by_event_type"] = byEventType,
        };

        if (mismatches.Count > 0)
        {
            summary["mismatches"] = mismatches;
        }

        return JsonSerializer.Serialize(summary, SummaryOpts);
    }

    private static void Record(
        Dictionary<string, Dictionary<string, CountedEvent>> sink,
        Dictionary<string, object?> evt)
    {
        if (!evt.TryGetValue("event_type", out var eventTypeValue) ||
            eventTypeValue is not string eventType ||
            string.IsNullOrWhiteSpace(eventType))
        {
            return;
        }

        if (!sink.TryGetValue(eventType, out var bucket))
        {
            bucket = new Dictionary<string, CountedEvent>(StringComparer.Ordinal);
            sink[eventType] = bucket;
        }

        var semanticShape = BuildSemanticShape(evt);
        var semanticKey = JsonSerializer.Serialize(semanticShape, KeyOpts);
        if (bucket.TryGetValue(semanticKey, out var countedEvent))
        {
            countedEvent.Count++;
            return;
        }

        bucket[semanticKey] = new CountedEvent
        {
            Count = 1,
            Sample = semanticShape,
        };
    }

    private static Dictionary<string, object?> BuildSemanticShape(Dictionary<string, object?> evt)
    {
        var result = new Dictionary<string, object?>();
        CopyIfPresent(evt, result, "event_type");
        CopyIfPresent(evt, result, "turn_index");
        CopyIfPresent(evt, result, "phase");
        CopyIfPresent(evt, result, "resolution_id");
        CopyIfPresent(evt, result, "parent_resolution_id");
        CopyIfPresent(evt, result, "resolution_depth");
        CopyIfPresent(evt, result, "payload");
        return result;
    }

    private static void CopyIfPresent(
        Dictionary<string, object?> source,
        Dictionary<string, object?> destination,
        string key)
    {
        if (source.TryGetValue(key, out var value))
        {
            destination[key] = value;
        }
    }

    private static List<Dictionary<string, object?>> BuildDeltaSamples(
        Dictionary<string, CountedEvent> primary,
        Dictionary<string, CountedEvent> other)
    {
        var result = new List<Dictionary<string, object?>>();

        foreach (var (key, countedEvent) in primary.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            var otherCount = other.TryGetValue(key, out var otherEvent) ? otherEvent.Count : 0;
            var delta = countedEvent.Count - otherCount;
            if (delta <= 0)
            {
                continue;
            }

            result.Add(new Dictionary<string, object?>
            {
                ["count"] = delta,
                ["event"] = countedEvent.Sample,
            });
        }

        return result;
    }
}
