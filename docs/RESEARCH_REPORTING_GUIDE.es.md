# Protocolo para publicar investigaciones del HP 8C40

Documento de apoyo al [atlas técnico](HP_8C40_RESEARCH_ATLAS.es.md) / [English atlas](HP_8C40_RESEARCH_ATLAS.md). No concede autorización para modificar firmware, EC ni hardware.

## 1. Evidencia mínima de un hallazgo

| Campo | Qué registrar |
|---|---|
| Plataforma | Modelo, SKU, placa/revisión, BIOS, CPU/GPU y modelo de suspensión, **sin números de serie** |
| Código | Commit SHA y nombre exacto del script/harness; no basta indicar una versión comercial |
| Método | Read-only, replay, CI o cualificación de escritura explícitamente supervisada |
| Fuentes | MSR/PawnIO, NVML, HP WMI, ACPI o DTT; señalar si un dato EC fue solo de laboratorio |
| Tiempo | UTC de cada **adquisición** y no únicamente timestamp del snapshot que la consume |
| Experimento | Workload, fuente AC/batería, límites activos, condiciones iniciales y criterio de detención |
| Integridad | Hash SHA-256 de archivos de entrada/salida, manifiesto y cantidad de muestras válidas/descartadas |
| Resultado | Valores observados, estado PASS/FAIL/NO-GO y explicaciones alternativas |
| Pendiente | Pregunta comprobable y criterio para resolverla sin atribuir causalidad por coincidencia |

## 2. Convenciones de interpretación

- Etiquetar **OBSERVADO**, **CORRELACIONADO**, **HIPÓTESIS**, **CI/REPLAY**, **PENDIENTE** o **NO-GO** por afirmación.
- Diferenciar `fan level` HP WMI `0x2D` de RPM físicas. `level*100` es representación nominal; no constituye tacómetro.
- Las mesetas A/B/C/D del OEM son regímenes identificados inicialmente; `Unmapped` demuestra que no son exhaustivas.
- La aceptación de comandos WMI y NVML no equivale a readback independiente de ownership/locked range.
- Resultados del HP 88F8 no validan el HP 8C40. Una BIOS diferente exige reconfirmación.
- No reutilizar temporalmente datos posteriores para explicar causalmente una adquisición anterior.
- Separar los riesgos de firmware, ventilación y rendimiento; una prueba de un subsistema no cierra el otro.

## 3. Datos y seguridad pública

- **No** adjuntar ZIP o logs brutos al repositorio público sin inspección previa. Eliminar rutas de perfil Windows, nombres, números de serie/IDs, datos de usuario y variables sensibles.
- Mantener un manifiesto de hashes de fuentes originales de forma local si contienen información privada; publicar solo datos saneados, agregados y reproducibles.
- No promover lecturas/escrituras EC directas al producto. Los offsets AML/EC encontrados son diagnóstico e ingeniería inversa, no un mapa de registros escribibles.
- No alterar el firmware, cambiar límites térmicos ni ejecutar pruebas destructivas para completar una tabla de un issue.
- En recuperación pendiente, preservar journals y evidencia; no borrar el estado para que un harness acepte ejecutarse.
- Para una versión nueva, verificar **tag/commit**, instalador/ZIP y evidencias de la **misma compilación**. La historia de otro commit no se convierte automáticamente en PASS.

## 4. Plantilla de issue

```markdown
### Contexto / identidad exacta
- Target y BIOS:
- Versión del programa, tag y commit:
- Referencia documental:

### Pregunta o defecto verificable
- Comportamiento observado:
- Comportamiento esperado:
- Hipótesis alternativas:

### Evidencia
- Origen del dato y marca de adquisición:
- Sesiones independientes y tamaño de muestra:
- Condiciones y límites de la prueba:
- SHA-256 / enlaces públicos saneados:

### Clasificación
- [ ] Observación física
- [ ] Análisis de datos / replay
- [ ] Hipótesis sin validar
- [ ] Corrección de documentación
- [ ] Requiere nueva cualificación física

### Criterios de cierre
- [ ] Resultado reproducible en el commit señalado
- [ ] Sin extrapolación a otras placas/BIOS
- [ ] PASS/FAIL/NO-GO y limitaciones explícitos
- [ ] Enlace a documentación actualizada
```

## 5. Índice de material reproducible existente

- [Ensayo de identidad/telemetría 8C40](HARDWARE_8C40.md)
- [Clasificador OEM y sus limitaciones](OEM_SHADOW_CLASSIFICATION_2026-10-08.md)
- [Comparación con sesiones completas separadas](OEM_SHADOW_MODEL_COMPARISON_2026-10-08.md)
- [Política TZ01/DTT3 y replays](PLATFORM_THERMAL_POLICY_2026-10-08.md)
- [Incidente CPU+RAM y cronología de epochs](PLATFORM_CPU_RAM_INTERRUPTION_2026-10-08.md)
- [Validaciones aún pendientes del producto](PRODUCT_VALIDATION_STATUS.md)

**Criterio de publicación:** maximizar detalle técnico **verificable**, no cantidad de afirmaciones. Documentar una incertidumbre es preferible a inventar una explicación del firmware.
