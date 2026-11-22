using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Runtime;

internal sealed record WmiFanDomainCommand(Guid Id,Guid FanSession,int OwnerPid,long OwnerStartUtcTicks);
internal sealed record WmiFanDomainReply(int SchemaVersion,Guid Id,Guid FanSession,int OwnerPid,long OwnerStartUtcTicks,
    int GuardianPid,long GuardianStartUtcTicks,string TargetProfileId,string SessionDirectory,bool FanReleased,
    bool ReleaseRequestAccepted,bool LegacyDefaultRequestAccepted,bool NativeComplete,CtgpStatus Ctgp,DateTimeOffset Utc);
