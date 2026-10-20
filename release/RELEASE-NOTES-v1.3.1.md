# VictusFanControl 1.3.1

Corrige la casilla **Iniciar con Windows** de 1.3.0: podía quedar desmarcada aunque Windows hubiera registrado y habilitado correctamente el inicio. Guardar preferencias no cambiaba el resultado.

Windows puede devolver el usuario del disparador como nombre de cuenta en lugar de SID. Ahora se reconocen ambos formatos de la misma identidad actual y se mantienen todas las comprobaciones de seguridad y batería. El mensaje confirma claramente **Inicio con Windows activado/desactivado**; ya no pide guardar Automático.

Se corrige además la lectura posterior al clic cuando se usa un directorio de módulos personalizado. Los errores de escritura o verificación dejan el estado pendiente de consultar, en lugar de presentar una confirmación engañosa.

Actualiza desde el programa o descarga `VictusFanControl-1.3.1-Setup-win-x64.exe`. Conserva perfiles, curvas y preferencias. No requiere reiniciar Windows. El firmware no cambia: una placa que ya ejecuta el de 1.2.17 no necesita otra carga.

Mantiene las correcciones de inicio, cierre y recuperación de 1.3.0. Pruebas con el XML exportado real y fixtures aislados; sigue pendiente comprobar otro inicio físico de Windows. No se declaran nuevas pruebas físicas de ESP32, ventiladores o drivers.

[Causa, corrección y validación](../docs/RELEASE-1.3.1-VALIDATION.md).
