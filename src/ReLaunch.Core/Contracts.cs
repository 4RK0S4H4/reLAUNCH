using ReLaunch.Core.Catalogue;

namespace ReLaunch.Core.Contracts;

/// <summary>Shapes shared by the service, the desktop app (named pipe) and the relay protocol.</summary>
public sealed record AppDto(string Id, string Name, bool Favorite, bool Trackable, string? IconHash, AppStatus Status, string Phase);

public sealed record ActionResultDto(bool Ok, string Phase, string? ErrorCode, string? Message);

public sealed record ActivityDto(DateTimeOffset Time, string DeviceId, string AppId, string AppName, string Action, string Result);

public sealed record PairingInfo(string Code, string Url, long ExpiresAt);

public sealed record DeviceInfo(string Id, string Name, long CreatedAt, long? LastSeen);

/// <summary>Everything the desktop app shows about the service at a glance.</summary>
public sealed record ServiceState(
    string Version,
    string PcName,
    string Session,
    bool AgentReady,
    int AppCount,
    string RelayState,
    string RelayUrl,
    bool HasKey,
    DateTimeOffset? LastHeartbeat,
    string? LastError,
    bool DebugApi,
    string DataFolder);
