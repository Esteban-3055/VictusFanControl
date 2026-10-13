# Prueba dirigida CPU 54 / GPU 52 — 1.2.8-preview.1

La observación con OMEN mostró CPU 5400 y GPU 5200 RPM nominales. Es evidencia del modo Máx. del firmware, no una calificación de nuestro comando 54/52. Esta versión permite comprobar esa combinación sin ampliar todavía el uso habitual.

## Abrir la prueba

1. Devuelve OMEN Gaming Hub a AUTO y sal de VictusFanControl desde la bandeja. Cerrar la ventana solo la minimiza.
2. Extrae el ZIP de esta preview en una carpeta nueva. No es necesario instalarlo ni reemplazar la versión estable.
3. Abre PowerShell como administrador con tu misma cuenta, en la carpeta extraída, y ejecuta:

```powershell
.\Start-ProductGui.ps1 -Mode FanRangeTest
```

El lanzador verifica el manifiesto y cada archivo antes de abrir la GUI. La entrada requiere el target HP 8C40 / 9D0R1LA / BIOS F.18, conserva las comprobaciones de telemetría, el supervisor WMI, la exclusión de llamadas nativas y la liberación. Arranca en Firmware; no activa Automático al iniciar ni cambia tus preferencias guardadas. Automático queda bloqueado durante esta prueba. Las RPM mostradas siguen siendo feedback WMI con resolución de 100 RPM, no confirmación independiente de ownership.

## Secuencia

En Ventiladores selecciona Manual. Mueve el nivel a **51** y pulsa **Aplicar nivel**. Espera al menos 30 segundos y observa varias lecturas nuevas. Repite con **52**, **53** y **54**, pulsando Aplicar una vez por nivel. No hace falta jugar ni ejecutar un benchmark para comprobar esta respuesta.

| Nivel lógico | Comando CPU | Comando GPU |
|---:|---:|---:|
| 51 | 51 | 51 |
| 52 | 52 | 52 |
| 53 | 53 | 52 |
| 54 | 54 | 52 |

La GPU nunca recibe un comando superior a 52. Una consigna nominal no garantiza un techo instantáneo exacto de RPM. Registra la meseta y cualquier oscilación; la lectura WMI está cuantizada y puede retrasarse durante la rampa. Si la GUI muestra un fallo o libera el control, termina la prueba y conserva el diagnóstico: no vuelvas a aplicar el comando que falló.

Después pulsa **Volver a Firmware**, espera el resultado y exporta un diagnóstico desde Configuración. Sal desde la bandeja. El ZIP contiene los comandos efectivos, los retornos WMI, la telemetría y la evidencia del supervisor. La aceptación WMI no se presentará como prueba independiente de propiedad del firmware. Ante una liberación no confirmada, los registros se conservan y no deben borrarse.

## Cambios de interfaz incluidos

- Añadir punto inserta en el centro del intervalo inmediatamente a la derecha del seleccionado y selecciona el punto nuevo. Sin selección, en el último punto, sin espacio o con 64 puntos, el botón se deshabilita. Los demás puntos se conservan.
- Los rotores usan dos superficies pequeñas opacas, con sus propios buffers. Cada cuadro actualiza los rotores y sus cifras estáticas sin repintar el lienzo grande. Continúan RPM independientes, inercia, archivo de ritmo y pausa por pestaña/ocultación/minimización.
- Exportar diagnóstico identifica el renderizador y registra la cadencia real. Las pruebas incluyen composición a escala 2; el resultado en tu 4K todavía debe medirse en el equipo.

La apertura normal conserva el control 10..50 y los perfiles existentes. La promoción del rango 54/52 a Automático y a sus curvas se hará después de revisar la prueba física. Esta preview no convierte los nuevos niveles en físicamente cualificados.
