"use client";

import { useMutation } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import { useState } from "react";
import { request } from "@/lib/client";
import { useDescribeError } from "@/lib/i18n";
import type { RecipeText, RepoRelease } from "@/lib/types";

/**
 * «Agregar una receta» (#110, decisión 367), con InstallPanel de Otteretro como modelo: se pega la URL de un repo (GitHub, Forgejo o
 * Gitea), se ven sus releases con los archivos de cada una y, al elegir el de Windows, el servidor arma el borrador (id y nombre del
 * repo, fuente y el patrón del archivo con la versión cambiada por un comodín) que se completa en el formulario de edición. Si el archivo no
 * trae un ejecutable claro (otteretro-catalogo#174), se elige aquí entre los .exe que trae.
 */
export function RecipeAdd({ onDraft, onCancel, repo = "" }: { onDraft: (recipe: Record<string, unknown>) => void; onCancel: () => void; repo?: string }) {
  const t = useTranslations("emulators");
  const format = useFormatter();
  const describe = useDescribeError();
  const [url, setUrl] = useState(repo);
  const find = useMutation({ mutationFn: () => request<RepoRelease[]>(`/api/recipes/releases?url=${encodeURIComponent(url.trim())}`) });
  const draft = useMutation({
    mutationFn: (file: string) => request<RecipeText>("/api/recipes/draft", { method: "POST", body: JSON.stringify({ url: url.trim(), file }) }),
    onSuccess: (r) => { if (!r.exes) onDraft(JSON.parse(r.json)); },
  });
  const exes = draft.data?.exes;
  const useExe = (detect: string) => onDraft({ ...JSON.parse(draft.data!.json), detect });

  return (
    <section className="panel">
      <form className="toolbar" onSubmit={(e) => { e.preventDefault(); find.mutate(); }}>
        <input style={{ flex: 1 }} aria-label={t("repoUrl")} placeholder={t("repoPlaceholder")} value={url} onChange={(e) => setUrl(e.target.value)} />
        <button className="ot-btn ot-btn-primary" type="submit" disabled={!url.trim() || find.isPending}>{t("addFind")}</button>
        <button className="ot-btn" type="button" onClick={onCancel}>{t("cancel")}</button>
      </form>
      {[find.error, draft.error].filter(Boolean).map((e, i) => <p key={i} className="error small" role="alert">{describe(e)}</p>)}
      {exes && (
        <div style={{ display: "grid", gap: 4 }}>
          <p className="small" style={{ margin: 0 }}>{exes.length > 0 ? t("addExes") : t("addNoExe")}</p>
          {exes.map((exe) => (
            <div key={exe} className="toolbar">
              <span style={{ flex: 1 }}>{exe}</span>
              <button className="ot-btn" type="button" onClick={() => useExe(exe)}>{t("addUseExe")}</button>
            </div>
          ))}
          <div className="toolbar"><button className="ot-btn" type="button" onClick={() => useExe("")}>{t("addContinue")}</button></div>
        </div>
      )}
      {find.data && (
        <>
          <p className="small muted" style={{ margin: 0 }}>{t("addPick")}</p>
          {find.data.map((rel) => (
            <div key={rel.tag} style={{ display: "grid", gap: 4 }}>
              <strong>{rel.tag} <span className="small muted">{rel.publishedAt ? format.dateTime(new Date(rel.publishedAt), { dateStyle: "medium" }) : t("addNoDate")}</span></strong>
              {rel.files.map((f) => (
                <div key={f.name} className="toolbar">
                  <span style={{ flex: 1 }}>{f.name}{f.windows && <span className="small muted"> · {t("addWindows")}</span>}</span>
                  <button className="ot-btn" type="button" disabled={draft.isPending} onClick={() => draft.mutate(f.name)}>{t("addUse")}</button>
                </div>
              ))}
            </div>
          ))}
        </>
      )}
    </section>
  );
}
