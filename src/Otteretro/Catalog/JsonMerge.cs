using System.Text.Json.Nodes;

namespace Otteretro.Catalog;

/// <summary>
/// Fusión en tres vías por campo de un archivo publicado (#91): parte de la base local, suma lo que cambió en main y lo que cambió acá.
/// Solo es conflicto un mismo campo que cambió distinto en los dos lados. Las listas de objetos con id (juegos, versiones, campos,
/// criterios, pasos) se fusionan elemento por elemento; las demás listas, enteras.
/// </summary>
public static class JsonMerge
{
    /// <summary>Con qué se reconoce un elemento de una lista entre las tres versiones.</summary>
    static readonly string[] Keys = ["id", "field", "criterion", "position"];

    /// <param name="origin">La base de la que parte el catálogo local; null si el archivo no existía.</param>
    /// <param name="conflicts">Donde se anotan los campos en conflicto (p. ej. games[10].images.cover); en ellos queda lo local.</param>
    /// <returns>Lo fusionado; null si el archivo no queda.</returns>
    public static JsonNode? Merge(JsonNode? origin, JsonNode? main, JsonNode? local, List<string> conflicts, string at = "")
    {
        if (Same(main, local) || Same(origin, local)) return main?.DeepClone();
        if (Same(origin, main)) return local?.DeepClone();
        if (main is JsonObject m && local is JsonObject l)
        {
            var o = origin as JsonObject;
            var result = new JsonObject();
            // El orden de lo local (el del generador), y al final lo que solo trae main.
            foreach (var name in l.Select(p => p.Key).Concat(m.Select(p => p.Key).Where(k => !l.ContainsKey(k))).ToList())
                if (Merge(o?[name], m[name], l[name], conflicts, at.Length == 0 ? name : $"{at}.{name}") is { } value) result[name] = value;
            return result;
        }
        if (main is JsonArray ma && local is JsonArray la && KeyOf(ma, la, origin as JsonArray) is { } key)
        {
            string Id(JsonNode? e) => e![key]!.ToJsonString();
            Dictionary<string, JsonNode?> Index(JsonArray? a) => a?.ToDictionary(Id) ?? [];
            var (oi, mi, li) = (Index(origin as JsonArray), Index(ma), Index(la));
            var result = new JsonArray();
            // ponytail: lo que solo agregó main va al final, no en su lugar; basta mientras el generador ordene al escribir.
            foreach (var id in la.Select(Id).Concat(ma.Select(Id).Where(k => !li.ContainsKey(k))).ToList())
                if (Merge(oi.GetValueOrDefault(id), mi.GetValueOrDefault(id), li.GetValueOrDefault(id), conflicts, $"{at}[{id.Trim('"')}]") is { } value)
                    result.Add(value);
            return result;
        }
        conflicts.Add(at.Length == 0 ? "(archivo)" : at);
        return local?.DeepClone();
    }

    /// <summary>Igualdad por contenido en la que un campo ausente y una lista vacía valen lo mismo: el generador omite las listas
    /// vacías que main puede traer como [] (p. ej. criteria, routes), y eso no es cambio ni conflicto.</summary>
    public static bool Same(JsonNode? a, JsonNode? b)
    {
        static bool Empty(JsonNode? n) => n is null || n is JsonArray { Count: 0 };
        if (Empty(a) && Empty(b)) return true;
        if (a is JsonObject x && b is JsonObject y)
            return x.Select(p => p.Key).Union(y.Select(p => p.Key)).All(k => Same(x[k], y[k]));
        if (a is JsonArray p && b is JsonArray q)
            return p.Count == q.Count && p.Zip(q).All(t => Same(t.First, t.Second));
        return JsonNode.DeepEquals(a, b);
    }

    /// <summary>La clave que identifica cada elemento en las tres listas, o null si no hay una.</summary>
    static string? KeyOf(params JsonArray?[] arrays) =>
        Keys.FirstOrDefault(k => arrays.All(a => a is null || (a.All(e => e is JsonObject o && o[k] is JsonValue)
            && a.Select(e => e![k]!.ToJsonString()).Distinct().Count() == a.Count)));
}
