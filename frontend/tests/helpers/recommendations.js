// Browser contract fixture. The previous JS calculator remains an independent oracle for the C# API tests.
import { getScope, layers, coverage, minimumDeficit, skillContext, basisJobs, directions } from "../../src/data.js";

import { buildPlan } from "./legacy-calculator.js";

export function planFixture(profile, catalog, layer = null, suppliedJobs) {
  const all = suppliedJobs || getScope(profile), values = layers(all, profile.skills);
  const active = values.some(([value]) => value === layer) ? layer : null;
  const jobs = active === null ? all : all.filter(job => job.required.filter(skill => !profile.skills.includes(skill)).length === active);
  const bases = catalog.skills.filter(skill => skill.basicFor.includes(profile.direction)).map(skill => skill.name);
  const remaining = bases.filter(skill => !profile.skills.includes(skill));
  const noSkills = !directions[profile.direction].skills.some(skill => profile.skills.includes(skill));
  const metric = (skill, step) => {
    const context = skillContext(profile.skills, step, skill), before = coverage(jobs, context), ids = basisJobs(jobs, context, skill).map(job => job.id);
    return { skill, category: "required", before, after: before + ids.length, gain: ids.length, points: jobs.length ? 100 * ids.length / jobs.length : 0, basisIds: ids };
  };
  const steps = noSkills ? [] : buildPlan(jobs, profile.skills, profile.direction, bases).map(step => ({ ...step, metrics: step.skills.map(skill => metric(skill, step)) }));
  return { profileVersion: profile.version || 0, catalogVersion: 1, source: "demo", snapshotId: "demo-2026-10-20", snapshotDate: "2026-10-20T00:00:00Z",
    updateIntervalDays: 5, scopeCount: all.length, total: jobs.length, selectedLayer: active, layers: values.map(([layer, count]) => ({ layer, count })),
    covered: coverage(jobs, profile.skills), minimumDeficit: minimumDeficit(jobs, profile.skills), basicSkills: bases, remainingBasics: remaining,
    noSkills, basics: remaining.map(skill => metric(skill)), steps, jobs, excludedVacancies: 0 };
}
