/** Bytes a "812 GB" (o "1,4 GB" por debajo de 10) en el idioma activo. La API manda los long como número o texto. */
export function gb(bytes: number | string, locale: string): string {
  const value = Number(bytes) / 1024 ** 3;
  return `${value.toLocaleString(locale, { maximumFractionDigits: value < 10 ? 1 : 0 })} GB`;
}

/** Como gb, pero lo que no llega a 1 GB va en MB (el tamaño de un juego o un emulador, #160). */
export function size(bytes: number | string, locale: string): string {
  const mb = Number(bytes) / 1024 ** 2;
  return mb < 1024 ? `${mb.toLocaleString(locale, { maximumFractionDigits: mb < 10 ? 1 : 0 })} MB` : gb(bytes, locale);
}
