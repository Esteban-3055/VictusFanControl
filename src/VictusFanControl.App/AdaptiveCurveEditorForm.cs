using VictusFanControl.Control.Adaptive;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed class AdaptiveCurveEditorForm : Form
{
    private readonly AdaptiveCurveProfileStore _store;
    private CpuDemandTemperatureSource _cpuSource;
    private readonly ComboBox _profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox _axes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 235 };
    private readonly AdaptiveCurveChart _chart = new();
    private readonly NumericUpDown _input = new() { Width = 80, Maximum = 200 };
    private readonly NumericUpDown _level = new() { Width = 65, Minimum = 10, Maximum = 50 };
    private readonly ListBox _points = new() { Dock = DockStyle.Fill, Height = 85 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(1050, 0) };
    private readonly System.Windows.Forms.Timer _freshnessTimer = new() { Interval = 500 };
    private readonly Label _demands = new() { AutoSize = true, MaximumSize = new Size(1050, 0) };
    private readonly Stack<AdaptiveCurveProfile> _undo = new();
    private readonly Stack<AdaptiveCurveProfile> _redo = new();
    private readonly Queue<double> _cpuHistory = new();
    private readonly Action<AdaptiveCurveProfile> _applyPreview;
    private AdaptiveCurveProfile _draft = AdaptiveCurveProfiles.Presets()[1];
    private AdaptiveCurveProfile _applied = AdaptiveCurveProfiles.Presets()[1];
    private string _baseline = "";
    private string _loadNotice = "";
    private bool _refreshing;
    private DateTimeOffset? _lastSample;
    private TelemetrySnapshot? _snapshot;
    private readonly record struct AxisChoice(AdaptiveCurveAxis Axis, string Text)
    { public override string ToString() => Text; }
    private AdaptiveCurveAxis Axis => ((AxisChoice)_axes.SelectedItem!).Axis;
    private bool Dirty => AdaptiveCurveProfiles.Serialize(_draft) != _baseline;

    public AdaptiveCurveEditorForm(Action<AdaptiveCurveProfile> applyPreview, AdaptiveCurveProfile applied, string? profileDirectory = null, CpuDemandTemperatureSource cpuSource = CpuDemandTemperatureSource.PackageOrHottestCore)
    {
        _cpuSource = cpuSource;
        _store = new(profileDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusFanControl", "profiles"));
        _freshnessTimer.Tick += (_,_) => { if (_snapshot is not null && DateTimeOffset.UtcNow - _snapshot.Timestamp >= TimeSpan.FromSeconds(3)) ClearTelemetry(); };
        _freshnessTimer.Start();
        _applyPreview = applyPreview;
        _applied = AdaptiveCurveProfiles.Copy(applied);
        Text = "Curvas de ventiladores — previsualización";
        Size = new Size(1080, 800); MinimumSize = new Size(850, 760);
        StartPosition = FormStartPosition.CenterParent; KeyPreview = true;
        Resize += (_,_) => { _status.MaximumSize = new Size(Math.Max(100,ClientSize.Width-40),0); _demands.MaximumSize = _status.MaximumSize; };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 7 };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Absolute, 85)); root.RowStyles.Add(new(SizeType.AutoSize));
        var profiles = Flow(); profiles.Controls.Add(new Label { Text = "Perfil:", AutoSize = true }); profiles.Controls.Add(_profiles);
        AddButton(profiles, "Nuevo", NewProfile); AddButton(profiles, "Duplicar", Duplicate);
        AddButton(profiles, "Renombrar", Rename); AddButton(profiles, "Eliminar", Delete);
        var actions = Flow(); AddButton(actions, "Restablecer", ResetDraft); AddButton(actions, "Guardar", Save);
        AddButton(actions, "Guardar como…", SaveAs); AddButton(actions, "Aplicar a vista previa", ApplyPreview);
        AddButton(actions, "Deshacer", Undo); AddButton(actions, "Rehacer", Redo);
        var axes = Flow(); axes.Controls.Add(_axes);
        axes.Controls.Add(new Label { AutoSize = true, Text = "Arrastra puntos. Verde: entrada actual · Violeta: mediana CPU visual (5 muestras)." });
        _axes.Items.AddRange(new object[] {
            new AxisChoice(AdaptiveCurveAxis.CpuTemperature,"Temperatura CPU (°C)"), new AxisChoice(AdaptiveCurveAxis.GpuTemperature,"Temperatura GPU (°C)"),
            new AxisChoice(AdaptiveCurveAxis.CpuPower,"Potencia CPU (W)"), new AxisChoice(AdaptiveCurveAxis.GpuPower,"Potencia GPU (W)"),
            new AxisChoice(AdaptiveCurveAxis.CpuLoad,"Carga CPU (%)"), new AxisChoice(AdaptiveCurveAxis.GpuLoad,"Carga GPU (%)") });
        _axes.SelectedIndex = 0;
        var edit = Flow(); edit.Controls.Add(new Label { AutoSize = true, Text = "Entrada:" }); edit.Controls.Add(_input);
        edit.Controls.Add(new Label { AutoSize = true, Text = "Nivel:" }); edit.Controls.Add(_level);
        AddButton(edit, "Actualizar punto", UpdatePoint); AddButton(edit, "Añadir punto", AddPoint); AddButton(edit, "Quitar punto", RemovePoint);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        footer.Controls.Add(_status); footer.Controls.Add(_demands);
        root.Controls.Add(profiles,0,0); root.Controls.Add(actions,0,1); root.Controls.Add(axes,0,2); root.Controls.Add(_chart,0,3);
        root.Controls.Add(edit,0,4); root.Controls.Add(_points,0,5); root.Controls.Add(footer,0,6); Controls.Add(root);
        _profiles.SelectedIndexChanged += (_,_) => SelectProfile();
        _axes.SelectedIndexChanged += (_,_) => RefreshDraft();
        _points.SelectedIndexChanged += (_,_) => { if (!_refreshing) SelectPoint(_points.SelectedIndex); };
        _chart.PointSelected += SelectPoint;
        _chart.DraftChanged += p => Execute(() => Change(AdaptiveCurveProfiles.WithCurve(_draft, Axis, p)));
        KeyDown += (_,e) => { if(e.Control && e.KeyCode==Keys.Z){Execute(Undo);e.Handled=true;} else if(e.Control && e.KeyCode==Keys.Y){Execute(Redo);e.Handled=true;} else if(e.Control && e.KeyCode==Keys.S){Execute(Save);e.Handled=true;} };
        FormClosing += (_,e) => { if(e.CloseReason == CloseReason.UserClosing && !AllowDiscard()) e.Cancel=true; };
        ReloadProfiles(_applied.Id);
        DashboardTheme.Apply(this);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _freshnessTimer.Dispose();
        base.Dispose(disposing);
    }
    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
    private void AddButton(FlowLayoutPanel panel, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_,_) => Execute(action); panel.Controls.Add(button);
    }
    private void Execute(Action action)
    {
        try { action(); }
        catch(Exception e) { MessageBox.Show(this,e.Message,"No se aplicó el cambio",MessageBoxButtons.OK,MessageBoxIcon.Warning); RefreshDraft(); }
    }
    private bool AllowDiscard() => !Dirty || MessageBox.Show(this,"Hay cambios sin guardar. ¿Descartarlos?","Borrador",
        MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes;
    private void ReloadProfiles(string selected)
    {
        _refreshing=true;
        try
        {
            _profiles.Items.Clear(); foreach(var p in AdaptiveCurveProfiles.Presets()) _profiles.Items.Add(p);
            var custom = _store.LoadCustom(out var rejected);
            foreach(var p in custom) _profiles.Items.Add(p);
            _loadNotice=rejected>0 ? $" Se omitieron {rejected} archivos inválidos." : "";
            _profiles.SelectedIndex=Enumerable.Range(0,_profiles.Items.Count).FirstOrDefault(i=>(_profiles.Items[i] as AdaptiveCurveProfile)?.Id==selected,1);
            LoadDraft((AdaptiveCurveProfile)_profiles.SelectedItem!);
        }
        finally { _refreshing=false; }
        RefreshDraft();
    }
    private void SelectProfile()
    {
        if(_refreshing || _profiles.SelectedItem is not AdaptiveCurveProfile p) return;
        if(!AllowDiscard()) { _refreshing=true; _profiles.SelectedIndex=Enumerable.Range(0,_profiles.Items.Count).FirstOrDefault(i=>(_profiles.Items[i] as AdaptiveCurveProfile)?.Id==_draft.Id,1); _refreshing=false; return; }
        LoadDraft(p); RefreshDraft();
    }
    private void LoadDraft(AdaptiveCurveProfile p)
    {
        _draft=AdaptiveCurveProfiles.Copy(p);_baseline=AdaptiveCurveProfiles.Serialize(_draft);_undo.Clear();_redo.Clear();_chart.SelectedIndex=0;
    }
    private void Change(AdaptiveCurveProfile p)
    {
        _=AdaptiveCurveProfiles.Validate(p);
        if(AdaptiveCurveProfiles.Serialize(p)==AdaptiveCurveProfiles.Serialize(_draft))return;
        _undo.Push(AdaptiveCurveProfiles.Copy(_draft));_redo.Clear();_draft=p;RefreshDraft();
    }
    private void Undo() { if(_undo.Count==0)return;_redo.Push(_draft);_draft=_undo.Pop();RefreshDraft(); }
    private void Redo() { if(_redo.Count==0)return;_undo.Push(_draft);_draft=_redo.Pop();RefreshDraft(); }
    private void RefreshDraft()
    {
        _refreshing=true;
        try
        {
            var curve=AdaptiveCurveProfiles.Curve(AdaptiveCurveProfiles.Validate(_draft),Axis);
            _chart.Points=curve;_chart.MaximumInput=AdaptiveCurveProfiles.MaximumInput(Axis);_chart.AxisLabel=_axes.SelectedItem!.ToString()!;
            _input.Maximum=(decimal)_chart.MaximumInput;
            _points.Items.Clear();foreach(var p in curve)_points.Items.Add($"{p.Input:0} → {p.Level:0}");
            SelectPoint(Math.Clamp(_chart.SelectedIndex,0,curve.Count-1));
            _status.Text=$"Borrador: {_draft.Name}{(Dirty ? " · sin guardar" : "")} | Vista previa activa: {_applied.Name} | Automático deshabilitado. Nivel 10–50, no RPM.{_loadNotice}";
            RefreshMarkers();_chart.Invalidate();
        }
        finally { _refreshing=false; }
    }
    private void SelectPoint(int i)
    {
        if(i<0 || i>=_chart.Points.Count)return;
        var previous=_refreshing;_refreshing=true;
        _chart.SelectedIndex=i;_points.SelectedIndex=i;_input.Value=(decimal)_chart.Points[i].Input;_level.Value=(decimal)_chart.Points[i].Level;
        _refreshing=previous;_chart.Invalidate();
    }
    private void UpdatePoint()
    {
        var points=_chart.Points.ToArray();if(_chart.SelectedIndex<0)return;
        points[_chart.SelectedIndex]=new((double)_input.Value,(double)_level.Value);
        Change(AdaptiveCurveProfiles.WithCurve(_draft,Axis,points));
    }
    private void AddPoint()
    {
        var points=_chart.Points.ToList(); if(points.Count>=64)throw new InvalidOperationException("Máximo 64 puntos.");
        // Insert in the widest free interval; endpoints remain fixed unless explicitly edited.
        var gap=Enumerable.Range(0,points.Count-1).OrderByDescending(i=>points[i+1].Input-points[i].Input).First();
        var x=Math.Floor((points[gap].Input+points[gap+1].Input)/2);
        if(x<=points[gap].Input)throw new InvalidOperationException("No queda un intervalo entero libre.");
        var y=Math.Round(AdaptiveCurveProfiles.Interpolate(points,x));points.Insert(gap+1,new(x,y));
        _chart.SelectedIndex=gap+1;Change(AdaptiveCurveProfiles.WithCurve(_draft,Axis,points));
    }
    private void RemovePoint()
    {
        if(_chart.Points.Count<=2)throw new InvalidOperationException("Se requieren al menos dos puntos.");
        var points=_chart.Points.ToList();points.RemoveAt(_chart.SelectedIndex);Change(AdaptiveCurveProfiles.WithCurve(_draft,Axis,points));
    }
    private string? AskName(string title, string initial)
    {
        using var dialog=new Form {Text=title,ClientSize=new Size(390,115),FormBorderStyle=FormBorderStyle.FixedDialog,StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false};
        var text=new TextBox {Text=initial,Left=15,Top=15,Width=360,MaxLength=60};
        var ok=new Button {Text="Aceptar",DialogResult=DialogResult.OK,Left=200,Top=60};
        var cancel=new Button {Text="Cancelar",DialogResult=DialogResult.Cancel,Left=285,Top=60};
        dialog.Controls.AddRange([text,ok,cancel]);dialog.AcceptButton=ok;dialog.CancelButton=cancel;
        return dialog.ShowDialog(this)==DialogResult.OK ? text.Text.Trim() : null;
    }
    private void NewProfile() { if(!AllowDiscard())return;CreateCustom(AdaptiveCurveProfiles.Presets()[1],"Nuevo perfil"); }
    private void Duplicate() => CreateCustom(_draft,_draft.Name+" copia");
    private void CreateCustom(AdaptiveCurveProfile source,string suggested)
    {
        var name=AskName("Nombre del perfil",suggested);if(name is null)return;
        var p=AdaptiveCurveProfiles.Copy(source) with {Id=Guid.NewGuid().ToString("N"),Name=name};
        _store.Save(p);ReloadProfiles(p.Id);
    }
    private void Rename()
    {
        if(_draft.IsBuiltIn)throw new InvalidOperationException("Duplica el preset para renombrarlo.");
        var name=AskName("Renombrar",_draft.Name);if(name is null)return;Change(_draft with {Name=name});
    }
    private void Delete()
    {
        if(_draft.IsBuiltIn)throw new InvalidOperationException("Los presets no se eliminan.");
        if(MessageBox.Show(this,"¿Eliminar este perfil guardado? La vista previa activa se conservará hasta Aplicar otro.","Eliminar",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
        _store.Delete(_draft.Id);ReloadProfiles("equilibrado");
    }
    private void ResetDraft()
    {
        var source=AdaptiveCurveProfiles.Presets().FirstOrDefault(p=>p.Id==_draft.Id) ?? AdaptiveCurveProfiles.Presets()[1];
        Change(AdaptiveCurveProfiles.Create(_draft.Id,_draft.Name,AdaptiveCurveProfiles.Validate(source)));
    }
    private void Save()
    {
        if(_draft.IsBuiltIn){SaveAs();return;}
        _store.Save(_draft);ReloadProfiles(_draft.Id);
    }
    private void SaveAs()=>CreateCustom(_draft,_draft.Name+" personalizado");
    private void ApplyPreview()
    {
        var candidate=AdaptiveCurveProfiles.Copy(_draft);_=AdaptiveCurveProfiles.Validate(candidate);
        _applyPreview(candidate);_applied=candidate;ClearTelemetry();RefreshDraft();
    }
    internal void SetCpuTemperatureSource(CpuDemandTemperatureSource source)
    {
        if (_cpuSource == source) return;
        _cpuSource = source;
        ClearTelemetry();
    }
    public void UpdateTelemetry(SystemState state,TelemetrySnapshot snapshot,AdaptiveFanPolicyShadowEvaluation? result)
    {
        var now=DateTimeOffset.UtcNow;var age=now-snapshot.Timestamp;
        if(state!=SystemState.Healthy || age<TimeSpan.Zero || age>=TimeSpan.FromSeconds(3) || result?.SafetyPreconditionsReady!=true || !result.PolicyAccepted)
        { ClearTelemetry();return; }
        if(_lastSample.HasValue && (snapshot.Timestamp<_lastSample || snapshot.Timestamp-_lastSample>TimeSpan.FromSeconds(3)))_cpuHistory.Clear();
        if(snapshot.Timestamp!=_lastSample && CpuDemandTemperature.Select(snapshot,_cpuSource) is double cpu && double.IsFinite(cpu))
        { _cpuHistory.Enqueue(cpu);while(_cpuHistory.Count>5)_cpuHistory.Dequeue(); }
        _lastSample=snapshot.Timestamp;_snapshot=snapshot;RefreshMarkers();
        var c=AdaptiveCurveProfiles.Validate(_applied);var axes=Enum.GetValues<AdaptiveCurveAxis>();
        var demands=axes.Select(a=>(Axis:a,Value:Value(snapshot,a))).ToArray();
        if(demands.Any(d=>!d.Value.HasValue)){_demands.Text="Esperando entradas completas.";return;}
        var values=demands.Select(d=>(d.Axis,Level:AdaptiveCurveProfiles.Interpolate(AdaptiveCurveProfiles.Curve(c,d.Axis),d.Value!.Value))).ToArray();
        var dominant=values.OrderByDescending(d=>d.Level).First();
        _demands.Text=string.Join(" · ",values.Select(d=>$"{d.Axis}: {d.Level:0.0}"))+Environment.NewLine+
            $"Dominante: {dominant.Axis} | Demanda MAX: {result!.RawDemandLevel:0.0} | Nivel suavizado: {result.RecommendedEqualLevel?.ToString() ?? "—"}";
    }
    public void ClearTelemetry(){_snapshot=null;_lastSample=null;_cpuHistory.Clear();_chart.LiveInput=null;_chart.MedianInput=null;_demands.Text="Esperando telemetría fresca.";_chart.Invalidate();}
    private void RefreshMarkers()
    {
        _chart.LiveInput=_snapshot is null ? null : Value(_snapshot,Axis);
        _chart.MedianInput=Axis==AdaptiveCurveAxis.CpuTemperature && _cpuHistory.Count==5 ? _cpuHistory.Order().ElementAt(2) : null;
        _chart.Invalidate();
    }
    private double? Value(TelemetrySnapshot s,AdaptiveCurveAxis a)=>a switch
    {
        AdaptiveCurveAxis.CpuTemperature=>CpuDemandTemperature.Select(s,_cpuSource),AdaptiveCurveAxis.GpuTemperature=>s.GpuTemperatureC,
        AdaptiveCurveAxis.CpuPower=>s.CpuPackagePowerW,AdaptiveCurveAxis.GpuPower=>s.GpuPowerW,
        AdaptiveCurveAxis.CpuLoad=>s.CpuLoadPercent,AdaptiveCurveAxis.GpuLoad=>s.GpuLoadPercent,_=>null
    };
}
