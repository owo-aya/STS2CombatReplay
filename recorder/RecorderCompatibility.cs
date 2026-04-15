using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;

namespace STS2CombatRecorder;

internal static class RecorderProtocol
{
    public const string Version = "0.2.0";
    public const string KnownVersionsCatalogFileName = "known-game-versions.json";
    public const string KnownVersionsCatalogEmbeddedResourceName = "STS2CombatRecorder.known-game-versions.json";
}

internal static class RecorderPaths
{
    public static string GetRecorderDirectory()
    {
        return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ??
               AppContext.BaseDirectory;
    }

    public static string GetCombatLogsRoot()
    {
        return Path.Combine(GetRecorderDirectory(), "combat_logs");
    }

    public static string GetKnownVersionsCatalogPath()
    {
        return Path.Combine(GetRecorderDirectory(), RecorderProtocol.KnownVersionsCatalogFileName);
    }
}

internal enum RecorderCompatStatus
{
    Verified,
    Unverified,
    Unsupported,
    Unknown,
}

internal static class RecorderCompatStatusExtensions
{
    public static string ToWireValue(this RecorderCompatStatus status)
    {
        return status switch
        {
            RecorderCompatStatus.Verified => "verified",
            RecorderCompatStatus.Unverified => "unverified",
            RecorderCompatStatus.Unsupported => "unsupported",
            _ => "unknown",
        };
    }

    public static RecorderCompatStatus Parse(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "verified" => RecorderCompatStatus.Verified,
            "unverified" => RecorderCompatStatus.Unverified,
            "unsupported" => RecorderCompatStatus.Unsupported,
            _ => RecorderCompatStatus.Unknown,
        };
    }
}

internal readonly record struct RecorderGameFingerprint(
    string? Channel,
    string? Version,
    string? Build,
    string? Sts2DllHash,
    string? AssemblyPath)
{
    public bool HasIdentity =>
        !string.IsNullOrWhiteSpace(Version) ||
        !string.IsNullOrWhiteSpace(Build) ||
        !string.IsNullOrWhiteSpace(Sts2DllHash);
}

internal sealed record RecorderCompatibilityAssessment(
    RecorderGameFingerprint Game,
    RecorderCompatStatus Status,
    IReadOnlyList<string> WarningCodes,
    IReadOnlyList<string> WarningMessages,
    string? CatalogPath);

internal sealed class RecorderReleaseInfoFile
{
    [JsonPropertyName("commit")]
    public string? Commit { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }
}

internal sealed record RecorderReleaseInfo(
    string? Version,
    string? Commit,
    string? Branch,
    string? SourcePath);

internal sealed class RecorderCompatCatalogEntry
{
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("build")]
    public string? Build { get; init; }

    [JsonPropertyName("sts2_dll_hash")]
    public string? Sts2DllHash { get; init; }

    [JsonPropertyName("compat_status")]
    public string? CompatStatus { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

internal sealed class RecorderCompatCatalog
{
    private sealed class CatalogFileModel
    {
        [JsonPropertyName("entries")]
        public List<RecorderCompatCatalogEntry>? Entries { get; init; }
    }

    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public RecorderCompatCatalog(
        string? sourcePath,
        IReadOnlyList<RecorderCompatCatalogEntry> entries)
    {
        SourcePath = sourcePath;
        Entries = entries;
    }

    public string? SourcePath { get; }
    public IReadOnlyList<RecorderCompatCatalogEntry> Entries { get; }

    public static RecorderCompatCatalog LoadDefault(
        string? overridePath = null,
        Assembly? resourceAssembly = null)
    {
        var path = string.IsNullOrWhiteSpace(overridePath)
            ? RecorderPaths.GetKnownVersionsCatalogPath()
            : overridePath;
        if (File.Exists(path))
        {
            var fileCatalog = LoadFromJsonFile(path);
            if (fileCatalog != null)
                return fileCatalog;
        }

        return LoadEmbedded(resourceAssembly) ??
               new RecorderCompatCatalog(path, Array.Empty<RecorderCompatCatalogEntry>());
    }

    internal static RecorderCompatCatalog? LoadEmbedded(Assembly? resourceAssembly = null)
    {
        var assembly = resourceAssembly ?? typeof(RecorderCompatCatalog).Assembly;

        try
        {
            using var stream = assembly.GetManifestResourceStream(
                RecorderProtocol.KnownVersionsCatalogEmbeddedResourceName);
            if (stream == null)
                return null;

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return ParseCatalogJson(
                json,
                $"embedded://{RecorderProtocol.KnownVersionsCatalogEmbeddedResourceName}");
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(RecorderCompatCatalog) + ".LoadEmbedded", ex);
            return null;
        }
    }

    private static RecorderCompatCatalog? LoadFromJsonFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return ParseCatalogJson(json, path);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(RecorderCompatCatalog) + ".LoadFromJsonFile", ex);
            return null;
        }
    }

    private static RecorderCompatCatalog ParseCatalogJson(string json, string sourcePath)
    {
        var model = JsonSerializer.Deserialize<CatalogFileModel>(json, CatalogJsonOptions);
        return new RecorderCompatCatalog(
            sourcePath,
            model?.Entries?.Where(entry => entry != null).ToList() ??
            new List<RecorderCompatCatalogEntry>());
    }
}

