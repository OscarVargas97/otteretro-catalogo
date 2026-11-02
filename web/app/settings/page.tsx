"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { Suspense } from "react";
import { ClaudeSettings } from "@/components/ClaudeSettings";
import { ConsoleSettings } from "@/components/ConsoleSettings";
import { CredentialsSettings } from "@/components/Credentials";
import { ImagesFolder } from "@/components/ImagesFolder";
import { LanguagePicker } from "@/components/LanguagePicker";
import { UpdateChannel, VersionAndUpdate } from "@/components/Update";
import { useAppState } from "@/components/Shell";
import { Tabs } from "@/components/Tabs";

/** Las pestañas de Ajustes, en orden (#97, #111). La que viene en `?s=` se abre; sin ella, la primera. */
const SECTIONS = ["credentials", "consoles", "claude", "general"] as const;
/** Las pestañas de antes (#111, #199; la carpeta de datos es parte de Almacenamiento, #208). */
const OLD: Record<string, Section> = {};
type Section = (typeof SECTIONS)[number];

export default function Settings() {
  return <Suspense><SettingsTabs /></Suspense>;
}

/** Pestañas arriba y una sola columna centrada (#200). */
function SettingsTabs() {
  const t = useTranslations("settings");
  const router = useRouter();
  const asked = useSearchParams().get("s");
  const current: Section = SECTIONS.find((s) => s === asked) ?? OLD[asked ?? ""] ?? SECTIONS[0];
  const title = (s: Section) => t(s === "consoles" ? "consoles.title" : s === "claude" ? "claude.title" : s);

  return (
    <div className="content">
      <div className="centered">
        <Tabs tabs={SECTIONS.map((s) => ({ id: s, label: title(s) }))} current={current} label={t("sections")} panel="settings-panel"
          onSelect={(s) => router.push(`/settings/?s=${s}`)} />
        {/* Sin segundo título (#108): la pestaña ya lo dice. */}
        <section className="sec" id="settings-panel" role="tabpanel" aria-labelledby={`tab-${current}`}>
          <Panel section={current} />
        </section>
      </div>
    </div>
  );
}

function Panel({ section }: { section: Section }) {
  const t = useTranslations("settings");
  const { data } = useAppState();
  switch (section) {
    case "credentials": return <CredentialsSettings />;
    case "consoles": return <ConsoleSettings />;
    case "claude": return <ClaudeSettings />;
    case "general": return (
      <div className="settings-blocks">
        <section className="panel"><h2>{t("language")}</h2><LanguagePicker /></section>
        <section className="panel"><h2>{t("images.title")}</h2><ImagesFolder /></section>
        <VersionAndUpdate version={data?.version} />
        <UpdateChannel />
        <p className="small muted" style={{ margin: 0 }}>{t("attribution")}</p>
      </div>
    );
  }
}
