"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useLocale, useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { LanguagePicker } from "@/components/LanguagePicker";
import { CredentialsSettings } from "@/components/Credentials";
import { request } from "@/lib/client";
import { gb } from "@/lib/format";
import { useDescribeError } from "@/lib/i18n";
import type { AppState, Drive, FolderRequest } from "@/lib/types";

// Clave en welcome.steps.* y fase en que se activa cada paso.
const STEPS = [
  { key: "welcome" },
  { key: "dataFolder" },
  { key: "accounts" },
] as const;

/** Asistente de primer arranque (pantallas.md §1). Funcionan los pasos 1, 2, 4 (discos) y 5 (cuentas de descarga, #191); el resto llega con su fase. Todo se puede saltar y hacer después en Ajustes. */
export default function Welcome() {
  const t = useTranslations("welcome");
  const [step, setStep] = useState(1);
  return (
    <div className="wizard">
      <aside className="wizard-steps">
        <span className="sidebar-logo" style={{ padding: 0 }}>
          <img src="/nutria.png" alt="" width={32} height={32} />
          <span>otteretro</span>
        </span>
        <ol>
          {STEPS.map((s, i) => (
            <li key={s.key} className="ot-nav-item" aria-current={i + 1 === step ? "page" : undefined}>
              <span>{i + 1}. {t(`steps.${s.key}`)}</span>
              {i + 1 < step ? <span className="small">✓</span> : <span />}
            </li>
          ))}
        </ol>
      </aside>
      <main className="wizard-body">
        <span className="small muted">{t("stepOf", { step, total: STEPS.length })}</span>
        {step === 1 ? <WelcomeStep onNext={() => setStep(2)} />
          : step === 2 ? <FolderStep onBack={() => setStep(1)} onNext={() => setStep(3)} />
          : <AccountsStep onBack={() => setStep(2)} />}
      </main>
    </div>
  );
}

function WelcomeStep({ onNext }: { onNext: () => void }) {
  const t = useTranslations("welcome");
  return (
    <>
      {/* La nutria da la bienvenida (#172). */}
      <img className="welcome-otter" src="/nutria.png" alt="" width={96} height={96} />
      <h1>Otteretro</h1>
      <p className="muted">{t("intro")}</p>
      <LanguagePicker />
      <div>
        <button className="ot-btn ot-btn-primary" type="button" onClick={onNext}>{t("start")}</button>
      </div>
    </>
  );
}

function FolderStep({ onBack, onNext }: { onBack: () => void; onNext: () => void }) {
  const t = useTranslations("welcome");
  const locale = useLocale();
  const describe = useDescribeError();
  const cache = useQueryClient();
  const setChosen = (state: AppState) => cache.setQueryData(["welcome-state"], state);
  const drives = useQuery({ queryKey: ["drives"], queryFn: () => request<Drive[]>("/api/drives") });
  const [path, setPath] = useState("");
  // La sugerida la decide el servidor: ~/Otteretro en Linux, el disco con más espacio libre en Windows.
  const chosen = path || drives.data?.find((d) => d.suggested)?.folder || "";

  const save = useMutation({
    mutationFn: (body: FolderRequest) => request<AppState>("/api/data-folder", { method: "POST", body: JSON.stringify(body) }),
        onSuccess: (state) => {
      setChosen(state);
      onNext();
    },
  });

  return (
    <>
      <h1>{t("chooseTitle")}</h1>
      <p className="muted">{t("chooseText")}</p>
      <div className="panel" style={{ gap: 0, padding: "0 16px" }}>
        {drives.data?.map((d) => {
          const option = d.folder;
          return (
            <label key={d.name} className="drive-option">
              <span>
                <input type="radio" name="drive" checked={chosen === option} onChange={() => setPath(option)} /> <b>{option}</b>
              </span>
              <span className="small muted">{t("freeOf", { free: gb(d.free, locale), total: gb(d.total, locale) })}</span>
            </label>
          );
        })}
      </div>
      <label className="small muted" htmlFor="path">{t("otherPath")}</label>
      <input id="path" value={chosen} onChange={(e) => setPath(e.target.value)} />
      {save.isError && <p className="error">{describe(save.error)}</p>}
      <div style={{ display: "flex", gap: 8 }}>
        <button className="ot-btn" type="button" onClick={onBack}>{t("back")}</button>
        <button className="ot-btn ot-btn-primary" type="button" disabled={!chosen || save.isPending} onClick={() => save.mutate({ path: chosen })}>
          {save.isPending ? t("preparing") : t("use", { path: chosen })}
        </button>
      </div>
    </>
  );
}

/** Paso 4 (§5.5, #201, #202): elegir los discos, adoptar la carpeta con la estructura antigua que se encuentre (pasa al estándar sola) o saltar. */
/** Cuentas de descarga (#191): 1fichier y Mega. Se puede saltar y volver desde Ajustes › Cuentas y claves. */
function AccountsStep({ onBack }: { onBack: () => void }) {
  const t = useTranslations("welcome");
  const router = useRouter();
  const cache = useQueryClient();
  const finish = () => {
    const state = cache.getQueryData<AppState>(["welcome-state"]);
    if (state) cache.setQueryData(["state"], state);
    router.replace("/");
  };
  return (
    <>
      <h1>{t("accountsTitle")}</h1>
      <p className="small muted">{t("accountsText")}</p>
      <CredentialsSettings only={["igdb", "steamgriddb", "rawg"]} />
      <div style={{ display: "flex", gap: 8 }}>
        <button className="ot-btn" type="button" onClick={onBack}>{t("back")}</button>
        <button className="ot-btn ot-btn-primary" type="button" onClick={finish}>{t("done")}</button>
      </div>
      <p className="small muted">{t("nextSteps")}</p>
    </>
  );
}
