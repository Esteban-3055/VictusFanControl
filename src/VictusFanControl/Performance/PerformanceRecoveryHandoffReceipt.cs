namespace VictusFanControl.Performance;

/// <summary>Proof of retired ownership, never proof that GPU Reset succeeded.</summary>
public sealed record PerformanceRecoveryHandoffReceipt(int SchemaVersion,string TargetProfileId,Guid SessionNonce,
    int OwnerPid,long OwnerStartUtcTicks,int GuardianPid,long GuardianStartUtcTicks,Guid GpuSession,
    bool SourceStopped,bool CpuDisabled,bool GpuRecoveryRequired)
{
    public bool Matches(string target,Guid nonce,int ownerPid,long ownerStart,int guardianPid,long guardianStart) =>
        SchemaVersion==1 && TargetProfileId==target && SessionNonce==nonce && nonce!=Guid.Empty &&
        OwnerPid==ownerPid && OwnerStartUtcTicks==ownerStart && GuardianPid==guardianPid && GuardianStartUtcTicks==guardianStart &&
        GpuSession!=Guid.Empty && SourceStopped && CpuDisabled && GpuRecoveryRequired;
}
