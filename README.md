<p align="center"><img src="web/public/nutria.png" alt="La nutria de Otteretro" width="96"></p>

<h1 align="center">Otteretro Catálogo</h1>

<p align="center"><b>El catálogo de sagas y juegos de <a href="https://github.com/OscarVargas97/otteretro">Otteretro</a>.</b><br>Los datos publicados, el generador y su administración.</p>

> **Estado:** en construcción. Si solo quieres usar Otteretro, no necesitas esto: la app trae el catálogo y se actualiza sola.

## Qué hay aquí

- **Administración:** un programa de escritorio con la misma interfaz de Otteretro, para generar y revisar el catálogo.
- **Generador:** trae los datos de IGDB y usa Claude para depurar sagas, armar rutas y comparar versiones.
- **Datos publicados:** llegarán como un archivo JSON por saga, para que un cambio se pueda proponer y revisar en un PR.
- **Imágenes:** este repo se encarga también de ellas; cómo se publican todavía se está definiendo.

## Usarlo

```powershell
./scripts/publicar.ps1 -OutputDir ..\catalogo-publicado
..\catalogo-publicado\OtteretroCatalogo.exe
```

El programa tiene su propia carpeta de datos y nunca abre los de Otteretro. Las claves de IGDB y SteamGridDB se configuran en **Ajustes › Cuentas y claves**.

## Créditos y licencia

Datos de juegos: [IGDB.com](https://www.igdb.com). El código se publica bajo la licencia [GPL-3.0](LICENSE).
