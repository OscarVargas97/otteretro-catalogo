// Claves de next-intl con tipos: usar una que no existe en es.json no compila.
import type { Locale } from "@/messages";
import type es from "@/messages/es.json";

declare module "next-intl" {
  interface AppConfig {
    Locale: Locale;
    Messages: typeof es;
  }
}
