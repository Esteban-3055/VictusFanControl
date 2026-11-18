# Prototipo de aislamiento, sin hardware

Ejecutable de laboratorio para el bloque 9. No es un motor de control de producción y no se distribuye en el instalador. Carece de referencias a hardware, VFC runtime, WMI, PawnIO y NVML. Todos sus estados declaran `FixtureOnly=true` y `HardwareAuthority=false`.

```powershell
dotnet build tools/ControlIsolationPrototype/ControlIsolationPrototype.csproj -c Release -warnaserror -p:NuGetAudit=false
dotnet tools/ControlIsolationPrototype/bin/Release/net8.0-windows/VfcIsolationFixture.dll --self-test
```

La prueba crea un host y visores en procesos separados, sin ventanas. Verifica:

1. Cambiar el proceso visor conserva identidad y estado del host.
2. Una segunda instancia del host no toma un propietario vivo.
3. El canal local restringido al usuario usa desafío HMAC; MAC incorrecta y solicitud capturada con otro desafío se rechazan.
4. Autenticación no concede capacidad de control: incluso la operación `control` permanece denegada.
5. El estado simulado pendiente se conserva hasta una señal de retorno exacta del arnés.
6. Salir del host con estado simulado pendiente deja evidencia intacta y bloquea su reemplazo, aunque el PID anterior ya no esté vivo.

La clave es aleatoria, efímera y se transmite por stdin del hijo. No se lee la clave de emparejamiento ESP32 ni se escribe la clave de prueba a disco o a argumentos. Los archivos del arnés viven en un directorio temporal propio y no se llaman como los journals del producto. La limpieza final elimina solo datos de ese fixture.

El protocolo de ejemplo no ofrece comandos nativos ni acredita seguridad de una futura integración. La prueba de retorno llega del arnés porque no hay driver: nunca debe convertirse en un botón que certifique un retorno real. Los requisitos de promoción están en [el informe de bloques](../../docs/STABILITY-RECOVERY-HARDENING.md).
