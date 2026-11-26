import React, { createContext, useContext, useEffect, useState } from "react";
import { request } from "../auth";
import { applySkillCatalog } from "../data";

const CatalogContext = createContext(null);
export const useCatalog = () => useContext(CatalogContext);

export default function CatalogProvider({ children }) {
  const [catalog, setCatalog] = useState(null), [error, setError] = useState("");
  async function reload() {
    const received = await request("/catalog", undefined, { service: "skills", method: "GET" });
    applySkillCatalog(received);
    setCatalog(received);
    setError("");
    return received;
  }
  useEffect(() => { reload().catch((failure) => setError(failure.message)); }, []);
  if (!catalog) return <main className="container page">
    <h1>{error ? "Справочник временно недоступен" : "Загружаем справочник"}</h1>
    {error ? <><p role="alert">{error}</p><button className="button" onClick={() => reload().catch((failure) => setError(failure.message))}>Повторить загрузку</button></> : <p role="status">Загрузка…</p>}
  </main>;
  return <CatalogContext.Provider value={{ catalog, reload }}>{children}</CatalogContext.Provider>;
}
