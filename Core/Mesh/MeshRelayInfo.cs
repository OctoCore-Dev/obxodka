namespace obxodka.Core.Mesh;

public sealed record MeshRelayInfo(
    string IpAddress,
    int Port,
    string RelayId,
    int LoadPercent,
    int PingMs,
    string CountryCode,
    string CountryFlag,
    bool IsFriend
);
