import React, { useEffect, useRef, useState } from "react";
import { loadProfile } from "../data";
import { request } from "../auth";
import Dashboard from "../pages/Dashboard";
import Profile from "../pages/Profile";

const profileRequest = (path, body, options = {}) => request(path, body,
  { ...options, service: "profile", authenticated: true });
const settings = (profile) => ({ direction: profile.direction, level: profile.level,
  regions: profile.regions, skills: profile.skills, version: profile.version });

export default function ServerProfile({ user, route }) {
  const [profile, setProfile] = useState(null), [loading, setLoading] = useState(true),
    [saving, setSaving] = useState(false), [error, setError] = useState("");
  const pending = useRef(false), mounted = useRef(true);
  const legacyKey = `skill-day-profile:${user.id}`;

  async function load() {
    setLoading(true); setError("");
    try {
      let received = await profileRequest("/me", undefined, { method: "GET" });
      let legacy = null;
      try { legacy = localStorage.getItem(legacyKey); } catch { /* Storage can be disabled. */ }
      // Import only this account's old settings, and only while the server has no saved profile.
      if (received.version === 0 && legacy) {
        try {
          received = await profileRequest("/me", { ...settings(loadProfile(legacyKey)), version: 0 }, { method: "PUT" });
        } catch (failure) {
          if (failure.status !== 409) throw failure;
          received = await profileRequest("/me", undefined, { method: "GET" });
        }
      }
      try { if (received.version > 0) localStorage.removeItem(legacyKey); } catch { /* Best effort legacy cleanup. */ }
      if (mounted.current) setProfile(received);
    } catch (failure) { if (mounted.current) setError(failure.message); }
    finally { if (mounted.current) setLoading(false); }
  }

  useEffect(() => {
    mounted.current = true;
    load();
    return () => { mounted.current = false; };
  }, [user.id]);

  async function update(next) {
    if (pending.current) return false;
    pending.current = true; setSaving(true); setError("");
    try {
      const received = await profileRequest("/me", { ...settings(next), version: profile.version }, { method: "PUT" });
      if (mounted.current) setProfile(received);
      return true;
    } catch (failure) {
      if (mounted.current) setError(failure.message);
      return false;
    } finally {
      pending.current = false;
      if (mounted.current) setSaving(false);
    }
  }

  if (loading) return <section className="container page"><h1>Загружаем профиль</h1><p role="status">Загрузка…</p></section>;
  if (!profile) return <section className="container page"><h1>Профиль временно недоступен</h1><p role="alert">{error}</p><button className="button" onClick={load}>Повторить загрузку</button></section>;
  return <>
    {error && <section className="container profile-error notice" role="alert"><p>{error}</p><button className="button secondary" onClick={load}>Загрузить актуальный профиль</button></section>}
    {saving && <p role="status" className="container small">Сохраняем настройки…</p>}
    <fieldset className="server-profile-content" disabled={saving} aria-label="Персональные настройки" aria-busy={saving}>
      {route === "plan" ? <Dashboard profile={{ ...profile, email: user.email }} update={update} />
        : <Profile profile={{ ...profile, email: user.email }} update={update} onDelete={() => { try { localStorage.removeItem(legacyKey); } catch {} }} />}
    </fieldset>
  </>;
}
