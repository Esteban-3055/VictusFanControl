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

        var network =
            new SecurityIdentifier(
                WellKnownSidType.NetworkSid,
                domainSid: null);

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.SetOwner(system);

        // Named pipes are network-capable by design. Deny the well-known
        // NETWORK SID explicitly so even a remote administrator cannot use
        // this local privileged control endpoint.
        security.AddAccessRule(
            new PipeAccessRule(
                network,
                PipeAccessRights.FullControl,
                AccessControlType.Deny));

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
