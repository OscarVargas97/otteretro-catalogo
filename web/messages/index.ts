// Catálogos de textos por idioma (#140). El español es el base: cada catálogo tiene que tener sus mismas claves,
// y TypeScript falla si a uno le falta alguna. Para sumar un idioma: su .json, una línea acá y en AppConfig.Languages.
import es from "./es.json";
import en from "./en.json";

export const CATALOGS: Record<"es" | "en", typeof es> = { es, en };

export type Locale = keyof typeof CATALOGS;
export const LOCALES = Object.keys(CATALOGS) as Locale[];
export const isLocale = (value: string | undefined): value is Locale => LOCALES.includes(value as Locale);

/** Idioma del sistema antes de saber el elegido: español si empieza por "es", si no inglés (como AppConfig). */
export const systemLocale = (): Locale =>
  typeof navigator !== "undefined" && navigator.language.toLowerCase().startsWith("es") ? "es" : "en";
