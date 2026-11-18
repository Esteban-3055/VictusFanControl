# CPU y GPU independientes de los ventiladores

En Rendimiento selecciona «Aplicar límite de CPU» y «Aplicar límite de GPU» por separado, y pulsa Aplicar. Puedes elegir CPU sola, GPU sola, ambos o ninguno. Guardar conserva la selección y los valores para próximos inicios. El botón Automático prepara los límites que hayas seleccionado antes de iniciar los ventiladores; los cambios posteriores no detienen ni renuevan esa sesión de ventiladores.

Apagar CPU restaura solo sus campos RAPL. Apagar GPU ejecuta solo el Reset de su sesión NVML. El otro dominio conserva propietario y valores: no se reinicia todo el Guardian para cambiar una casilla. Ambos apagados conservan una sesión supervisada sin controles de potencia/reloj; los lectores de temperatura y las protecciones de ventiladores siguen activos.

Cada dominio se construye al activarlo, verifica su destino y requisitos habituales y conserva sus diarios de propiedad. Una liberación confirmada retira ese propietario; volver a activarlo abre una sesión nueva de ese dominio. Cambiar AC/Batería solo llama a los dominios seleccionados. La suspensión libera controles y la vuelta conserva la última selección confirmada, sin reactivar dominios apagados por el usuario.

Un fallo no se presenta como éxito ni borra registros. Los cambios confirmados se informan por dominio; la limpieza intenta ambos propietarios independientemente. Una operación no confirmada conserva las barreras de seguridad y de recuperación existentes. La recuperación sigue siendo únicamente de liberación, sin reaplicar ajustes.

Las pruebas sin hardware cubren ninguna/CPU/GPU/ambas selecciones, encendido perezoso, apagado sin modificar el compañero, regreso de pantalla, fuente, fallos parciales, limpieza independiente, interfaz y comunicación real entre procesos. La observación física de esas combinaciones sigue pendiente para esta versión.
