// Conexión con la API local. La ventana abre la app con ?key=… (y en desarrollo ?api=…);
// se guardan para la sesión porque la navegación interna ya no los lleva en la URL.
import type { ApiError } from "./types";

function read(storageKey: string, param: string): string {
  if (typeof window === "undefined") return "";
  const fromUrl = new URLSearchParams(window.location.search).get(param);
  if (fromUrl) sessionStorage.setItem(storageKey, fromUrl);
  return sessionStorage.getItem(storageKey) ?? "";
}

const key = () => read("otteretro.key", "key");
const apiBase = () => read("otteretro.api", "api");

/** Error de la API como código: lo traduce useDescribeError (#142). */
export class ApiRequestError extends Error {
  constructor(readonly error: ApiError) {
    super(error.code);
  }
}

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(apiBase() + path, {
    ...init,
    headers: { "Content-Type": "application/json", "X-Otteretro-Key": key(), ...init?.headers },
  });
  if (!response.ok) {
    const body: ApiError | null = await response.json().catch(() => null);
    throw new ApiRequestError(body?.code ? body : { code: "http", params: { status: String(response.status) } });
  }
  // Algunas respuestas no traen cuerpo (abrir carpeta, borrar): no se leen como JSON.
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/** EventSource no admite cabeceras, así que la clave va en la URL. */
export const eventsUrl = () => `${apiBase()}/api/events?key=${encodeURIComponent(key())}`;

/** Portada, héroe o logo de un juego (SteamGridDB, guardado en la carpeta de datos). Un <img> no manda cabeceras. */
export const imageUrl = (gameId: number | string, kind: "cover" | "hero" | "logo") =>
  `${apiBase()}/api/images/${gameId}/${kind}?key=${encodeURIComponent(key())}`;
