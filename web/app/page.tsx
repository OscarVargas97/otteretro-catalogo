"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { Empty } from "@/components/Shell";
import { NextCards } from "@/components/Skeleton";
import { useDynamicT } from "@/lib/i18n";
import { noticesQuery, reviewQuery } from "@/lib/queries";

/** Panel de la administración: las claves que faltan y lo que espera en Revisar. */
export default function Home() {
  const t = useTranslations("home");
  const tn = useTranslations("notices");
  const dt = useDynamicT();
  const format = useFormatter();
  const notices = useQuery(noticesQuery);
  const review = useQuery(reviewQuery);
  const pending = review.data?.pending.length ?? 0;
  const missing = notices.data?.filter((n) => n.code === "notices.missingKey") ?? [];
  if (notices.isPending || review.isPending)
    return <div className="content" aria-busy="true"><section className="sec"><div className="sec-h"><h2>{t("review")}</h2></div><NextCards count={2} /></section></div>;

  return (
    <>
      {missing.length > 0 && (
        <div className="notice" role="note" aria-label={tn("title")}>
          <span>{tn("missingKeys", { services: format.list(missing.map((n) => dt(`credentials.service.${n.params?.service}`))) })}</span>
          <Link className="ot-btn" href="/settings/?s=credentials">{tn("openCredentials")}</Link>
        </div>
      )}
      <div className="content">
        <section className="sec">
          <div className="sec-h"><h2>{t("review")}</h2></div>
          {pending > 0
            ? <div className="panel"><Link className="ot-btn" href="/review/" style={{ alignSelf: "flex-start" }}>{t("reviewCount", { count: pending })}</Link></div>
            : <Empty title={t("nothingTitle")} text={t("nothingText")}><Link className="ot-btn ot-btn-primary" href="/sagas/">{t("seeSagas")}</Link></Empty>}
        </section>
      </div>
    </>
  );
}
