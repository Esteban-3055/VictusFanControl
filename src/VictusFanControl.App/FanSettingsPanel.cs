using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

/// <summary>Stages data; Apply delegates to the existing gated controller in Firmware mode.</summary>
internal sealed class FanSettingsPanel : UserControl
{
    private readonly Dictionary<string,NumericUpDown> _numbers = [];
    private readonly CheckBox _remember = new() { Text = "Conservar el pico térmico en el filtro normal", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(850,0) };
    private readonly Label _profileName = new() { AutoSize = true };
    private readonly AdaptiveCurveChart _chart = new() { MinimumSize = new Size(350,250) };
    private readonly ComboBox _cpuSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, AccessibleName = "Fuente de temperatura CPU" };
    private readonly ComboBox _axis = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly Func<FanConfiguration,Task> _apply;
    private FanConfiguration _applied;
    private AdaptiveCurveProfile _profile;
    private bool _busy;
    internal FanSettingsPanel(FanConfiguration configuration, Func<FanConfiguration,Task> apply, string? notice = null)
    {
        _apply=apply;_applied=FanConfigurationStore.Copy(configuration);_profile=AdaptiveCurveProfiles.Copy(configuration.Profile);
        Dock=DockStyle.Fill;AutoScroll=true;
        var root=new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, ColumnCount=2, Padding=new Padding(16) };
        root.ColumnStyles.Add(new(SizeType.Percent,50));root.ColumnStyles.Add(new(SizeType.Percent,50));
        var title=new Label { Text="AJUSTES DE VENTILACIÓN", AutoSize=true, Font=new Font("Segoe UI",16,FontStyle.Bold), Margin=new Padding(3,0,3,22) };
        root.Controls.Add(title,0,0);root.SetColumnSpan(title,2);
        var fields=new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, ColumnCount=2, Padding=new Padding(0,0,22,0) };
        fields.ColumnStyles.Add(new(SizeType.Percent,75));fields.ColumnStyles.Add(new(SizeType.Percent,25));
        _cpuSource.Items.AddRange(new object[] { "Package / núcleo más caliente", "CPU Average (núcleos físicos)" });
        fields.Controls.Add(new Label { Text = "Temperatura CPU para demanda", AutoSize = true },0,fields.RowCount++);
        fields.Controls.Add(_cpuSource,0,fields.RowCount++);fields.SetColumnSpan(_cpuSource,2);
        void Number(string key,string label,decimal minimum,decimal maximum,decimal increment=1,int decimals=0)
        {
            var n=new NumericUpDown { Minimum=minimum,Maximum=maximum,Increment=increment,DecimalPlaces=decimals,
                Width=85,AccessibleName=label,Margin=new Padding(6,5,6,5) };
            _numbers.Add(key,n);
            var row=fields.RowCount++;fields.Controls.Add(new Label {Text=label,AutoSize=true,Margin=new Padding(3,8,3,8)},0,row);fields.Controls.Add(n,1,row);
        }
        Number(nameof(AdaptiveFanTuning.MinimumLevel),"Nivel mínimo · 100 RPM nominales/nivel",10,50);
        Number(nameof(AdaptiveFanTuning.MaximumLevel),"Nivel máximo",10,50);
        Number(nameof(AdaptiveFanTuning.RiseTimeConstantSeconds),"Filtro de subida (s)",.5m,10,.5m,1);
        Number(nameof(AdaptiveFanTuning.FallTimeConstantSeconds),"Filtro de bajada (s)",1,60,.5m,1);
        Number(nameof(AdaptiveFanTuning.IncreaseConfirmationSeconds),"Confirmar subida durante (s)",0,5,.5m,1);
        Number(nameof(AdaptiveFanTuning.DecreaseConfirmationSeconds),"Confirmar bajada durante (s)",2,60,.5m,1);
        Number(nameof(AdaptiveFanTuning.NormalMaximumUpStepLevels),"Paso normal de subida (niveles)",1,4);
        Number(nameof(AdaptiveFanTuning.MaximumDownStepLevels),"Paso de bajada (niveles)",1,2);
        Number(nameof(AdaptiveFanTuning.CpuThermalOverrideC),"Respuesta térmica CPU desde (°C)",75,85);
        Number(nameof(AdaptiveFanTuning.GpuThermalOverrideC),"Respuesta térmica GPU desde (°C)",68,78);
        Number(nameof(AdaptiveFanTuning.NormalPollingDelayMilliseconds),"Pausa entre lecturas normales (ms)",500,1500,100);
        fields.Controls.Add(_remember,0,fields.RowCount++);fields.SetColumnSpan(_remember,2);
        root.Controls.Add(fields,0,1);
        var curves=new TableLayoutPanel { Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,Padding=new Padding(18,0,0,0) };
        curves.Controls.Add(new Label { Text="CURVAS CPU + GPU", AutoSize=true, Font=new Font("Segoe UI",12,FontStyle.Bold) });
        curves.Controls.Add(_profileName);curves.Controls.Add(_axis);curves.Controls.Add(_chart);
        _axis.Items.AddRange(new object[]{"Temperatura CPU","Temperatura GPU","Potencia CPU","Potencia GPU","Carga CPU","Carga GPU"});
        _axis.SelectedIndex=0;_axis.SelectedIndexChanged+=(_,_)=>RefreshCurve();
        var edit=new Button { Text="Editar las seis curvas…",AutoSize=true,Margin=new Padding(3,14,3,14) };
        edit.Click+=(_,_)=>
        {
            using var editor=new AdaptiveCurveEditorForm(p=>{_profile=AdaptiveCurveProfiles.Copy(p);RefreshCurve();
                _status.Text="Curvas en borrador. Aplica los ajustes para guardarlas en la configuración.";},_profile,cpuSource:(CpuDemandTemperatureSource)_cpuSource.SelectedIndex);
            editor.ShowDialog(FindForm());
        };
        curves.Controls.Add(edit);
        curves.Controls.Add(new Label { Text="El nivel final respeta el mínimo y máximo configurados.", AutoSize=true, MaximumSize=new Size(390,0) });
        curves.Controls.Add(new Label { Text="PROTECCIONES", AutoSize=true, Font=new Font("Segoe UI",12,FontStyle.Bold) });
        curves.Controls.Add(new Label { AutoSize=true,MaximumSize=new Size(390,0),Text=
            "CPU: confirmar desde 95 °C; entrega inmediata a 99 °C.\nGPU: entrega inmediata a 87 °C.\nMáximo: 5 muestras únicas o 2 segundos.\nSubida térmica: hasta 4 niveles por muestra.\nFrescura: hasta 3 segundos.\nDemanda CPU: fuente seleccionada.\nEmergencia CPU: Package / núcleo más caliente.\n\nLos ajustes no autorizan Automatic. El inicio siempre es Firmware.\nEl piso 26 es una propuesta; el experimento activo conserva 30–50." });
        root.Controls.Add(curves,1,1);
        var buttons=new FlowLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,Margin=new Padding(0,22,0,8) };
        var reset=new Button { Text="Restablecer",AutoSize=true };
        reset.Click+=(_,_)=>{_profile=FanConfiguration.QuietProfile();Set(new FanConfiguration().Tuning);_status.Text="Valores recomendados en borrador; aún no se guardaron.";};
        var cancel=new Button { Text="Descartar",AutoSize=true };
        cancel.Click+=(_,_)=>{_profile=AdaptiveCurveProfiles.Copy(_applied.Profile);Set(_applied.Tuning);_status.Text="Borrador descartado.";};
        var save=new Button { Text="Aplicar y guardar",AutoSize=true };
        save.Click+=async(_,_)=>await ApplyAsync();
        var export=new Button { Text="Exportar prueba 30–50…",AutoSize=true };
        export.Click+=(_,_)=>Export();
        buttons.Controls.AddRange(new System.Windows.Forms.Control[]{reset,cancel,save,export});
        var footer=new TableLayoutPanel { Dock=DockStyle.Bottom,AutoSize=true,ColumnCount=1,RowCount=2,Padding=new Padding(16,0,16,12) };
        footer.ColumnStyles.Add(new(SizeType.Percent,100));
        footer.Controls.Add(buttons,0,0);footer.Controls.Add(_status,0,1);
        var viewport=new Panel { Dock=DockStyle.Fill,AutoScroll=true };
        viewport.Controls.Add(root);
        Controls.Add(viewport);Controls.Add(footer);
        Set(configuration.Tuning);_status.Text=notice ?? "Edita en borrador. Aplicar requiere modo Firmware y no envía órdenes de ventilación.";
        DashboardTheme.Apply(this);
    }
    private void Set(AdaptiveFanTuning tuning)
    {
        foreach(var field in _numbers)field.Value.Value=Convert.ToDecimal(typeof(AdaptiveFanTuning).GetProperty(field.Key)!.GetValue(tuning));
        _cpuSource.SelectedIndex=(int)tuning.CpuTemperatureSource;
        _remember.Checked=tuning.RememberThermalDemand;RefreshCurve();
    }
    private FanConfiguration Draft()
    {
        var tuning=new AdaptiveFanTuning();
        foreach(var field in _numbers)
        {
            var property=typeof(AdaptiveFanTuning).GetProperty(field.Key)!;
            property.SetValue(tuning,property.PropertyType==typeof(int)? (object)decimal.ToInt32(field.Value.Value):(object)decimal.ToDouble(field.Value.Value));
        }
        tuning=tuning with {RememberThermalDemand=_remember.Checked,CpuTemperatureSource=(CpuDemandTemperatureSource)_cpuSource.SelectedIndex};
        var c=new FanConfiguration {Tuning=tuning,Profile=AdaptiveCurveProfiles.Copy(_profile)};
        _=c.BuildPolicy();return c;
    }
    private async Task ApplyAsync()
    {
        if(_busy)return;
        _busy=true; Enabled=false;
        try
        {
            var copy=FanConfigurationStore.Copy(Draft());
            await _apply(copy);
            _applied=copy;_status.Text="Ajustes guardados. Automatic conserva su autorización actual.";
        }
        catch(Exception ex){_status.Text="No se aplicaron los ajustes: "+ex.Message;}
        finally{_busy=false; Enabled=true;}
    }
    private void Export()
    {
        try
        {
            var c=Draft();c=c with {Tuning=c.Tuning with {MinimumLevel=Math.Max(30,c.Tuning.MinimumLevel)}};
            _=c.BuildPolicy();
            using var dialog=new SaveFileDialog {Filter="Configuración JSON|*.json",FileName="fan-test-30-50.json"};
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            FanConfigurationStore.Save(c,dialog.FileName);
            _status.Text="Prueba exportada con piso ≥30; no cambia los ajustes guardados ni inicia hardware.";
        }
        catch(Exception ex){_status.Text="No se exportó: "+ex.Message;}
    }
    private void RefreshCurve()
    {
        var axis=(AdaptiveCurveAxis)Math.Max(0,_axis.SelectedIndex);
        _profileName.Text=_profile.Name;
        _chart.Points=AdaptiveCurveProfiles.Curve(AdaptiveCurveProfiles.Validate(_profile),axis);
        _chart.MaximumInput=AdaptiveCurveProfiles.MaximumInput(axis);_chart.AxisLabel=_axis.Text;_chart.Invalidate();
    }
}