internal static class RecorderCompatResolver
{
    public static RecorderCompatibilityAssessment Resolve(
        RecorderGameFingerprint runtime,
        RecorderCompatCatalog catalog)
    {
        var matchedEntry = FindBestMatch(catalog.Entries, runtime);
        var status = matchedEntry != null
            ? RecorderCompatStatusExtensions.Parse(matchedEntry.CompatStatus)
            : runtime.HasIdentity
                ? RecorderCompatStatus.Unverified
                : RecorderCompatStatus.Unknown;

        var warningCodes = new List<string>();
        var warningMessages = new List<string>();

        switch (status)
        {
            case RecorderCompatStatus.Unverified:
                warningCodes.Add("unverified_game_version");
                warningMessages.Add(
                    $"Recorder is running on an unverified STS2 build ({FormatFingerprint(runtime, matchedEntry)}). Recording continues, but manual audit is recommended.");
                break;
            case RecorderCompatStatus.Unsupported:
                warningCodes.Add("unsupported_game_version");
                warningMessages.Add(
                    $"Recorder is running on an unsupported STS2 build ({FormatFingerprint(runtime, matchedEntry)}). Recording continues in degraded mode and outputs may be incomplete.");
                break;
            case RecorderCompatStatus.Unknown:
                warningCodes.Add("missing_version_info");
                warningMessages.Add(
                    "Recorder could not read enough STS2 version/build/fingerprint information to determine compatibility.");
                break;
        }

        var mergedFingerprint = new RecorderGameFingerprint(
            FirstNonBlank(runtime.Channel, matchedEntry?.Channel, "unknown"),
            FirstNonBlank(runtime.Version, matchedEntry?.Version),
            FirstNonBlank(runtime.Build, matchedEntry?.Build),
            FirstNonBlank(runtime.Sts2DllHash, matchedEntry?.Sts2DllHash),
            runtime.AssemblyPath);

        return new RecorderCompatibilityAssessment(
            mergedFingerprint,
            status,
            warningCodes,
            warningMessages,
            catalog.SourcePath);
    }

