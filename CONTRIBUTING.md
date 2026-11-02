# Cómo proponer un cambio

El catálogo son archivos de texto en [`catalogo/`](catalogo). Un cambio es un PR, y al fusionarlo Otteretro lo recibe solo.

## Qué hay

| Archivo | Qué es |
|---|---|
| `catalogo/sagas/<id>-<nombre>.json` | Una saga con sus juegos, versiones y rutas |
| `catalogo/sin-saga/<parte>.json` | Los juegos que no tienen saga |
| `catalogo/consolas.json` | Las consolas |
| `catalogo/reemplazados.json` | Lo que dejó de existir y con qué se reemplazó |

## Reglas

- **Los ids son permanentes.** Las colecciones de las personas apuntan a ellos: no cambies ni reutilices uno.
- **Para fusionar o quitar un juego,** agrégalo a `reemplazados.json` con `newId`, o con `newId` vacío si no tiene reemplazo. Así nadie pierde su progreso.
- **Para corregir un dato,** cambia su fila en `fields` con `"manual": true`, y también el campo del juego.
- **En una ruta,** cada paso apunta a un juego y a una versión que existan.
- **Nada personal:** ni tu colección, ni enlaces de descarga, ni de dónde sacar un juego.

## Antes de enviar

```powershell
dotnet build src/Otteretro -p:OutputType=Exe
src/Otteretro/bin/Debug/net10.0/OtteretroCatalogo.exe catalogo validar catalogo
```

La misma revisión corre sola en cada PR, y un dato inválido no pasa.

## Publicar desde la administración

En una saga o un juego, **Publicar** muestra qué cambia respecto de lo publicado y abre el PR por ti. Necesitas un token clásico de GitHub con el permiso `public_repo` en **Ajustes › Cuentas y claves**. Si no puedes escribir en este repo, el programa crea un fork en tu cuenta y abre el PR desde ahí. Publicar otra vez lo mismo actualiza el PR abierto. El programa solo propone: los PR los revisa y fusiona quien administra el catálogo.

## Regenerar desde la administración

Quien administra el catálogo trabaja en el programa y escribe los archivos con `catalogo publicar catalogo`. El resultado es estable: en el PR solo se ve lo que cambió.

## Créditos

Los datos de juegos vienen de [IGDB.com](https://www.igdb.com). Los aportes se publican bajo la licencia [GPL-3.0](LICENSE) del repo.
