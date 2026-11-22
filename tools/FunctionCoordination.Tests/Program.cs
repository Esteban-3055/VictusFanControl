using VictusFanControl.Product;

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static bool Ready(IReadOnlyList<ProductFunctionPlan> plan, ProductFunction function) => plan.Single(p => p.Function == function).MayRequestActivation;
static ProductFunctionFlags Flags(int mask) => new((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0);

var legacy = new ProductFunctionPolicies { Ctgp = new(ProductFunctionBinding.FollowAutomatic, true),
    ExternalCooling = new(ProductFunctionBinding.FollowInternalFans, true) };
var all = Flags(15);
var empty = new ProductFunctionFlags();
var ac = new ProductFunctionContext(ProductFunctionFanMode.Automatic, ProductFunctionPower.Ac, all, true);
Require(ProductFunctionCoordination.ApplicationStart(legacy) == empty, "Legacy selection silently enabled controls at application startup.");
var automatic = ProductFunctionCoordination.EnterFanMode(legacy, empty, ProductFunctionFanMode.Firmware, ProductFunctionFanMode.Automatic, ProductFunctionEntryReason.User);
Require(automatic == all, "Legacy Automatic did not select its linked functions.");
var stopped = ProductFunctionCoordination.EnterFanMode(legacy, automatic, ProductFunctionFanMode.Automatic, ProductFunctionFanMode.Firmware, ProductFunctionEntryReason.User);
var firmwarePlan = ProductFunctionCoordination.Plan(legacy, stopped, ac with { FanMode = ProductFunctionFanMode.Firmware });
Require(Ready(firmwarePlan, ProductFunction.CpuLimits) && Ready(firmwarePlan, ProductFunction.GpuClocks) &&
    !Ready(firmwarePlan, ProductFunction.Ctgp) && !Ready(firmwarePlan, ProductFunction.ExternalCooling), "Legacy stop contract was replaced by a global off.");
var manual = ProductFunctionCoordination.EnterFanMode(legacy, empty, ProductFunctionFanMode.Firmware, ProductFunctionFanMode.Manual, ProductFunctionEntryReason.User);
Require(manual == new ProductFunctionFlags(ExternalCooling: true), "Manual fan control activated CPU/GPU/cTGP or lost external following.");

var disabledCpuGpu = automatic with { CpuLimits = false, GpuClocks = false };
foreach (var reason in new[] { ProductFunctionEntryReason.AutomaticRecovery, ProductFunctionEntryReason.SystemResume })
    Require(ProductFunctionCoordination.EnterFanMode(legacy, disabledCpuGpu, ProductFunctionFanMode.Firmware, ProductFunctionFanMode.Automatic, reason) == disabledCpuGpu,
        "Recovery re-enabled user-disabled CPU/GPU functions.");
Require(ProductFunctionCoordination.EnterFanMode(legacy, disabledCpuGpu, ProductFunctionFanMode.Automatic, ProductFunctionFanMode.Automatic, ProductFunctionEntryReason.User) == disabledCpuGpu,
    "Pressing the existing Automatic mode reapplied linked selections.");
Require(ProductFunctionCoordination.EnterFanMode(legacy, disabledCpuGpu, ProductFunctionFanMode.Firmware, ProductFunctionFanMode.Automatic, ProductFunctionEntryReason.User) == all,
    "A fresh explicit Automatic activation lost selected legacy functions.");

var independent = legacy.Independent();
Require(ProductFunctionCoordination.ApplicationStart(independent) == empty, "Changing preset silently enabled startup activation.");
Require(ProductFunctionCoordination.EnterFanMode(independent, empty, ProductFunctionFanMode.Firmware, ProductFunctionFanMode.Automatic, ProductFunctionEntryReason.User) == all,
    "Independent control lost the selected convenience activation with Automatic.");
Require(ProductFunctionCoordination.EnterFanMode(independent.Unlinked(), empty, ProductFunctionFanMode.Firmware, ProductFunctionFanMode.Automatic, ProductFunctionEntryReason.User) == empty,
    "Explicit unlinking still activated functions with Automatic.");
Require(ProductFunctionCoordination.Plan(independent, all, ac with { FanMode = ProductFunctionFanMode.Firmware }).All(p => p.MayRequestActivation),
    "Independent functions still require Automatic.");
var battery = ProductFunctionCoordination.Plan(independent, all, ac with { Power = ProductFunctionPower.Battery });
Require(!Ready(battery, ProductFunction.Ctgp) && battery.Where(p => p.Function != ProductFunction.Ctgp).All(p => p.MayRequestActivation),
    "AC-only cTGP stopped unrelated functions or was activated on battery.");
var busy = ProductFunctionCoordination.Plan(independent, all, ac with { SharedWmiReady = false });
Require(!Ready(busy, ProductFunction.Ctgp) && busy.Where(p => p.Function != ProductFunction.Ctgp).All(p => p.MayRequestActivation),
    "Shared WMI failure disabled CPU/GPU or external control.");
var cpuFault = ProductFunctionCoordination.Plan(independent, all, ac with { OwnPrerequisitesReady = all with { CpuLimits = false } });
Require(!Ready(cpuFault, ProductFunction.CpuLimits) && cpuFault.Where(p => p.Function != ProductFunction.CpuLimits).All(p => p.MayRequestActivation),
    "A CPU readiness failure disabled another function.");
foreach (var context in new[] { ac with { Suspended = true }, ac with { Closing = true } })
    Require(ProductFunctionCoordination.Plan(independent, all, context).All(p => !p.MayRequestActivation), "Lifecycle closure admitted activation.");
var startup = independent with { Ctgp = independent.Ctgp with { StartWithApplication = true } };
Require(ProductFunctionCoordination.ApplicationStart(startup) == new ProductFunctionFlags(Ctgp: true), "Startup ignored per-function opt-in.");
for (var selection = 0; selection < 16; selection++)
{
    var selected = Flags(selection);
    var migrated = ProductFunctionPolicies.Legacy(selected);
    Require(ProductFunctionCoordination.ApplicationStart(migrated) == empty, "Legacy migration invented startup authority.");
    Require(ProductFunctionCoordination.EnterFanMode(migrated, all, ProductFunctionFanMode.Firmware,
        ProductFunctionFanMode.Automatic, ProductFunctionEntryReason.User) == selected, "Legacy migration changed existing linked selections.");
    Require(ProductFunctionCoordination.EnterFanMode(migrated.Independent(), all, ProductFunctionFanMode.Firmware,
        ProductFunctionFanMode.Automatic, ProductFunctionEntryReason.User) == all, "Independent convenience activation disabled an unselected function already requested by the user.");
}

long cases = 0;
foreach (var binding in Enum.GetValues<ProductFunctionBinding>())
foreach (var fan in Enum.GetValues<ProductFunctionFanMode>())
foreach (var power in Enum.GetValues<ProductFunctionPower>())
for (var requestedMask = 0; requestedMask < 16; requestedMask++)
for (var eligibleMask = 0; eligibleMask < 16; eligibleMask++)
foreach (var wmi in new[] { false, true })
{
    var policies = new ProductFunctionPolicies();
    foreach (var function in Enum.GetValues<ProductFunction>()) policies = policies.With(function, policies.Get(function) with { Binding = binding });
    var requested = Flags(requestedMask);
    var context = new ProductFunctionContext(fan, power, Flags(eligibleMask), wmi);
    var plan = ProductFunctionCoordination.Plan(policies, requested, context);
    foreach (var item in plan)
    {
        Require(!item.MayRequestActivation || requested.Get(item.Function) && context.OwnPrerequisitesReady.Get(item.Function), "Plan invented requested intent or controller readiness.");
        if (item.Function == ProductFunction.Ctgp && item.MayRequestActivation)
            Require(power == ProductFunctionPower.Ac && wmi, "cTGP escaped AC/shared-resource admission.");
    }
    Require(requested == Flags(requestedMask), "Planning mutated original intent.");
    cases++;
}
var rejected = false;
try { ProductFunctionCoordination.Plan(legacy with { Ctgp = new((ProductFunctionBinding)999, true) }, all, ac); }
catch (ArgumentOutOfRangeException) { rejected = true; }
Require(rejected, "Unknown persisted coordination policy was accepted.");
Console.WriteLine($"PASS function coordination policy: legacy stop semantics, Manual following, independent activation, AC-only cTGP, per-function failures, explicit startup, user disable through recovery and {cases} eligibility combinations. Pure linked source; no hardware/runtime/registry references. Runtime integration pending.");
