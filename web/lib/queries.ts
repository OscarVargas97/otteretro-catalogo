import type { QueryClient } from "@tanstack/react-query";
import { request } from "./client";
import { pagedOptions } from "./paged";
import type { ApiError, ConsoleCount, GameDetail, GameRecipe, ReviewPage, RoutesPage, SagaDetail, SagaPage, VersionCompare } from "./types";

/** Consultas que se piden por adelantado (#105): la pantalla y el enlace usan la misma clave, así el clic encuentra los datos. */
export const sagaQuery = (id: string) => ({ queryKey: ["saga", id], queryFn: () => request<SagaDetail>(`/api/sagas/${id}`) });
export const routesQuery = (id: string) => ({ queryKey: ["saga", id, "routes"], queryFn: () => request<RoutesPage>(`/api/sagas/${id}/routes`) });
export const gameQuery = (id: string) => ({ queryKey: ["game", id], queryFn: () => request<GameDetail>(`/api/games/${id}`) });
export const compareQuery = (id: string) => ({ queryKey: ["game", id, "compare"], queryFn: () => request<VersionCompare[]>(`/api/games/${id}/compare`) });
export const gameRecipesQuery = (id: string) => ({ queryKey: ["game", id, "recipes"], queryFn: () => request<GameRecipe[]>(`/api/games/${id}/recipes`) });
export const consolesQuery = { queryKey: ["consoles"], queryFn: () => request<ConsoleCount[]>("/api/consoles") };
export const reviewQuery = { queryKey: ["review"], queryFn: () => request<ReviewPage>("/api/review") };
export const noticesQuery = { queryKey: ["notices"], queryFn: () => request<ApiError[]>("/api/notices") };

/** La lista de Sagas: `where` es "", "company:Nintendo" o "console:nes". */
export function sagasUrl(filter: string, where: string, sort: string, offset: number) {
  const params = new URLSearchParams({ sort });
  if (filter !== "all") params.set("filter", filter);
  const [kind, value] = where.split(":");
  if (kind === "company") params.set("company", value);
  if (kind === "console") params.set("platform", value);
  return `/api/sagas?${params}&offset=${offset}`;
}

/** La lista de Sagas por tramos: la misma para la pantalla y la pre-carga (#128). */
export const sagasQuery = (filter: string, where: string, sort: string) =>
  pagedOptions(["sagas", filter, where, sort], (offset) => sagasUrl(filter, where, sort, offset), (p: SagaPage) => ({ items: p.items, total: Number(p.total) }));

/** Pide lo que abre un enlace (a una saga, un juego o una pestaña del menú) antes del clic. Lo ya pedido y fresco no se repite. */
export function prefetchLink(client: QueryClient, href: string) {
  const url = new URL(href, location.origin);
  const id = url.searchParams.get("id");
  const path = url.pathname.replace(/\/$/, "");
  if (path === "/saga" && id) {
    client.prefetchQuery(sagaQuery(id));
    if (id !== "0") client.prefetchQuery(routesQuery(id));
  }
  else if (path === "/game" && id) {
    client.prefetchQuery(gameQuery(id));
    client.prefetchQuery(compareQuery(id));
  }
  else if (path === "/sagas")
    client.prefetchInfiniteQuery(sagasQuery("all", "", "recognized"));
  else if (path === "/review") client.prefetchQuery(reviewQuery);
  else if (path === "") {
    client.prefetchQuery(noticesQuery);
    client.prefetchQuery(reviewQuery);
  }
}