    private static RecorderCompatCatalogEntry? FindBestMatch(
        IReadOnlyList<RecorderCompatCatalogEntry> entries,
        RecorderGameFingerprint runtime)
    {
        if (entries.Count == 0)
            return null;

        var hashMatch = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Sts2DllHash))
            .Where(entry => EntryMatches(entry, runtime))
            .OrderByDescending(GetSpecificity)
            .FirstOrDefault();

        if (hashMatch != null)
            return hashMatch;

        return entries
            .Where(entry => string.IsNullOrWhiteSpace(entry.Sts2DllHash))
            .Where(entry => EntryMatches(entry, runtime))
            .OrderByDescending(GetSpecificity)
            .FirstOrDefault();
    }

    private static bool EntryMatches(
        RecorderCompatCatalogEntry entry,
        RecorderGameFingerprint runtime)
    {
        var hasConstraint = false;

        if (!string.IsNullOrWhiteSpace(entry.Channel))
        {
            hasConstraint = true;
            if (!Matches(entry.Channel, runtime.Channel))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.Version))
        {
            hasConstraint = true;
            if (!Matches(entry.Version, runtime.Version))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.Build))
        {
            hasConstraint = true;
            if (!Matches(entry.Build, runtime.Build))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.Sts2DllHash))
        {
            hasConstraint = true;
            if (!Matches(entry.Sts2DllHash, runtime.Sts2DllHash))
                return false;
        }

        return hasConstraint;
    }

    private static int GetSpecificity(RecorderCompatCatalogEntry entry)
    {
        var specificity = 0;
        if (!string.IsNullOrWhiteSpace(entry.Channel)) specificity++;
        if (!string.IsNullOrWhiteSpace(entry.Version)) specificity++;
        if (!string.IsNullOrWhiteSpace(entry.Build)) specificity++;
        if (!string.IsNullOrWhiteSpace(entry.Sts2DllHash)) specificity++;
        return specificity;
    }

    private static bool Matches(string? left, string? right)
    {
        return string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "unknown";
    }

    private static string? FirstNonBlank(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first))
            return first;
        return !string.IsNullOrWhiteSpace(second) ? second : null;
    }

    private static string FormatFingerprint(
        RecorderGameFingerprint runtime,
        RecorderCompatCatalogEntry? matchedEntry)
    {
        var version = FirstNonBlank(runtime.Version, matchedEntry?.Version);
        var build = FirstNonBlank(runtime.Build, matchedEntry?.Build);
        var hash = FirstNonBlank(runtime.Sts2DllHash, matchedEntry?.Sts2DllHash);

        return string.Join(
            ", ",
            new[]
            {
                version != null ? $"version={version}" : null,
                build != null ? $"build={build}" : null,
                hash != null ? $"dll_hash={hash}" : null,
            }.Where(value => value != null));
    }
}

internal static class RecorderRuntimeEnvironment
{
    private static readonly object Sync = new();
    private static RecorderCompatibilityAssessment? _cachedAssessment;
    private static readonly JsonSerializerOptions ReleaseInfoJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static RecorderCompatibilityAssessment GetCurrentAssessment(
        bool refresh = false,
        bool logOutcome = false)
    {
        lock (Sync)
        {
            if (refresh || _cachedAssessment == null)
            {
                _cachedAssessment = BuildAssessment();
            }

            if (logOutcome)
            {
                LogAssessment(_cachedAssessment);
            }

            return _cachedAssessment;
        }
    }

    private static RecorderCompatibilityAssessment BuildAssessment()
    {
        var runtime = InspectRuntimeFingerprint();
        var catalog = RecorderCompatCatalog.LoadDefault();
        return RecorderCompatResolver.Resolve(runtime, catalog);
    }

