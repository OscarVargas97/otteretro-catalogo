# Estándar de código (ia-qa)

Reglas neutrales al lenguaje. Las usan la revisión con IA del CI, el agente `revisor` y quien arma un perfil nuevo. Cada regla se mide por un **síntoma** que una herramienta o un revisor puede ver; los números están en «Umbrales» y cada repo los puede cambiar en `.calidad.yml`.

## 1. Criterios primero

- Cada criterio de aceptación del issue tiene código que lo cumple y, si es lógica con ramas, una prueba que falla sin el cambio.
- Lo que el issue deja fuera de alcance no aparece en el diff.

## 2. SOLID por sus síntomas

| Principio | Síntoma que se revisa |
|---|---|
| Responsabilidad única | Una función o clase que cambia por dos razones distintas (p. ej. reglas de negocio y formato de salida); archivos por encima del umbral de líneas; funciones con complejidad sobre el umbral |
| Abierto/cerrado | Un `if`/`switch` por tipo que se repite en varios lugares y crece con cada caso nuevo |
| Sustitución | Una subclase o implementación que lanza «no soportado», ignora parámetros o cambia el contrato del padre |
| Segregación de interfaces | Implementaciones con métodos vacíos o que lanzan para cumplir una interfaz grande |
| Inversión de dependencias | El dominio importa infraestructura (base de datos, HTTP, SDK, sistema de archivos) en vez de recibirla |

## 3. Capas y dependencias

Las dependencias apuntan hacia adentro: `interfaz (UI, CLI, API) → aplicación (casos de uso) → dominio`, e `infraestructura → dominio` (implementa sus puertos). Prohibido:

- Dominio que importa aplicación, interfaz o infraestructura.
- Interfaz que habla directo con infraestructura saltándose la aplicación (salvo repos pequeños que declaren en su `AGENTS.md` que no tienen capas).
- Ciclos entre módulos.

Cada repo con capas las declara en su config del perfil (`import-linter` en Python, `dependency-cruiser` en Node) y el CI las verifica.

## 4. Mapa de funciones

Cada repo puede generar un mapa (`lizard` o `ctags`): por archivo, sus funciones públicas con una línea de qué hacen, complejidad y largo. Sirve para que los agentes lean lo justo. Lo que se revisa:

- Funciones públicas con nombre que dice qué hacen, no cómo.
- Nada duplicado: antes de una función nueva, buscar si ya existe una equivalente (`jscpd` mide la duplicación textual; la semántica la ve la revisión).

## 5. Errores, bordes y datos

- Las entradas se validan donde entran (API, archivos, argumentos, variables de entorno), no más adentro.
- Ningún error se traga en silencio: se maneja, se propaga o se registra con contexto.
- Leer un estado y escribir según él sin transacción ni bloqueo es una carrera.
- Mover, borrar o reescribir datos del usuario: copiar, verificar y recién entonces borrar.
- Sin secretos en el código ni en logs.

## 6. Pruebas

- La lógica con ramas nueva o cambiada tiene prueba. Bugs: una prueba que reproduce el bug.
- Las pruebas verifican comportamiento observable, no detalles internos.

## Umbrales (por defecto)

| Medida | Umbral | Herramienta |
|---|---|---|
| Complejidad ciclomática por función | 10 | lizard |
| Líneas por función | 60 | lizard |
| Parámetros por función | 5 | lizard |
| Duplicación del repo | 3 % | jscpd |
| Ciclos y capas | 0 violaciones | import-linter / dependency-cruiser |

Los umbrales de lizard se aplican **a los archivos que cambia el PR**, para no bloquear un repo por su deuda anterior.

## Severidad en la revisión

- **Grave** (check rojo): criterio sin cumplir, bug probable, pérdida de datos, secreto, violación de capas, ciclo.
- **Menor** (comentario): nombres, duplicación pequeña, prueba que falta en lógica trivial, umbral superado por poco.
