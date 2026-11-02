import type { QueryClient, QueryKey } from "@tanstack/react-query";

/**
 * Actualización optimista (#107): al tocar, el cambio se ve en el acto (en la caché de `key`) y se guarda por detrás; si el
 * guardado falla, vuelve a como estaba y la mutación queda con su error para avisar. Se pasa a `useMutation` con `...`.
 */
export function optimistic<T, V>(cache: QueryClient, key: QueryKey, apply: (old: T, vars: V) => T) {
  return {
    onMutate: async (vars: V) => {
      await cache.cancelQueries({ queryKey: key, exact: true }); // que una lectura en curso no pise el cambio
      const before = cache.getQueryData<T>(key);
      if (before !== undefined) cache.setQueryData<T>(key, apply(before, vars));
      return { before };
    },
    onError: (_error: Error, _vars: V, context?: { before?: T }) => {
      if (context?.before !== undefined) cache.setQueryData(key, context.before);
    },
  };
}
