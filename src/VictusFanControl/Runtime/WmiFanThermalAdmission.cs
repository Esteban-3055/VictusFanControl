using VictusFanControl.Hardware.Windows;
using VictusFanControl.Safety;

namespace VictusFanControl.Runtime;

// Experiment facade: the final Automatic adapter uses this same admission core.
internal sealed class WmiFanThermalAdmission : Hp8C40AutomaticThermalAdmission
{
    internal WmiFanThermalAdmission(HardwareIdentity hardware, Func<long>? milliseconds = null)
        : base(hardware, milliseconds) { }
    internal new static Hp8C40AutomaticThermalAdmissionSettings Settings => Hp8C40AutomaticThermalAdmission.Settings;
}
