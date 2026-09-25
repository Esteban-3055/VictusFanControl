using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using VictusFanControl.Control;

namespace VictusFanControl.Watchdog;

internal static class GateDPipeFactory
{
    public static NamedPipeServerStream Create()
    {
        var system =
            new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                domainSid: null);

        var administrators =
            new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                domainSid: null);

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.SetOwner(system);

        security.AddAccessRule(
            new PipeAccessRule(
                system,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

        security.AddAccessRule(
            new PipeAccessRule(
                administrators,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            FanControlWatchdogLeaseContract.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous |
            PipeOptions.WriteThrough,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }
}
