namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private HyperXMonitor? _hyperX;
    private HyperXSettings _hyperXSettings=new();
    private readonly HyperXNotificationPolicy _hyperXNotifications=new();
    private HyperXBatteryPage? _hyperXPage;
    private Task _hyperXStartup=Task.CompletedTask;
    private string? _hyperXNotice;
    private readonly System.Windows.Forms.Timer _hyperXTimer=new(){Interval=1000};
    private async Task InitializeHyperXAsync()
    {
        if(_isolatedRuntime)return; // Fixtures never open USB or the user's settings.
        var loaded=await Task.Run(()=>{var cfg=HyperXSettings.Load(HyperXSettings.DefaultPath,out var notice);return(Settings:cfg,Notice:notice);});
        if(_closing||IsDisposed)return;
        _hyperXSettings=loaded.Settings;_hyperXNotice=loaded.Notice;_hyperX=new();_hyperX.Start();
        _hyperXTimer.Tick+=(_,_)=>{if(!_closing&&!IsDisposed)PresentHyperX();};_hyperXTimer.Start();LayoutHyperXPage();
    }
    private async Task SaveHyperXAsync(HyperXSettings settings)
    {
        settings.Validate();
        if(!_isolatedRuntime)await Task.Run(()=>settings.Save(HyperXSettings.DefaultPath));
        if(!_closing&&!IsDisposed)_hyperXSettings=settings;
    }
    private void LayoutHyperXPage()
    {
        if(_closing||IsDisposed)return;
        bool selected=_canvas.Page==ProductPage.HyperXBattery;
        if(selected&&_hyperXPage is null&&(_isolatedRuntime||_hyperX is not null))
        {
            _hyperXPage=new(_hyperXSettings,SaveHyperXAsync);_canvas.Controls.Add(_hyperXPage);_hyperXPage.SetNotice(_hyperXNotice);
        }
        if(_hyperXPage is null)return;
        _hyperXPage.Visible=selected;
        var scale=Math.Min(_canvas.ClientSize.Width/1672f,_canvas.ClientSize.Height/941f);
        var x=(_canvas.ClientSize.Width-1672*scale)/2;var y=(_canvas.ClientSize.Height-941*scale)/2;
        _hyperXPage.Bounds=Rectangle.Round(new RectangleF(x+310*scale,y+150*scale,1337*scale,680*scale));
        _hyperXPage.UpdateReading(_hyperX?.Snapshot??new(){Status=_isolatedRuntime?"Lectura aislada de prueba":"Preparando lector"},DateTimeOffset.UtcNow);
    }
    private void PresentHyperX()
    {
        if(_hyperXStartup.IsFaulted){_hyperXNotice="No se pudo iniciar el lector HyperX.";_hyperXPage?.SetNotice(_hyperXNotice);return;}
        var monitor=_hyperX;if(monitor is null)return;
        var now=DateTimeOffset.UtcNow;
        foreach(var snapshot in monitor.DrainMuteEvents())Notify(snapshot);
        var current=monitor.Snapshot;Notify(current);
        if(_hyperXPage?.Visible==true)_hyperXPage.UpdateReading(current,now);
        void Notify(HyperXSnapshot snapshot)
        {
            foreach(var n in _hyperXNotifications.Evaluate(snapshot,_hyperXSettings,DateTimeOffset.UtcNow))
            {
                if(_closing||IsDisposed||!_tray.Visible)continue;
                try{_tray.ShowBalloonTip(8000,n.Title,n.Text,ToolTipIcon.Info);}
                catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){AppLog.Write("HyperX notification unavailable: "+e.GetType().Name);}
            }
        }
    }
    private async Task DisposeHyperXAsync()
    {
        if(_hyperX is{} monitor)await monitor.DisposeAsync();
    }
}
