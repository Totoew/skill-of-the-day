import React, { useEffect, useRef, useState } from "react";
import { request } from "../auth";
import { useCatalog } from "./CatalogProvider";
import Dashboard from "../pages/Dashboard";

export default function ServerRoadmap({ profile, update, reloadProfile }) {
  const { catalog } = useCatalog();
  const [result, setResult] = useState(null), [loading, setLoading] = useState(true),
    [error, setError] = useState(null), [layer, setLayer] = useState(null), [revision, setRevision] = useState(0);
  const scope = JSON.stringify([profile.direction, profile.level, profile.regions]);
  const loadedScope = useRef(null), previousScope = useRef(scope), manual = useRef(false);
  useEffect(() => {
    if (previousScope.current !== scope) { previousScope.current = scope; setLayer(null); }
  }, [scope]);
  useEffect(() => {
    const controller = new AbortController();
    let current = true;
    setLoading(true); setError(null);
    const params = new URLSearchParams({ profileVersion: profile.version });
    if (layer !== null) params.set("layer", layer);
    const recalculate = manual.current; manual.current = false;
    request(`${recalculate ? "/recalculate" : "/plan"}?${params}`, undefined, {
      service: "recommendations", authenticated: true, method: recalculate ? "POST" : "GET", signal: controller.signal,
    }).then((received) => {
      if (!current) return;
      if (received.profileVersion !== profile.version || !Array.isArray(received.steps) || !Array.isArray(received.jobs))
        throw new Error("Сервис рекомендаций вернул некорректный план.");
      loadedScope.current = scope; setResult(received);
      // When learning removes the selected layer, clear it rather than silently reapplying it if it reappears later.
      if (layer !== null && received.selectedLayer === null) setLayer(null);
    }).catch((failure) => { if (current) setError(failure); })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; controller.abort(); };
  }, [profile.version, scope, layer, catalog.version, revision]);
  function retry() { manual.current = true; setRevision(value => value + 1); }
  if (!result || loadedScope.current !== scope) return <section className="container page dashboard">
    <h1>Ваш roadmap</h1>
    {error ? <><p className="notice" role="alert">{error.message}</p><button className="button" onClick={error.status === 409 ? reloadProfile : retry}>{error.status === 409 ? "Загрузить актуальный профиль" : "Повторить расчёт"}</button></> : <p role="status">Загружаем персональный план…</p>}
  </section>;
  return <Dashboard profile={profile} update={update} result={result} processing={loading}
    blocked={loading || !!error} error={error} recalculate={retry} chooseLayer={setLayer} reloadProfile={reloadProfile} />;
}
