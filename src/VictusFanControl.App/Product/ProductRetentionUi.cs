using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductCanvas
{
    private void RetentionPanel(Graphics g)
    {
        DrawText(g,"Sensores de control",342,275,27,null,750,true);
        DrawText(g,"Temperatura, potencia y uso de CPU/GPU",342,342,23,Green,750);
        DrawText(g,"La curva y sus filtros de subida y bajada utilizan las seis variables principales. Se conservan los perfiles AC y Batería y las protecciones configuradas.",342,406,21,Muted,750,height:150);
        DrawText(g,"TZ01 y DTT3 retirados",342,590,24,null,750,true);
        DrawText(g,"La aplicación no consulta estas fuentes ni aplica la retención térmica experimental. Una configuración anterior no puede reactivarlas.",342,646,21,Muted,750,height:120);
        DrawText(g,"Ventiladores",1178,275,26,null,438,true);
        DrawText(g,"Se conserva la interfaz HP para leer y controlar los ventiladores. No se utiliza acceso directo al EC.",1178,342,21,Muted,438,height:150);
        DrawText(g,"Los límites CPU/GPU y las curvas guardadas se conservan.",1178,550,21,Muted,438,height:110);
        Button(g,"save","Guardar configuración",new(1178,777,438,40));
    }
}

internal sealed partial class ProductForm
{
    // Ignore commands from an older UI/session without editing curves or enabling readers.
    private bool HandleRetentionCommand(string id)
    {
        if(!id.StartsWith("retention-"))return false;
        if(!_canvas.Busy&&!_closing)
        {
            _canvas.Notice="TZ01/DTT3 y la retención experimental se han retirado. Se conserva la curva CPU/GPU.";
            _canvas.Invalidate();
        }
        return true;
    }
}
