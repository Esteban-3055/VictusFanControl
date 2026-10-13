# Ajustar la velocidad visual sin recompilar — v1.2.6

1. Actualiza a v1.2.6 y abre VictusFanControl al menos una vez. Se crea automáticamente el archivo de ajuste.
2. Pulsa Win + R y abre `%LOCALAPPDATA%\VictusFanControl`.
3. Abre `fan-animation.json` con Bloc de notas. Su contenido inicial es:

```json
{
  "segundosPorVueltaA3000Rpm": 1.0
}
```

4. Cambia el número, guarda y observa Inicio. El ajuste se lee en la siguiente comprobación de un segundo mientras Inicio está visible; la velocidad se acerca gradualmente al nuevo ritmo, conservando la inercia. No hace falta reiniciar ni recompilar.

| Valor | Giro visual a 3000 RPM medidas |
|---:|---|
| 0.5 | Una vuelta cada 0,5 segundos; doble de rápido |
| 1.0 | Una vuelta por segundo; valor inicial |
| 1.5 | Una vuelta cada 1,5 segundos |
| 2.0 | Una vuelta cada 2 segundos; mitad de rápido |
| 3.0 | Una vuelta cada 3 segundos |

Cada ventilador sigue sus propias RPM. A 1500 RPM tarda el doble que la referencia; a 6000 RPM, la mitad. El número central sigue mostrando la última RPM válida medida.

Usa punto para los decimales en JSON, sin comillas alrededor del número. El rango admitido es 0.1–60 segundos. El archivo debe tener solo ese campo y guardarse en UTF-8, con o sin BOM. Los valores inválidos, archivos bloqueados/ausentes, contenido incompleto o mayor que 4 KiB conservan el último ajuste válido durante la sesión. Si al abrir la app no hay un ajuste válido disponible, se usa 1 segundo. Un archivo existente nunca se sobrescribe.

Al cambiar de pestaña, ocultar o minimizar, se detiene el temporizador de animación y se suspenden las lecturas de este archivo. Al volver a Inicio, el próximo tick aplica cualquier edición pendiente. La primera lectura inicial también permite abrir la app directamente con tu valor guardado.

Este ajuste solo cambia la representación visual. No cambia RPM físicas, curvas, seguridad, perfiles ni registros de telemetría. El archivo está en la carpeta persistente del usuario, separado de los directorios de cada versión; la actualización y el instalador conservan su contenido.

Comprobaciones: fixtures de archivo y animación, más integración WinForms de recarga por el temporizador existente, RPM independientes, continuidad de fase/velocidad, errores transitorios, pausa al cambiar de página/minimizar/ocultar y recuperación al volver, sin escrituras de hardware ni cambios de perfiles.