    private static RecorderGameFingerprint InspectRuntimeFingerprint()
    {
        try
        {
            var assembly = typeof(CombatManager).Assembly;
            var assemblyPath = string.IsNullOrWhiteSpace(assembly.Location)
                ? null
                : assembly.Location;

            var informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            var fileVersionInfo = !string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath)
                ? FileVersionInfo.GetVersionInfo(assemblyPath)
                : null;
            var releaseInfo = LoadReleaseInfo(assemblyPath);

            var version = NormalizeVersion(
                releaseInfo?.Version ??
                ExtractVersionCore(informationalVersion) ??
                fileVersionInfo?.ProductVersion ??
                fileVersionInfo?.FileVersion ??
                assembly.GetName().Version?.ToString());
            var build =
                ExtractBuild(informationalVersion) ??
                ExtractBuild(fileVersionInfo?.ProductVersion) ??
                NormalizeNonBlank(releaseInfo?.Commit);
            var hash = ComputeSha256(assemblyPath);

            return new RecorderGameFingerprint(
                Channel: "unknown",
                Version: version,
                Build: build,
                Sts2DllHash: hash,
                AssemblyPath: assemblyPath);
        }
        catch (Exception ex)
        {
            DebugFileLogger.Error(nameof(RecorderRuntimeEnvironment) + ".InspectRuntimeFingerprint", ex);
            return new RecorderGameFingerprint("unknown", null, null, null, null);
        }
    }

    internal static RecorderReleaseInfo? LoadReleaseInfo(
        string? assemblyPath,
        string? processPath = null)
    {
        foreach (var candidatePath in GetPossibleReleaseInfoPaths(assemblyPath, processPath))
        {
            if (!File.Exists(candidatePath))
                continue;

            try
            {
                var json = File.ReadAllText(candidatePath);
                var releaseInfo = JsonSerializer.Deserialize<RecorderReleaseInfoFile>(
                    json,
                    ReleaseInfoJsonOptions);
                if (releaseInfo == null)
                    continue;

                return new RecorderReleaseInfo(
                    Version: NormalizeReleaseVersion(releaseInfo.Version),
                    Commit: NormalizeNonBlank(releaseInfo.Commit),
                    Branch: NormalizeNonBlank(releaseInfo.Branch),
                    SourcePath: candidatePath);
            }
            catch (JsonException ex)
            {
                DebugFileLogger.Error(nameof(RecorderRuntimeEnvironment) + ".LoadReleaseInfo", ex);
            }
            catch (Exception ex)
            {
                DebugFileLogger.Error(nameof(RecorderRuntimeEnvironment) + ".LoadReleaseInfo", ex);
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> GetPossibleReleaseInfoPaths(
        string? assemblyPath,
        string? processPath = null)
    {
        var candidates = new List<string>();

        var effectiveProcessPath = string.IsNullOrWhiteSpace(processPath)
            ? Environment.ProcessPath
            : processPath;
        if (!string.IsNullOrWhiteSpace(effectiveProcessPath))
        {
            var executableDirectory = Path.GetDirectoryName(effectiveProcessPath);
            if (!string.IsNullOrWhiteSpace(executableDirectory))
            {
                if (OperatingSystem.IsMacOS())
                {
                    candidates.Add(Path.GetFullPath(
                        Path.Combine(executableDirectory, "..", "Resources", "release_info.json")));
                    candidates.Add(Path.GetFullPath(
                        Path.Combine(executableDirectory, "release_info.json")));
                }
                else
                {
                    candidates.Add(Path.GetFullPath(
                        Path.Combine(executableDirectory, "release_info.json")));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(assemblyPath))
        {
            var assemblyDirectory = Path.GetDirectoryName(assemblyPath);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                candidates.Add(Path.GetFullPath(
                    Path.Combine(assemblyDirectory, "..", "release_info.json")));
                candidates.Add(Path.GetFullPath(
                    Path.Combine(assemblyDirectory, "release_info.json")));
            }
        }

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ComputeSha256(string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            return null;

        using var stream = File.OpenRead(assemblyPath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ExtractVersionCore(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var candidate = value.Trim();
        var plusIndex = candidate.IndexOf('+');
        return plusIndex >= 0 ? candidate[..plusIndex] : candidate;
    }

    private static string? ExtractBuild(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var candidate = value.Trim();
        var plusIndex = candidate.IndexOf('+');
        if (plusIndex < 0 || plusIndex == candidate.Length - 1)
            return null;

        var build = candidate[(plusIndex + 1)..].Trim();
        return string.IsNullOrWhiteSpace(build) ? null : build;
    }

    private static string? NormalizeReleaseVersion(string? value)
    {
        var normalized = NormalizeNonBlank(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase) && normalized.Length > 1)
        {
            normalized = normalized[1..];
        }

        return NormalizeVersion(normalized);
    }

    private static string? NormalizeNonBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var candidate = value.Trim();
        if (!Version.TryParse(candidate, out var parsed))
            return candidate;

        if (parsed.Revision > 0)
            return parsed.ToString(4);

        if (parsed.Build >= 0)
            return parsed.ToString(3);

        return parsed.ToString(2);
    }

    private static void LogAssessment(RecorderCompatibilityAssessment assessment)
    {
        var fingerprint = string.Join(
            ", ",
            new[]
            {
                !string.IsNullOrWhiteSpace(assessment.Game.Version)
                    ? $"version={assessment.Game.Version}"
                    : null,
                !string.IsNullOrWhiteSpace(assessment.Game.Build)
                    ? $"build={assessment.Game.Build}"
                    : null,
                !string.IsNullOrWhiteSpace(assessment.Game.Sts2DllHash)
                    ? $"dll_hash={assessment.Game.Sts2DllHash}"
                    : null,
            }.Where(value => value != null));

        Log.Info(
            $"[STS2CombatRecorder] Runtime compat check: status={assessment.Status.ToWireValue()}, {fingerprint}");

        for (var i = 0; i < assessment.WarningMessages.Count; i++)
        {
            Log.Info($"[STS2CombatRecorder] Warning[{assessment.WarningCodes[i]}]: {assessment.WarningMessages[i]}");
            DebugFileLogger.Log(
                nameof(RecorderRuntimeEnvironment) + ".LogAssessment",
                $"{assessment.WarningCodes[i]}: {assessment.WarningMessages[i]}");
        }
    }
}
