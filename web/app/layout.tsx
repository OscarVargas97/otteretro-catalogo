import type { Metadata } from "next";
import type { ReactNode } from "react";
import localFont from "next/font/local";
import { Shell } from "@/components/Shell";
import "./styles/tokens.css";
import "./styles/components.css";
import "./styles/app.css";

// Las fuentes van en el repo (app/fonts): la app y su compilación funcionan sin internet. Definen los tokens del sistema de diseño.
const display = localFont({ src: "./fonts/bricolage-grotesque-latin.woff2", weight: "600 700", variable: "--font-display" });
const sans = localFont({
  src: [
    { path: "./fonts/atkinson-hyperlegible-400-latin.woff2", weight: "400" },
    { path: "./fonts/atkinson-hyperlegible-700-latin.woff2", weight: "700" },
  ],
  variable: "--font-sans",
});
const pixel = localFont({ src: "./fonts/silkscreen-latin.woff2", weight: "400", variable: "--font-pixel" });

export const metadata: Metadata = { title: "Otteretro" };

export default function Layout({ children }: { children: ReactNode }) {
  return (
    <html lang="es" className={`${display.variable} ${sans.variable} ${pixel.variable}`}>
      <body>
        <Shell>{children}</Shell>
      </body>
    </html>
  );
}
