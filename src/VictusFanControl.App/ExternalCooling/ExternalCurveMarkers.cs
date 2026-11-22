using VictusFanControl.ExternalCooling;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed record ExternalCurveMarker(double Input,double Output,bool IsApplied);

internal static class ExternalCurveMarkers
{
    internal static IReadOnlyList<ExternalCurveMarker> Build(ExternalCurveConfiguration draft,
        ExternalCurveConfiguration saved,ExternalCoolingDiagnostics? diagnostics,DateTimeOffset now,ProductExternalOutputMode? expectedSource=null)
    {
        // Match the source to the exact acknowledged frame. LastSent could be a
        // later request; combining it with an older applied duty invents a point.
        if(diagnostics is not {State:ExternalCoolingConnectionState.Connected,
            Device:{} device,AcknowledgedTelemetry:{} telemetry,LastValidReceivedUtc:{} received})return [];
        bool Fresh(DateTimeOffset? at,int maximum) => at is{} time && time<=now &&
            (now-time).TotalMilliseconds<maximum;
        if(!Fresh(received,2000)||!Fresh(telemetry.CapturedAtUtc,2000))return [];
        double? input=null;var independent=telemetry.IndependentExternal;
        if(expectedSource==ProductExternalOutputMode.Fixed||independent is{Mode:"Fixed"})return [];
        if(expectedSource==ProductExternalOutputMode.TemperatureCurve&&independent is not{Mode:"Curve"}||
            expectedSource==ProductExternalOutputMode.FollowInternalFans&&independent is not null)return [];
        if(independent is not null)
        {
            if(independent.Mode!="Curve"||independent.Duty.HasValue||
                !Fresh(independent.SampledAtUtc,ExternalCoolingProtocol.MaximumSourceAgeMilliseconds))return [];
            input=ExportTelemetry.Range(independent.DemandPercent,0,100);
        }
        else if(draft.Input==0 && telemetry.ModoSolicitado=="Automatic" &&
            Fresh(telemetry.DecisionAtUtc,ExternalCoolingProtocol.MaximumSourceAgeMilliseconds))
            input=ExportTelemetry.Range(telemetry.DemandaPct,0,100);
        else if(draft.Input==1 && telemetry.SolicitudValida && telemetry.Solicitud is{} request &&
                Fresh(telemetry.SampledAtUtc,ExternalCoolingProtocol.MaximumSourceAgeMilliseconds) &&
                request>=draft.RequestMinimum && request<=draft.RequestMaximum)
            input=100.0*(request-draft.RequestMinimum)/(draft.RequestMaximum-draft.RequestMinimum);
        if(input is not{} x)return [];
        var markers=new List<ExternalCurveMarker>{new(x,draft.Evaluate(x),false)};
        // Applied PWM may be off the curve because of EMA, ramps or the
        // configured startup pulse. Never present Local or an old revision as Remote.
        if(device.Mode=="Remote" && device.AppliedConfigRevision==saved.Revision &&
            telemetry.ExternalConfiguration?.Revision==saved.Revision &&
            (independent is not null?device.IndependentControlVersion==1:
                saved.Input==draft.Input && saved.RequestMinimum==draft.RequestMinimum && saved.RequestMaximum==draft.RequestMaximum) &&
            device.AppliedDuty is >=0 and <=255)
            markers.Add(new(x,device.AppliedDuty.Value,true));
        return markers;
    }
}
