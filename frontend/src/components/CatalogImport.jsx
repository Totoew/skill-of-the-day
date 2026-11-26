import React, { useState } from "react";
import { request } from "../auth";
import { useCatalog } from "./CatalogProvider";

const call = (path, body, options = {}) => request(path, body, { ...options, service: "skills", authenticated: true });

export default function CatalogImport() {
  const { catalog, reload } = useCatalog();
  const [fileName, setFileName] = useState(""), [document, setDocument] = useState(null),
    [preview, setPreview] = useState(null), [busy, setBusy] = useState(false),
    [error, setError] = useState(null), [message, setMessage] = useState("");
  function fail(failure) { setError({ message: failure.message, details: failure.details || [] }); }
  async function choose(event) {
    const file = event.target.files?.[0];
    setDocument(null); setPreview(null); setError(null); setMessage(""); setFileName(file?.name || "");
    if (!file) return;
    if (!file.name.toLowerCase().endsWith(".json")) return fail(new Error("Выберите файл с расширением .json."));
    if (file.size > 1024 * 1024) return fail(new Error("Размер файла не должен превышать 1 МБ."));
    setBusy(true);
    try {
      const value = JSON.parse((await file.text()).replace(/^\uFEFF/, ""));
      if (!value || typeof value !== "object" || Array.isArray(value))
        return fail(new Error("В корне JSON должен быть объект с полями schemaVersion и skills."));
      setDocument(value);
    }
    catch { fail(new Error("Не удалось прочитать JSON. Проверьте скобки, запятые и кодировку UTF-8.")); }
    finally { setBusy(false); }
  }
  async function check() {
    setBusy(true); setError(null); setMessage(""); setPreview(null);
    try { setPreview(await call("/admin/import/preview", document)); }
    catch (failure) { fail(failure); }
    finally { setBusy(false); }
  }
  async function save() {
    setBusy(true); setError(null);
    try {
      const result = await call("/admin/import", { catalog: document, version: preview.version });
      setMessage(`Импорт завершён. Добавлено: ${result.added}; обновлено: ${result.updated}; без изменений: ${result.unchanged}.`);
      setPreview(null);
      try { await reload(); }
      catch { fail(new Error("Файл сохранён на сервере, но обновить список не удалось. Обновите страницу.")); }
    } catch (failure) { fail(failure); setPreview(null); }
    finally { setBusy(false); }
  }
  async function download(path, name) {
    setBusy(true); setError(null);
    try {
      const value = await call(path, undefined, { method: "GET" });
      const url = URL.createObjectURL(new Blob([JSON.stringify(value, null, 2) + "\n"], { type: "application/json;charset=utf-8" }));
      const link = window.document.createElement("a"); link.href = url; link.download = name; link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (failure) { fail(failure); }
    finally { setBusy(false); }
  }
  return <section className="panel catalog-import" aria-labelledby="catalog-import-title">
    <h2 id="catalog-import-title">Справочник навыков · импорт JSON</h2>
    <p>В справочнике: {catalog.skills.length} навыков. Версия: {catalog.version}.</p>
    <p>Загрузите навыки, синонимы и признак базового навыка по направлениям. Импорт дополняет справочник и обновляет записи по коду. Отсутствующие записи сохраняются.</p>
    <fieldset className="catalog-import-controls" disabled={busy} aria-busy={busy}>
      <div className="actions">
        <button type="button" className="button secondary" onClick={() => download("/admin/template", "skill-catalog.example.json")}>Скачать пример JSON</button>
        <button type="button" className="button secondary" onClick={() => download("/admin/export", "skill-catalog.json")}>Выгрузить справочник</button>
      </div>
      <label>JSON-файл справочника (до 1 МБ)<input type="file" accept=".json,application/json" onChange={choose} /></label>
      <p className="small muted">Направления: analyst, csharp, qa. Названия существующих навыков сохраняются, чтобы не потерять связь с профилями. Регионы и уровни в этот файл не входят.</p>
      {fileName && <p className="catalog-file-name">Файл: {fileName}</p>}
      <button type="button" className="button secondary" disabled={!document} onClick={check}>Проверить файл</button>
      {preview && <div className="notice catalog-preview" role="status">
        <strong>Файл проверен. Изменения ещё не сохранены.</strong>
        <p>Будет добавлено: {preview.added}; обновлено: {preview.updated}; без изменений: {preview.unchanged}. Всего после импорта: {preview.totalSkills}.</p>
        <details><summary>Навыки в файле ({document.skills.length})</summary><ul>{document.skills.map((skill) => <li key={skill.code}>{skill.name} · {skill.code}{skill.basicFor.length ? ` · базовый: ${skill.basicFor.join(", ")}` : ""}</li>)}</ul></details>
        <button type="button" className="button" onClick={save}>Импортировать справочник</button>
      </div>}
    </fieldset>
    {busy && <p role="status">Обрабатываем файл…</p>}
    {message && <p className="notice" role="status">{message}</p>}
    {error && <div className="notice" role="alert"><p>{error.message}</p>{error.details.length > 0 && <ul>{error.details.map((detail, i) => <li key={i}>{detail}</li>)}</ul>}</div>}
  </section>;
}
