using System.Text.Json;
using System.Text.RegularExpressions;

namespace Otteretro.Catalog;

/// <summary>Una receta publicada (#94): su id, su tipo, las consolas que corre y la receta entera en JSON compacto.</summary>
public record PublishedRecipe(string Id, string Kind, List<string> Consoles, string Json);

/// <summary>
/// Las recetas de emuladores (#94, decisión 349 de Otteretro): un archivo por receta en `recetas/`, con su esquema `recetas/receta.schema.json`.
/// Se editan en «Emuladores» (#95), en la base local como las sagas: `Write` escribe las que hay ahí y Publicar lleva las que cambiaron.
/// </summary>
public static partial class Published
{
    public const string RecipesFolder = "recetas";
    const string RecipeSchemaFile = "receta.schema.json";

    static IEnumerable<string> RecipeFiles(string folder)
    {
        var dir = Path.Combine(folder, RecipesFolder);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.json").Where(f => Path.GetFileName(f) != RecipeSchemaFile).Order(StringComparer.Ordinal)
            : [];
    }

    /// <summary>Las recetas, ya validadas, para compilarlas.</summary>
    static List<PublishedRecipe> ReadRecipes(string folder) => RecipeFiles(folder).Select(file =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var r = doc.RootElement;
        var consoles = r.TryGetProperty("consoles", out var k) ? k.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
        return new PublishedRecipe(r.GetProperty("id").GetString()!, r.GetProperty("kind").GetString()!, consoles, JsonSerializer.Serialize(r));
    }).ToList();

    /// <summary>Cada receta cumple el esquema, se llama como su id y no se repite.</summary>
    static void ValidateRecipes(string folder, List<string> errors)
    {
        var files = RecipeFiles(folder).ToList();
        if (files.Count == 0) return;
        var schemaPath = Path.Combine(folder, RecipesFolder, RecipeSchemaFile);
        if (!File.Exists(schemaPath)) { errors.Add($"Falta el esquema de las recetas: {RecipesFolder}/{RecipeSchemaFile}."); return; }
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (JsonException e) { errors.Add($"La receta {name} no es JSON válido: {e.Message}"); continue; }
            using (doc)
            {
                var problems = new List<string>();
                CheckSchema(schema.RootElement, schema.RootElement, doc.RootElement, "", problems);
                errors.AddRange(problems.Select(p => $"La receta {name} no cumple el esquema: {p}."));
                if (problems.Count == 0 && doc.RootElement.GetProperty("id").GetString() + ".json" != name)
                    errors.Add($"La receta {name} tiene otro id: {doc.RootElement.GetProperty("id").GetString()} (el archivo se llama como su id).");
            }
        }
    }

    /// <summary>El esquema de las recetas que trae el programa: lo usa la validación y el editor de «Emuladores» (#111).</summary>
    public static string RecipeSchema()
    {
        using var stream = typeof(Published).Assembly.GetManifestResourceStream(RecipeSchemaFile)!;
        return new StreamReader(stream).ReadToEnd();
    }

    /// <summary>Lo que no cumple el esquema de una receta, con el que trae el programa (#95: «Emuladores» la revisa al guardarla).</summary>
    public static List<string> CheckRecipe(JsonElement recipe)
    {
        using var schema = JsonDocument.Parse(RecipeSchema());
        var problems = new List<string>();
        CheckSchema(schema.RootElement, schema.RootElement, recipe, "", problems);
        return problems;
    }

    /// <summary>
    /// Lo de JSON Schema que usa `receta.schema.json`: type, enum, required, properties, additionalProperties: false, items, pattern,
    /// format: regex y $ref local. Lo demás (description, title) no valida nada.
    /// </summary>
    // ponytail: un subconjunto del estándar, el que usa el esquema; si suma oneOf, minimum u otra palabra, se agrega acá o se pasa a una librería.
    static void CheckSchema(JsonElement root, JsonElement schema, JsonElement value, string path, List<string> problems)
    {
        var at = path.Length == 0 ? "la raíz" : $"«{path}»";
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var target = root;
            foreach (var part in reference.GetString()!.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries)) target = target.GetProperty(part);
            CheckSchema(root, target, value, path, problems);
            return;
        }
        if (schema.TryGetProperty("enum", out var options) && !options.EnumerateArray().Any(o => JsonElement.DeepEquals(o, value)))
        {
            problems.Add($"{at} vale {value.GetRawText()}, y solo puede ser {string.Join(", ", options.EnumerateArray().Select(o => o.GetRawText()))}");
            return;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var ok = type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                var t => throw new InvalidDataException($"Tipo de JSON Schema que no se revisa: {t}"),
            };
            if (!ok) { problems.Add($"{at} tiene que ser {type.GetString()}"); return; }
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(text, pattern.GetString()!))
                problems.Add($"{at} no calza con {pattern.GetString()}: {text}");
            if (schema.TryGetProperty("format", out var format) && format.GetString() == "regex")
                try { _ = new Regex(text); }
                catch (ArgumentException) { problems.Add($"{at} no es una expresión regular válida: {text}"); }
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()!).Where(n => !value.TryGetProperty(n, out _)))
                    problems.Add($"falta «{Join(path, name)}»");
            var properties = schema.TryGetProperty("properties", out var p) ? p : default;
            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var sub))
                    CheckSchema(root, sub, property.Value, Join(path, property.Name), problems);
                else if (schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False)
                    problems.Add($"«{Join(path, property.Name)}» no está en el esquema");
            }
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var i = 0;
            foreach (var item in value.EnumerateArray()) CheckSchema(root, items, item, $"{path}[{i++}]", problems);
        }
    }

    static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";
}
